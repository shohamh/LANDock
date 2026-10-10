using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LANDock.Core;

namespace LANDock.Host;

public sealed record InstanceStatus(string Label, int Port, bool Up, string? Display);

/// <summary>
/// The laptop side. While it runs: the Apollo instances (restarted if one exits), a short pairing window with a random PIN
/// that is handed to pairing requests, full permissions for paired clients, the display layout (laptop | M1 | M2 | TV),
/// and a keep-awake request. Stop() ends the streams, removes leftover virtual displays and releases everything.
/// </summary>
public sealed class HostService : IDisposable
{
    private readonly Settings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, Process> _procs = new();
    private readonly Dictionary<string, bool> _up = new();
    private CancellationTokenSource? _cts;
    private Thread? _awakeThread;
    private Task? _supervisor, _acceptor, _layout;

    public HostService(Settings settings) => _settings = settings;

    public bool Running { get; private set; }
    public bool Starting { get; private set; }
    /// <summary>The pairing PIN of this session (shown to the user, never written to the log).</summary>
    public string Pin { get; private set; } = "";
    public DateTime PairingUntil { get; private set; } = DateTime.MinValue;
    public bool PairingOpen => Running && DateTime.Now < PairingUntil;
    public event Action? Changed;

    private void Raise() { try { Changed?.Invoke(); } catch { } }

    public async Task StartAsync(CancellationToken external = default)
    {
        if (Running || Starting) return;
        Starting = true; Raise();
        try
        {
            HostSetup.Ensure();
            if (!VddTool.DriverPresent()) throw new InvalidOperationException(VddTool.DriverHelp);
            StopLeftovers();
            Cleanup();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            var ct = _cts.Token;

            if (_settings.Host.KeepAwake) StartKeepAwake(ct);

            Log.Info("starting the Apollo instances (a supervisor restarts any that exit)");
            _supervisor = Task.Run(() => Supervise(ct));
            foreach (var inst in HostPlanning.Instances)
            {
                var ok = false;
                for (var i = 0; i < 90 && !ok && !ct.IsCancellationRequested; i++)
                {
                    ok = await IsUpAsync(inst.Port, ct);
                    if (!ok) await Task.Delay(1000, ct);
                }
                if (!ok) throw new InvalidOperationException($"instance {inst.Label} did not come up (see {Paths.InstanceLog(inst.Key)})");
                lock (_gate) _up[inst.Key] = true;
                Log.Info($"instance {inst.Label} up on port {inst.Port}");
                Raise();
            }

            Pin = HostPlanning.NewPin();
            PairingUntil = DateTime.Now.AddMinutes(_settings.Host.PairingWindowMinutes);
            Running = true;
            _acceptor = Task.Run(() => Accept(ct));
            _layout = Task.Run(() => WatchLayout(ct));
            Log.Info($"host ready; new desktop PCs can pair for the next {_settings.Host.PairingWindowMinutes} minutes");
        }
        catch
        {
            await StopAsync();
            throw;
        }
        finally { Starting = false; Raise(); }
    }

    /// <summary>Opens the pairing window again with a new PIN (for a desktop PC that is not paired yet).</summary>
    public void NewPairingWindow()
    {
        if (!Running) return;
        Pin = HostPlanning.NewPin();
        PairingUntil = DateTime.Now.AddMinutes(_settings.Host.PairingWindowMinutes);
        Log.Info($"pairing window opened for {_settings.Host.PairingWindowMinutes} minutes with a new PIN");
        Raise();
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts == null && _procs.Count == 0) { Running = false; return; }
        Log.Info("stopping the host");
        Running = false; Raise();

        // end the streams politely first: that removes their virtual displays
        foreach (var inst in HostPlanning.Instances)
        {
            try
            {
                using var api = new ApolloApi(inst.Port);
                using var to = new CancellationTokenSource(3000);
                await api.CloseAppAsync(to.Token);
            }
            catch { }
        }

        cts?.Cancel();
        try { await Task.WhenAll(new[] { _supervisor, _acceptor, _layout }.Where(t => t != null)!.Select(t => t!.WaitAsync(TimeSpan.FromSeconds(8)))); }
        catch { }

