using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LANDock.Core;
using LANDock.Discovery;
using LANDock.Ultrasound;
using LANDock.Update;

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
    private ControlServer? _control;
    private readonly LanAdvertiser _advertiser = new();
    /// <summary>The desktop whose pairing is in progress: its Moonlight pairings are named after it (so Forget can find them).</summary>
    private string? _pairingName;
    /// <summary>The size each display was last agreed at (by name), so the laptop knows what each stream is serving.</summary>
    private readonly Dictionary<string, StreamAgreement> _agreed = new(StringComparer.OrdinalIgnoreCase);

    public HostService(Settings settings) => _settings = settings;

    public bool Running { get; private set; }
    public bool Starting { get; private set; }
    /// <summary>The pairing PIN of this session (shown to the user, never written to the log).</summary>
    public string Pin { get; private set; } = "";
    public DateTime PairingUntil { get; private set; } = DateTime.MinValue;
    public bool PairingOpen => Running && DateTime.Now < PairingUntil;
    public event Action? Changed;

    private void Raise() { try { Changed?.Invoke(); } catch { } }

    // one host at a time on this PC (the tray's host or a "host up" window): the mutex is held while the host runs, and "host down"
    // waits for it to be released. The stop signal ("host down") ends the host wherever it runs.
    private ManualResetEventSlim? _holdRelease;
    private EventWaitHandle? _stopSignal;

    /// <summary>
    /// Takes the one-host mutex and keeps it on a thread of its own until the host stops: a mutex can only be released by the thread
    /// that took it, and the host stops on another thread. Returns false when another host holds it.
    /// </summary>
    private Task<bool> AcquireInstance(string name)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, name);
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }   // the last host ended without releasing it: it is ours now
                answer.TrySetResult(owned);
                if (!owned) return;
                release.Wait();
                try { mutex.ReleaseMutex(); } catch { }
            }
            catch (Exception ex) { answer.TrySetException(ex); }
        }) { IsBackground = true, Name = "LANDock host instance" };
        holder.Start();
        _holdRelease = release;
        return answer.Task;
    }

    private void ReleaseInstance()
    {
        _holdRelease?.Set();   // the holder thread releases the mutex now; "host down" sees it released
        _holdRelease = null;
    }

    public async Task StartAsync(CancellationToken external = default)
    {
        if (Running || Starting) return;
        Starting = true; Raise();
        try
        {
            if (!await AcquireInstance(@"Local\LANDock.Host." + Paths.InstanceId))
                throw new InvalidOperationException("a host is already running (the LANDock tray or another window)");
            HostSetup.Ensure();
            if (!VddTool.DriverPresent()) throw new InvalidOperationException(VddTool.DriverHelp);
            StopLeftovers();
            Cleanup();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            var ct = _cts.Token;

            // "LANDock host down" sets this event; the host then stops itself (also when it runs in the tray)
            _stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\LANDock.HostStop");
            _stopSignal.Reset();
            var signal = _stopSignal;
            // checked every half second rather than waited on: the stop must not depend on one wait that can end early
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (signal.WaitOne(0))
                        {
                            Log.Info($"host down requested (running: {Running})");
                            if (Running) _ = StopAsync();
                            return;
                        }
                        await Task.Delay(500, ct);
                    }
                    Log.Info("host down watch ended: the host was stopped");
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    Log.Info($"host down watch ended ({ex.GetType().Name})");
                }
                catch (Exception ex)
                {
                    Log.Error("host down watch", ex);
                }
            });
            Log.Info("watching for \"host down\"");

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
            var port = ControlServer.ChoosePort(_settings.Host.ControlPort);
            if (port != _settings.Host.ControlPort) { _settings.Host.ControlPort = port; _settings.Save(); }
            _control = new ControlServer(this);
            await _control.StartAsync(port, ct);

            await ApplyPendingForgetsAsync(ct);
            Running = true;
            _advertiser.Start(BuildAdvert());
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }
                    try { _advertiser.Update(BuildAdvert()); } catch (Exception ex) { Log.Error("advert", ex); }
                }
            }, ct);
            _acceptor = Task.Run(() => Accept(ct));
            _layout = Task.Run(() => WatchLayout(ct));
            Log.Info($"host ready; new desktop PCs can pair for the next {_settings.Host.PairingWindowMinutes} minutes");
            // "host down" that came while the host was still starting: the watcher could not stop a host that was not running yet
            if (_stopSignal is { } asked && asked.WaitOne(0))
            {
                Log.Info("host down was asked while the host started: stopping it");
                await StopAsync();
            }
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
        if (cts == null && _procs.Count == 0) { Running = false; ReleaseInstance(); return; }
        Log.Info("stopping the host");
        Running = false; Raise();

        // end the streams politely first: that removes their virtual displays
        foreach (var inst in Launched())
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
        _advertiser.Stop();
        if (_control != null) { await _control.StopAsync(); _control = null; }
        _agreed.Clear();
        lock (_gate) _launched.Clear();
        _displayScreen.Clear();
        _latestScreens = null;
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
        RestoreDisplays();   // the display setup from before the connection comes back now that the virtual displays are gone
        cts?.Dispose(); _cts = null; Pin = "";
        _stopSignal?.Dispose(); _stopSignal = null;
        ReleaseInstance();
        Log.Info("host stopped");
        Raise();
    }

    // ---- the handshake (see ControlServer) -------------------------------------------------------------------------

    public ControlIdentity Identity() => new(HostIdentity.HostId(), Environment.MachineName, AppInfo.Version,
        HostIdentity.Fingerprint(), PairingOpen, _settings.Host.ControlPort);

    /// <summary>Pairs a desktop with the PIN of the current pairing window. Returns the token the desktop keeps.</summary>
    public (bool ok, string? token, string? reason) Pair(string? pin, string? client, string? ultraId = null)
    {
        if (!Running) return (false, null, "the host is not running");
        if (!PairingOpen) return (false, null, "pairing is closed on the laptop: press New pairing PIN there");
        if (string.IsNullOrEmpty(pin) || pin != Pin) return (false, null, "wrong PIN");
        var name = string.IsNullOrWhiteSpace(client) ? "desktop" : client.Trim();
        _pairingName = name;
        var token = HostIdentity.Issue(name, ultraId ?? "");
        Log.Info($"desktop '{name}' paired over the control port");
        Raise();
        return (true, token, null);
    }

    public bool IsPaired(string token) => Running && HostIdentity.IsKnown(token);

    /// <summary>What the LAN is told: who this laptop is, its control port, pairing state, and the paired desktops it heard
    /// through ultrasound in the last minute (their id, how loud, how long ago).</summary>
    public Advert BuildAdvert()
    {
        var now = DateTime.Now;
        var heard = new List<(string id, double db, double ageSeconds)>();
        foreach (var d in HostIdentity.Desktops())
        {
            if (string.IsNullOrEmpty(d.UltraId) || Heard.Last(d.UltraId) is not { } h) continue;
            var age = (now - h.at).TotalSeconds;
            if (age <= 60) heard.Add((d.UltraId, h.db, age));
        }
        var starts = new List<string>();
        lock (_gate)
        {
            foreach (var kv in _startRequests.ToList())
            {
                if (kv.Value > now) starts.Add(kv.Key);
                else _startRequests.Remove(kv.Key);
            }
        }
        return new Advert(HostIdentity.HostId(), Environment.MachineName, AppInfo.Version, _settings.Host.ControlPort,
            PairingOpen ? "open" : "closed", heard, starts);
    }

    /// <summary>
    /// Forgets a desktop: its token stops working, and its Moonlight pairings on every stream are removed (so it cannot stream
    /// either). Moonlight pairings can only be removed while the host runs; otherwise they go when the host next starts with the
    /// desktop forgotten.
    /// </summary>
    public async Task<int> ForgetDesktopAsync(string name, CancellationToken ct = default)
    {
        HostIdentity.Forget(name);
        var removed = 0;
        if (Running) removed = await UnpairNamedAsync(name, ct);
        else
        {
            // the Moonlight pairings are removed when the host next starts (see StartAsync)
            Directory.CreateDirectory(Paths.HostState);
            File.AppendAllText(PendingForgetFile, name + Environment.NewLine);
        }
        Log.Info($"forgot desktop '{name}': token revoked" + (Running ? $", {removed} Moonlight pairing(s) removed" : ", Moonlight pairings follow at the next start"));
        Raise();
        return removed;
    }

    private static string PendingForgetFile => Path.Combine(Paths.HostState, "pending-forget.txt");

    /// <summary>Removes the Moonlight pairings that were made by a desktop, on every stream.</summary>
    private async Task<int> UnpairNamedAsync(string name, CancellationToken ct)
    {
        var removed = 0;
        foreach (var inst in Launched())
        {
            using var api = new ApolloApi(inst.Port);
            try
            {
                foreach (var c in await api.ListClientsAsync(ct))
                    if (c.name.Equals(name, StringComparison.OrdinalIgnoreCase)) { await api.UnpairAsync(c.uuid, ct); removed++; }
            }
            catch (Exception ex) { Log.Error($"forget {name} on {inst.Label}", ex); }
        }
        return removed;
    }

    /// <summary>Applies the forgets that were made while the host was not running.</summary>
    private async Task ApplyPendingForgetsAsync(CancellationToken ct)
    {
        if (!File.Exists(PendingForgetFile)) return;
        var names = File.ReadAllLines(PendingForgetFile).Where(n => n.Trim() != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var n in names) Log.Info($"removing the Moonlight pairings of forgotten desktop '{n}': {await UnpairNamedAsync(n, ct)} removed");
        File.Delete(PendingForgetFile);
    }

    /// <summary>The streams this host serves: name and the Moonlight port for each.</summary>
    public List<StreamAgreement> StreamList() =>
        HostPlanning.AllInstances.Select(i => new StreamAgreement(i.Label, i.Port, 0, 0, 0)).ToList();

    /// <summary>
    /// Agrees the size of each requested display. The desktop asks for the size of the monitor it shows the display on; the
    /// host keeps it inside what it can serve (at most 4K, 30 or 60 frames per second). An empty request just lists the streams.
    /// </summary>
    /// <summary>
    /// Agrees the size of each requested display for the desktop that holds this token. The desktop's choices for its displays
    /// apply: a display that is off for it is left out (not an error), and the size is kept within its largest size and frame rate.
    /// </summary>
    public (bool ok, List<StreamAgreement> agreed, string? reason) Agree(List<StreamOffer> offers, string token)
    {
        if (offers.Count == 0) return (true, StreamList(), null);
        var choices = HostIdentity.DisplaysOfToken(token);
        var res = new List<StreamAgreement>();
        foreach (var o in offers)
        {
            var inst = HostPlanning.AllInstances.FirstOrDefault(i => i.Label.Equals(o.Name, StringComparison.OrdinalIgnoreCase));
            if (inst == null) return (false, new(), $"unknown display '{o.Name}'");
            var policy = DisplayPolicies.Choose(choices, o.ScreenId, inst.Label);
            if (!policy.Enabled)
            {
                Log.Info($"display {inst.Label}: off for this desktop, not offered");
                continue;
            }
            var w = Math.Min(Math.Max(o.Width, 640), policy.MaxWidth) & ~1;
            var h = Math.Min(Math.Max(o.Height, 480), policy.MaxHeight) & ~1;
            var fps = o.Fps >= 45 && policy.MaxFps >= 60 ? 60 : 30;
            var a = new StreamAgreement(inst.Label, inst.Port, w, h, fps);
            _agreed[inst.Label] = a;
            if (o.ScreenId != null) _displayScreen[inst.Label] = o.ScreenId; else _displayScreen.Remove(inst.Label);
            res.Add(a);
            Log.Info($"display {inst.Label}: agreed {w}x{h} at {fps} fps (port {inst.Port})");
        }
        return (true, res, null);
    }

    /// <summary>The last size agreed for each display (for the Host tab).</summary>
    public IReadOnlyDictionary<string, StreamAgreement> Agreed => _agreed;

    /// <summary>The last size agreed for the display that shows this desktop screen (null when no display shows it).</summary>
    public StreamAgreement? AgreedOn(string screenId)
    {
        var label = _displayScreen.FirstOrDefault(kv => kv.Value.Equals(screenId, StringComparison.OrdinalIgnoreCase)).Key;
        return label != null && _agreed.TryGetValue(label, out var a) ? a : null;
    }

    private readonly Dictionary<string, IReadOnlyList<MonitorOffer>> _desktopScreens = new();
    private readonly Dictionary<string, DateTime> _lastSession = new(StringComparer.Ordinal);      // by token hash
    private readonly Dictionary<string, DateTime> _startRequests = new(StringComparer.OrdinalIgnoreCase);   // desktop id -> until

    /// <summary>A paired desktop asked for its displays (its session request): it counts as connected for a while.</summary>
    public void NoteSession(string token)
    {
        if (token == "") return;
        lock (_gate)
        {
            _lastSession[HostIdentity.Hash(token)] = DateTime.Now;
            _activeToken = token;
            _lastSessionAt = DateTime.Now;
        }
    }

    // the layout of the laptop's screens and the display setup to put back (LaptopLayout, DisplayConfig)
    private byte[]? _savedDisplays;
    private string? _activeToken;                      // the desktop that asked last
    private DateTime _lastSessionAt = DateTime.MinValue;
    private DateTime? _noDisplaysSince;
    // the instances that show a copy of the laptop's screen: instance key -> the laptop screen it captures (see HostSetup.WriteCopyConf)
    private readonly Dictionary<string, string> _copyOutput = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TimeSpan BackAfter = TimeSpan.FromSeconds(10), StillAsking = TimeSpan.FromSeconds(60), PendingStart = TimeSpan.FromMinutes(3);
    private DateTime _lastActiveAt = DateTime.MinValue;   // the last time a display ran or a desktop sent a heartbeat
    private string _lastInputs = "";

    /// <summary>The placement and the copy the laptop uses for the desktop that asked last: its own choice, else the general one.</summary>
    private (LaptopPlacement placement, DuplicateTarget duplicate) LayoutForActive() =>
        LayoutOf(_activeToken == null ? null : HostIdentity.DesktopOfToken(_activeToken));

    /// <summary>Keeps the display setup from before the first connection, to put it back when the displays are gone.</summary>
    private void SaveDisplays()
    {
        if (_savedDisplays != null) return;
        try
        {
            _savedDisplays = DisplayConfig.Save();
            Log.Info("the display setup is saved; it comes back when the laptop's displays are gone");
        }
        catch (Exception ex) { Log.Error("the display setup could not be saved", ex); }
    }

    /// <summary>Puts the display setup from before the connection back (once: the saved setup is then forgotten).</summary>
    private void RestoreDisplays()
    {
        var saved = _savedDisplays;
        _savedDisplays = null;
        _noDisplaysSince = null;
        if (saved == null) return;
        try
        {
            var ok = DisplayConfig.Restore(saved);
            Log.Info(ok ? "the display setup is back as it was before the connection" : "the display setup could not be put back (check Windows Display settings)");
        }
        catch (Exception ex) { Log.Error("the display setup could not be put back", ex); }
    }

    // when each display was last turned: Apollo sets a display's mode again when its stream starts, so a turn can be undone and is retried
    private readonly Dictionary<string, DateTime> _turnedAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TurnEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A display that shows a desktop screen of the other orientation is turned to match it. Apollo reuses a display it made for a screen
    /// of another orientation, so a portrait screen can get a landscape display: the laptop turns it.
    /// </summary>
    private void TurnDisplays(Dictionary<string, string?> names, Dictionary<string, ScreenInfo> screens)
    {
        foreach (var inst in Launched())
        {
            var name = names.TryGetValue(inst.Key, out var n) ? n : null;
            if (name == null || !screens.TryGetValue(name, out var now)) continue;
            if (!_agreed.TryGetValue(inst.Label, out var agreed)) continue;
            if (!DisplayRotation.NeedsTurn(agreed.Width, agreed.Height, now.Width, now.Height)) continue;
            if (_turnedAt.TryGetValue(name, out var last) && DateTime.Now - last < TurnEvery) continue;
            _turnedAt[name] = DateTime.Now;
            var portrait = agreed.Height > agreed.Width;
            var ok = DisplayRotation.Turn($@"\\.\{name}", portrait);
            Log.Info($"{inst.Label}: display {name} turned to {(portrait ? "portrait" : "landscape")} to match its screen ({agreed.Width}x{agreed.Height}): {(ok ? "done" : "Windows refused")}");
        }
    }

    /// <summary>When no display runs for a while and no desktop has asked lately, the display setup from before the connection comes back.</summary>
    private void WatchDisplaysGone(bool anyRunning)
    {
        var now = DateTime.Now;
        if (anyRunning) { _noDisplaysSince = null; _lastActiveAt = now; return; }
        _noDisplaysSince ??= now;
        if (RestoreDue(now, _noDisplaysSince.Value, _lastSessionAt, _lastActiveAt, _savedDisplays != null)) RestoreDisplays();
    }

    /// <summary>
    /// When the display setup comes back: no display has run for BackAfter, and the desktop has gone quiet. A desktop that asked (a
    /// session) but has not streamed yet gets PendingStart (Moonlight takes a while to start). Once something has run or a heartbeat
    /// came (lastActive after the session), it is StillAsking since that last sign of use. Pure: the times are passed in.
    /// </summary>
    public static bool RestoreDue(DateTime now, DateTime noDisplaysSince, DateTime lastSession, DateTime lastActive, bool saved)
    {
        if (!saved || now - noDisplaysSince <= BackAfter) return false;
        return lastActive > lastSession ? now - lastActive > StillAsking : now - lastSession > PendingStart;
    }

    private readonly Dictionary<string, DateTime> _lastBeat = new(StringComparer.Ordinal);   // by token hash

    /// <summary>A paired desktop says it is still connected (its heartbeat). It is also a sign that the laptop is in use.</summary>
    public void NoteHeartbeat(string token)
    {
        if (token == "") return;
        var now = DateTime.Now;
        lock (_gate)
        {
            _lastBeat[HostIdentity.Hash(token)] = now;
            _lastActiveAt = now;
        }
    }

    /// <summary>When the desktop with this token hash last sent a heartbeat (DateTime.MinValue when never).</summary>
    public DateTime LastBeatOf(string tokenHash)
    {
        lock (_gate) return _lastBeat.TryGetValue(tokenHash, out var t) ? t : DateTime.MinValue;
    }

    /// <summary>When the desktop with this token hash last asked for its displays (DateTime.MinValue when never).</summary>
    public DateTime LastSessionOf(string tokenHash)
    {
        lock (_gate) return _lastSession.TryGetValue(tokenHash, out var t) ? t : DateTime.MinValue;
    }

    /// <summary>True when the desktop with this token hash asked for its displays within the given time.</summary>
    public bool SessionWithin(string tokenHash, TimeSpan within)
    {
        lock (_gate) return _lastSession.TryGetValue(tokenHash, out var t) && DateTime.Now - t <= within;
    }

    /// <summary>
    /// Asks the desktop with this ultrasound id to start its windows now. The request rides on the laptop's advert for two minutes;
    /// the desktop starts its windows for this laptop when it sees the request.
    /// </summary>
    public void RequestStart(string desktopId)
    {
        if (desktopId == "") return;
        lock (_gate) _startRequests[desktopId] = DateTime.Now.AddMinutes(2);
        if (Running) _advertiser.Update(BuildAdvert());
    }
    private readonly Dictionary<string, string> _displayScreen = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<MonitorOffer>? _latestScreens;

    /// <summary>Keeps the screens a desktop reported in its latest session request: the layout places the displays where those
    /// screens are (plan-monitor-layout.md, step 3). A request without a screen report changes nothing.</summary>
    public void ReportMonitors(string token, IReadOnlyList<MonitorOffer>? monitors)
    {
        if (monitors == null || token == "") return;
        // what changed since the desktop's last report: a screen added or removed while connected (plan-monitor-layout.md, step 6)
        if (_desktopScreens.TryGetValue(token, out var before))
        {
            var was = before.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = monitors.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = monitors.Where(m => !was.Contains(m.Id)).Select(m => m.Id).ToList();
            var removed = before.Where(m => !now.Contains(m.Id)).Select(m => m.Id).ToList();
            if (added.Count > 0 || removed.Count > 0)
                Log.Info($"the desktop's screens changed: added [{string.Join(", ", added)}], removed [{string.Join(", ", removed)}]");
        }
        _desktopScreens[token] = monitors;
        _latestScreens = monitors;
        HostIdentity.SetScreens(token, monitors);
        Log.Info($"a desktop reported {monitors.Count} screen(s): " + string.Join(", ", monitors.Select(m => $"{m.Id} {m.Width}x{m.Height} at {m.X},{m.Y} rotation {m.Rotation}")));
    }

    /// <summary>The screens a desktop last reported, or null when it has not reported any (an older desktop).</summary>
    public IReadOnlyList<MonitorOffer>? ScreensOf(string token) => _desktopScreens.TryGetValue(token, out var m) ? m : null;

    // ---- state for the UI and the updater -----------------------------------------------------------------------------

    public List<InstanceStatus> Snapshot()
    {
        var res = new List<InstanceStatus>();
        var started = Launched();
        // the fixed displays always, and the others while a desktop uses them
        foreach (var inst in HostPlanning.AllInstances.Where(i => HostPlanning.Instances.Contains(i) || started.Contains(i)))
        {
            bool up; lock (_gate) up = _up.TryGetValue(inst.Key, out var u) && u;
            res.Add(new InstanceStatus(inst.Label, inst.Port, up, ActiveDisplayOf(inst)));
        }
        return res;
    }

    /// <summary>True when no stream is connected (a safe moment to replace the host files).</summary>
    public bool IsIdle() => Launched().All(i => ActiveDisplayOf(i) == null);

    private readonly HashSet<string> _launched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The instances that are started now: the fixed three from the start of the host, the others since a desktop asked
    /// for them (until the host stops).</summary>
    public List<Instance> Launched()
    {
        lock (_gate) return HostPlanning.AllInstances.Where(i => _launched.Contains(i.Key)).ToList();
    }

    /// <summary>
    /// Starts the instances that the desktop's displays need and that are not started yet, and waits until each answers. Returns the
    /// labels of the displays that did not come up (the handshake refuses them).
    /// </summary>
    /// <param name="copyScreenId">The desktop screen that shows a copy of the laptop's screen (null: none). Its display captures the
    /// laptop's own screen instead of a virtual display.</param>
    public async Task<List<string>> EnsureDisplaysAsync(IEnumerable<StreamOffer> offers, CancellationToken ct = default, string? copyScreenId = null)
    {
        var failed = new List<string>();
        SaveDisplays();   // before any virtual display of this connection exists
        foreach (var o in offers)
        {
            var inst = HostPlanning.AllInstances.FirstOrDefault(i => i.Label.Equals(o.Name, StringComparison.OrdinalIgnoreCase));
            if (inst == null) continue;   // Agree names it
            var output = copyScreenId != null && string.Equals(o.ScreenId, copyScreenId, StringComparison.OrdinalIgnoreCase) ? LaptopOutput() : null;
            bool fresh;
            lock (_gate) fresh = _launched.Add(inst.Key);
            if (!fresh)
            {
                // running already: it is restarted when its copy changes or its size does, and no stream of it runs now. Apollo keeps the
                // size of the display it made and a reused display cannot take another size (the driver offers the modes of its first
                // size), so an instance for another size starts afresh and makes its display again.
                var current = _copyOutput.TryGetValue(inst.Key, out var c) ? c : null;
                var resized = _agreed.TryGetValue(inst.Label, out var was) && (was.Width != o.Width || was.Height != o.Height);
                if (current == output && !resized) continue;
                // a copy waits for the stream that runs now to end; a size does not (that stream is for the old size)
                if (current != output && StreamRunningOf(inst)) { Log.Info($"{inst.Label}: its copy changes at the next connection (a stream of it runs now)"); continue; }
                if (resized && was != null) Log.Info($"{inst.Label}: size changes from {was.Width}x{was.Height} to {o.Width}x{o.Height}; the instance starts afresh");
                KillInstance(inst);
                // the driver keeps a display after its instance stops, and the new instance would take that display with its old size:
                // the display of the client is removed first, so the stream makes one at the new size
                if (resized)
                {
                    var uuids = UuidsOf(inst.Key);
                    if (uuids.Count > 0) Log.Info($"{inst.Label}: its display is removed: {VddTool.Remove(uuids).Trim()}");
                }
            }
            SetCopyMode(inst, output);
            try { Launch(inst); }
            catch (Exception ex)
            {
                Log.Error($"instance {inst.Label} could not be started", ex);
                lock (_gate) _launched.Remove(inst.Key);
                failed.Add(inst.Label);
                continue;
            }
            var up = false;
            for (var i = 0; i < 90 && !up && !ct.IsCancellationRequested; i++)
            {
                up = await IsUpAsync(inst.Port, ct);
                if (!up) await Task.Delay(1000, ct);
            }
            lock (_gate) _up[inst.Key] = up;
            if (up) Log.Info($"instance {inst.Label} up on port {inst.Port} (started for a desktop)");
            else { Log.Error($"instance {inst.Label} did not come up (see {Paths.InstanceLog(inst.Key)})", null); failed.Add(inst.Label); }
            Raise();
        }
        return failed;
    }

    public static string? ActiveDisplayOf(Instance inst) => HostPlanning.ActiveDisplay(ReadTailLines(Paths.InstanceLog(inst.Key), 512 * 1024));

    /// <summary>True while a desktop is connected to the instance (see HostPlanning.StreamRunning).</summary>
    public static bool StreamRunningOf(Instance inst) => HostPlanning.StreamRunning(ReadTailLines(Paths.InstanceLog(inst.Key), 512 * 1024));

    // ---- background loops ---------------------------------------------------------------------------------------------

    private async Task Supervise(CancellationToken ct)
    {
        foreach (var inst in HostPlanning.Instances)
        {
            lock (_gate) _launched.Add(inst.Key);
            Launch(inst);
            try { await Task.Delay(4000, ct); } catch (OperationCanceledException) { return; }   // one after another: a startup crash was seen when all of them probed encoders at once
        }
        while (!ct.IsCancellationRequested)
        {
            foreach (var inst in Launched())
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

    /// <summary>The laptop's own screen as Apollo captures it for a copy (null when the primary screen is not known).</summary>
    private static string? LaptopOutput()
    {
        var primary = VddTool.Screens().Values.FirstOrDefault(s => s.Primary);
        return primary == null ? null : @"\\.\" + primary.Name;
    }

    /// <summary>Makes the instance a copy of the laptop's screen (output given) or an ordinary display (null). Its config is written here;
    /// the instance reads it when it starts.</summary>
    private void SetCopyMode(Instance inst, string? output)
    {
        if (output == null) { lock (_gate) _copyOutput.Remove(inst.Key); return; }
        HostSetup.WriteCopyConf(inst, output);
        lock (_gate) _copyOutput[inst.Key] = output;
        Log.Info($"{inst.Label} shows a copy of the laptop's screen ({output})");
    }

    /// <summary>Stops an instance that runs (for a restart with another config). The supervisor does not restart it: it is taken off
    /// the process table first.</summary>
    private void KillInstance(Instance inst)
    {
        Process? p;
        lock (_gate) { _procs.TryGetValue(inst.Key, out p); _procs.Remove(inst.Key); _up.Remove(inst.Key); }
        try { if (p != null && !p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { }
    }

    /// <summary>The desktop screen that shows a copy of the laptop's screen for this desktop (null: none).</summary>
    public string? CopyScreenFor(HostIdentity.PairedDesktop? desktop, IReadOnlyList<MonitorOffer>? report) =>
        LaptopLayout.DuplicateScreen(report ?? new List<MonitorOffer>(), LayoutOf(desktop).duplicate)?.Id;

    /// <summary>The placement and the copy for a desktop: its own choice, else the general one.</summary>
    private (LaptopPlacement placement, DuplicateTarget duplicate) LayoutOf(HostIdentity.PairedDesktop? d) =>
        (LaptopLayout.ParsePlacement(d?.Placement ?? _settings.Host.LaptopPlacement),
         LaptopLayout.ParseDuplicate(d?.Duplicate ?? _settings.Host.LaptopDuplicate));

    private void Launch(Instance inst)
    {
        Directory.CreateDirectory(Paths.InstanceDir(inst.Key));
        var psi = new ProcessStartInfo(Paths.Sunshine)
        {
            WorkingDirectory = Paths.HostBin, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        var copy = _copyOutput.ContainsKey(inst.Key);
        psi.ArgumentList.Add(Paths.Fwd(copy ? Paths.InstanceConfCopy(inst.Key) : Paths.InstanceConf(inst.Key)));
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
        var apis = new Dictionary<int, ApolloApi>();
        var seen = new HashSet<string>();
        var lastGrant = DateTime.MinValue;
        var wasOpen = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // an instance started for a desktop after this loop began is served from now on
                foreach (var inst in Launched())
                    if (!apis.ContainsKey(inst.Port)) apis[inst.Port] = new ApolloApi(inst.Port);

                if (DateTime.Now < PairingUntil)
                {
                    wasOpen = true;
                    // all instances at once: a pairing request that is waiting for its PIN must not wait for another instance's answer
                    var pin = Pin;
                    await Task.WhenAll(apis.Select(async kv =>
                    {
                        try { await kv.Value.SubmitPinAsync(pin, _pairingName ?? $"desktop-{kv.Key}", ct); } catch { }   // none pending, or the instance is not up yet
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

    /// <summary>The desktop screen a display shows, as the desktop reported it in the last handshake (null when unknown).</summary>
    private string? ScreenShownBy(string label) => _displayScreen.TryGetValue(label, out var id) ? id : null;

    /// <summary>instance key -> the monitor id of the virtual display it shows, recorded when that display is first seen.</summary>
    private readonly Dictionary<string, string> _displayIds = new();

    /// <summary>The display each instance is showing now (null when none). Apollo's log names a display when it is created, and Windows
    /// renames displays when others go (it reuses the names), so a display is recognised by its monitor id, which stays with it.</summary>
    private Dictionary<string, string?> CurrentDisplays(IReadOnlyDictionary<string, ScreenInfo> screens)
    {
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in screens.Keys)
        {
            var id = LANDock.Client.ClientService.MonitorIdOf(@"\\.\" + name);
            if (id != "") byId[id] = name;
        }
        var res = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in Launched())
        {
            var logged = ActiveDisplayOf(inst);   // the display Apollo logged for the stream now running, or null once it has ended
            if (logged == null) { _displayIds.Remove(inst.Key); res[inst.Key] = null; continue; }
            if (!_displayIds.ContainsKey(inst.Key))
            {
                var id = LANDock.Client.ClientService.MonitorIdOf(@"\\.\" + logged);
                if (id == "") { res[inst.Key] = logged; continue; }   // not readable yet: the name as logged
                _displayIds[inst.Key] = id;
            }
            res[inst.Key] = byId.TryGetValue(_displayIds[inst.Key], out var current) ? current : null;
        }
        return res;
    }

    private async Task WatchLayout(CancellationToken ct)
    {
        var last = "";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var screens = VddTool.Screens();
                var names = CurrentDisplays(screens);
                var report = _latestScreens;
                var laptop = screens.Values.FirstOrDefault(s => s.Primary);
                var running = Launched().Where(i => names[i.Key] != null).Select(i => (Display: names[i.Key]!, ScreenId: ScreenShownBy(i.Label))).ToList();
                List<string> todo;
                if (report is { Count: > 0 } && laptop != null)
                {
                    // the desktop's screens keep their arrangement; the laptop's screen is placed as the setting says (LaptopLayout)
                    var (placement, _) = LayoutForActive();
                    todo = LaptopLayout.Compute(laptop, report, running, screens, placement);
                    var inputs = $"placement {LaptopLayout.PlacementName(placement)}, desktop screens {report.Count}, displays " +
                                 string.Join(" ", running.Select(r => $"{r.Display}->{r.ScreenId ?? "?"}"));
                    if (inputs != _lastInputs) { Log.Info($"layout: {inputs}"); _lastInputs = inputs; }
                }
                else todo = HostPlanning.PlanLayout(screens, Launched().Select(i => names[i.Key]));
                if (todo.Count > 0)
                {
                    var msg = $"layout {string.Join(' ', todo)} -> {VddTool.Layout(todo).Split('\n').LastOrDefault()?.Trim()}";
                    if (msg != last) { Log.Info(msg); last = msg; }
                }
                TurnDisplays(names, screens);
                WatchDisplaysGone(running.Count > 0);
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
        var all = new SortedSet<string>();
        foreach (var inst in HostPlanning.AllInstances) all.UnionWith(UuidsOf(inst.Key));
        return all.ToList();
    }

    /// <summary>The client uuids that paired with one instance (from its state.json): the clients whose display it shows.</summary>
    public static List<string> UuidsOf(string instanceKey)
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
        try { Walk(JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.InstanceDir(instanceKey), "state.json")))); } catch { }
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