        lock (_gate)
        {
            foreach (var p in _procs.Values) { try { if (!p.HasExited) p.Kill(true); } catch { } }
            _procs.Clear(); _up.Clear();
        }
        StopLeftovers();
        await Task.Delay(1500);
        Cleanup();
        cts?.Dispose(); _cts = null; Pin = "";
        Log.Info("host stopped");
        Raise();
    }

    // ---- state for the UI and the updater -----------------------------------------------------------------------------

    public List<InstanceStatus> Snapshot()
    {
        var res = new List<InstanceStatus>();
        foreach (var inst in HostPlanning.Instances)
        {
            bool up; lock (_gate) up = _up.TryGetValue(inst.Key, out var u) && u;
            res.Add(new InstanceStatus(inst.Label, inst.Port, up, ActiveDisplayOf(inst)));
        }
        return res;
    }

    /// <summary>True when no stream is connected (a safe moment to replace the host files).</summary>
    public bool IsIdle() => HostPlanning.Instances.All(i => ActiveDisplayOf(i) == null);

    public static string? ActiveDisplayOf(Instance inst) => HostPlanning.ActiveDisplay(ReadTailLines(Paths.InstanceLog(inst.Key), 512 * 1024));

    // ---- background loops ---------------------------------------------------------------------------------------------

    private async Task Supervise(CancellationToken ct)
    {
        foreach (var inst in HostPlanning.Instances)
        {
            Launch(inst);
            try { await Task.Delay(4000, ct); } catch (OperationCanceledException) { return; }   // one after another: a startup crash was seen when all of them probed encoders at once
        }
        while (!ct.IsCancellationRequested)
        {
            foreach (var inst in HostPlanning.Instances)
            {
                Process? p; lock (_gate) _procs.TryGetValue(inst.Key, out p);
                if (p != null && p.HasExited)
                {
                    Log.Info($"instance {inst.Label} exited with code {p.ExitCode}; restarting");
                    lock (_gate) _up[inst.Key] = false;
                    try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
                    Launch(inst);
                }
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Launch(Instance inst)
    {
        Directory.CreateDirectory(Paths.InstanceDir(inst.Key));
        var psi = new ProcessStartInfo(Paths.Sunshine)
        {
            WorkingDirectory = Paths.HostBin, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        psi.ArgumentList.Add(Paths.Fwd(Paths.InstanceConf(inst.Key)));
        var p = Process.Start(psi)!;
        var sink = new StreamWriter(new FileStream(Paths.InstanceOut(inst.Key), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) try { lock (sink) sink.WriteLine(e.Data); } catch { } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) try { lock (sink) sink.WriteLine(e.Data); } catch { } };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        p.Exited += (_, _) => { try { sink.Dispose(); } catch { } };
        p.EnableRaisingEvents = true;
        lock (_gate) _procs[inst.Key] = p;
        Log.Info($"started instance {inst.Label} (pid {p.Id})");
    }

    private async Task Accept(CancellationToken ct)
    {
        var apis = HostPlanning.Instances.ToDictionary(i => i.Port, i => new ApolloApi(i.Port));
        var seen = new HashSet<string>();
        var lastGrant = DateTime.MinValue;
        var wasOpen = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.Now < PairingUntil)
                {
                    wasOpen = true;
                    // all instances at once: a pairing request that is waiting for its PIN must not wait for another instance's answer
                    var pin = Pin;
                    await Task.WhenAll(apis.Select(async kv =>
                    {
                        try { await kv.Value.SubmitPinAsync(pin, $"desktop-{kv.Key}", ct); } catch { }   // none pending, or the instance is not up yet
                    }));
                    if (ct.IsCancellationRequested) return;
                }
                else if (wasOpen)
                {
                    wasOpen = false;
                    Log.Info("pairing window closed: no new desktop PC can pair until a new window is opened");
                    Raise();
                }
                if ((DateTime.Now - lastGrant).TotalSeconds > 6)
                {
                    lastGrant = DateTime.Now;
                    foreach (var (port, api) in apis)
                    {
                        List<(string uuid, string name, long perm)> clients;
                        try { clients = await api.ListClientsAsync(ct); } catch (OperationCanceledException) { return; } catch { continue; }
                        foreach (var c in clients)
                        {
                            if (seen.Add(c.uuid)) Log.Info($"port {port}: client '{c.name}' is paired");
                            if (c.perm != ApolloApi.FullPermission)
                                try { await api.SetPermissionsAsync(c.uuid, c.name, ct); Log.Info($"port {port}: gave '{c.name}' full permissions"); }
                                catch (OperationCanceledException) { return; }
                                catch (Exception ex) { Log.Error($"port {port}: could not update '{c.name}'", ex); }
                        }
                    }
                }
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
            }
        }
        finally { foreach (var a in apis.Values) a.Dispose(); }
    }

    private async Task WatchLayout(CancellationToken ct)
    {
        var last = "";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var screens = VddTool.Screens();
                var todo = HostPlanning.PlanLayout(screens, HostPlanning.Instances.Select(ActiveDisplayOf));
                if (todo.Count > 0)
                {
                    var msg = $"layout {string.Join(' ', todo)} -> {VddTool.Layout(todo).Split('\n').LastOrDefault()?.Trim()}";
                    if (msg != last) { Log.Info(msg); last = msg; }
                }
            }
            catch (Exception ex) { Log.Error("layout watcher", ex); }
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void StartKeepAwake(CancellationToken ct)
    {
        // holds "display required" for the life of this thread: the laptop does not dim or lock while the desktop PC is connected.
        // It changes no system setting; when the thread ends Windows goes back to normal.
        const uint flags = Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED;
        _awakeThread = new Thread(() =>
        {
            Native.SetThreadExecutionState(flags);
            while (!ct.WaitHandle.WaitOne(20_000)) Native.SetThreadExecutionState(flags);
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        }) { IsBackground = true, Name = "keep-awake" };
        _awakeThread.Start();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static async Task<bool> IsUpAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            var text = await http.GetStringAsync($"http://127.0.0.1:{port}/serverinfo", ct);
            return text.Contains("hostname");
        }
        catch (Exception ex)
        {
            if (_probeLogged.Add(port)) Log.Info($"port {port} is not answering yet ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    private static readonly HashSet<int> _probeLogged = new();

    /// <summary>Ends Apollo processes that run from OUR host folder (left over from a crash); other Apollo/Sunshine installs are untouched.</summary>
    public static void StopLeftovers()
    {
        foreach (var p in Process.GetProcessesByName("sunshine"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && path.StartsWith(Paths.HostBin, StringComparison.OrdinalIgnoreCase)) { p.Kill(true); p.WaitForExit(5000); }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Every client uuid that ever paired with an instance. A client's virtual display is created under its uuid.</summary>
    public static List<string> ClientUuids()
    {
        var found = new SortedSet<string>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    if (o["uuid"] is JsonValue v && v.TryGetValue<string>(out var u) && Regex.IsMatch(u, "^[0-9A-Fa-f-]{36}$")) found.Add(u);
                    foreach (var kv in o) Walk(kv.Value);
                    break;
                case JsonArray a:
                    foreach (var x in a) Walk(x);
                    break;
            }
        }
        foreach (var inst in HostPlanning.Instances)
        {
            try { Walk(JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.InstanceDir(inst.Key), "state.json")))); } catch { }
        }
        return found.ToList();
    }

    /// <summary>Removes virtual displays nobody owns any more (for instance after an instance was killed mid-stream).</summary>
    public static string Cleanup()
    {
        var res = VddTool.Remove(ClientUuids());
        Log.Info("virtual display cleanup: " + res);
        return res;
    }

    private static string[] ReadTailLines(string file, int bytes)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > bytes) fs.Seek(-bytes, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n');
        }
        catch { return Array.Empty<string>(); }
    }

    public void Dispose() { try { StopAsync().GetAwaiter().GetResult(); } catch { } }
}
