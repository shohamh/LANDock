using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using LANDock.Core;

namespace LANDock.Client;

/// <summary>A screen of this PC. Name is the Windows display name (such as DISPLAY1), which Windows reuses; DeviceId is the monitor's own device id, which
/// stays with the monitor (empty when it cannot be read). Key is the identity used everywhere: the device id, else the name.</summary>
public sealed record MonitorInfo(int Index, string Name, Rectangle Bounds, bool Primary, string DeviceId = "")
{
    public string Key => DeviceId == "" ? Name : DeviceId;
}
public sealed record StreamStatus(string Name, bool Running, int Pid, Rectangle Window, int Monitor);

/// <summary>
/// The desktop PC side. A saved host profile holds everything needed to connect again: the address, the pinned certificate,
/// the token, the streams and which monitor each stream goes to. Starting a host runs the handshake first (the displays are
/// agreed with the host at the sizes of their monitors), then one Moonlight window per display is placed on its monitor.
/// Moonlight has no command-line option for which monitor to use, so after the video starts the window is moved there and
/// switched to fullscreen with Moonlight's own hotkey. Every stream has its own Moonlight copy, per host, because each
/// stream is a separate "computer" (its own port) with its own pairing.
/// </summary>
public sealed class ClientService
{
    private readonly Settings _settings;
    public ClientService(Settings settings) => _settings = settings;

    private ClientSettings Cfg => _settings.Client;

    public static List<MonitorInfo> Monitors()
    {
        var i = 0;
        return Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .Select(s => new MonitorInfo(++i, s.DeviceName, s.Bounds, s.Primary, MonitorIdOf(s.DeviceName))).ToList();
    }

    /// <summary>What the handshake asks for each display: the size of the monitor it is shown on, its frame rate, and the screen.</summary>
    private static List<DisplayOffer> OffersFor(IEnumerable<StreamSettings> chosen, IReadOnlyList<MonitorInfo> mons)
    {
        return chosen.Select(s =>
        {
            var m = ScreenOf(s, mons);
            // a display whose screen is not connected is offered at the primary screen's size, without a screen
            if (m == null) { var p = mons.First(); return new DisplayOffer(s.Name, p.Bounds.Width, p.Bounds.Height, s.Fps, null); }
            return new DisplayOffer(s.Name, m.Bounds.Width, m.Bounds.Height, s.Fps, m.Key);
        }).ToList();
    }

    /// <summary>The screen a display is shown on. A display that has a screen id is shown on that screen, or on none while it is not
    /// connected (it is not moved to another screen). A display without an id (settings of older versions) uses its number.
    /// The id is the monitor's device id; a name saved by an older version is still accepted.</summary>
    public static MonitorInfo? ScreenOf(StreamSettings s, IReadOnlyList<MonitorInfo> mons) =>
        string.IsNullOrEmpty(s.ScreenId)
            ? mons.FirstOrDefault(m => m.Index == s.Monitor)
            : mons.FirstOrDefault(m => m.Key.Equals(s.ScreenId, StringComparison.OrdinalIgnoreCase) || m.Name.Equals(s.ScreenId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows a display on the screen with this number (as the Client tab lists them), keeping its id in step.</summary>
    public static void AssignMonitor(StreamSettings s, int index)
    {
        s.Monitor = index;
        s.ScreenId = Monitors().FirstOrDefault(m => m.Index == index)?.Key ?? "";
    }

    /// <summary>
    /// Automatic displays: every connected screen gets a display (one connection to the laptop) when the displays start. A screen that
    /// has one already keeps it; the others take the displays that are not in use, in the order of the laptop's displays (M1, M2, ...),
    /// and the screens are taken left to right. A display that was on a screen now gone is free for another one.
    /// </summary>
    private void SyncDisplaysToScreens(HostProfile h, IReadOnlyList<MonitorInfo> mons)
    {
        if (!_settings.Client.AutoDisplays) return;
        var covered = h.Streams.Where(s => s.Enabled && ScreenOf(s, mons) != null).Select(s => ScreenOf(s, mons)!.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slots = LANDock.Host.HostPlanning.AllInstances.Select(i => h.Streams.FirstOrDefault(s => s.Name.Equals(i.Label, StringComparison.OrdinalIgnoreCase)))
            .Where(s => s != null).Select(s => s!).ToList();
        var changed = false;
        foreach (var m in mons.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y))
        {
            if (covered.Contains(m.Key)) continue;
            var free = slots.FirstOrDefault(s => !(s.Enabled && ScreenOf(s, mons) != null));
            if (free == null) { Log.Info($"{h.Name}: {m.Name} has no free display (all {slots.Count} are in use)"); break; }
            free.Enabled = true;
            AssignMonitor(free, m.Index);
            covered.Add(m.Key);
            changed = true;
            Log.Info($"{h.Name} {free.Name}: shown on {m.Name} (automatic display)");
        }
        if (changed) _settings.Save();
    }

    /// <summary>The screens the enabled displays are shown on and that are connected now.</summary>
    public static List<MonitorInfo> ScreensInUse(HostProfile h, IReadOnlyList<MonitorInfo> mons) =>
        h.Streams.Where(s => s.Enabled).Select(s => ScreenOf(s, mons)).Where(m => m != null).Select(m => m!)
            .DistinctBy(m => m.Key).ToList();

    /// <summary>The screens that the given monitors are, as the handshake reports them (plan-monitor-layout.md, step 1).</summary>
    public static List<LANDock.Host.MonitorOffer> ScreenReport(IEnumerable<MonitorInfo> screens) =>
        screens.Select(m => new LANDock.Host.MonitorOffer(m.Key, m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height,
            ScreenRotation(m.Name), m.Primary)).ToList();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, byte[] devMode);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private struct DisplayDevice
    {
        public int cb;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    /// <summary>The device id of the monitor shown on a display (MONITOR\LEN9059\...). It stays with the monitor when Windows renames the
    /// display; empty when it cannot be read.</summary>
    public static string MonitorIdOf(string displayName)
    {
        // while Windows rearranges the displays the lookup can fail for a moment: it is tried again, and when it still fails the id
        // seen before for this display is kept (a monitor must not change its key for a moment)
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var dd = new DisplayDevice { cb = System.Runtime.InteropServices.Marshal.SizeOf<DisplayDevice>() };
            if (EnumDisplayDevices(displayName, 0, ref dd, 0) && !string.IsNullOrEmpty(dd.DeviceID))
            {
                lock (_monitorIds) _monitorIds[displayName] = dd.DeviceID;
                return dd.DeviceID;
            }
            System.Threading.Thread.Sleep(100);
        }
        Log.Info($"monitor id of {displayName} could not be read; using the id seen before");
        lock (_monitorIds) return _monitorIds.TryGetValue(displayName, out var known) ? known : "";
    }

    private static readonly Dictionary<string, string> _monitorIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The orientation of a display in degrees (0, 90, 180 or 270), from the DEVMODE that Windows keeps for it. In DEVMODEW the
    /// field dmDisplayOrientation is at byte 84: after the 32-character device name (64 bytes), the version, size and extra bytes (8),
    /// the fields (4) and the position (8).</summary>
    public static int ScreenRotation(string deviceName)
    {
        const int EnumCurrentSettings = -1, OrientationOffset = 84;
        var devMode = new byte[256];
        if (!EnumDisplaySettingsW(deviceName, EnumCurrentSettings, devMode)) return 0;
        return BitConverter.ToInt32(devMode, OrientationOffset) switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
    }

    /// <summary>The monitor a new display goes to by default: the monitors to the right of the primary one, in order.</summary>
    public static int DefaultMonitor(int position)
    {
        var mons = Monitors();
        var primary = mons.FirstOrDefault(m => m.Primary)?.Index ?? 1;
        var right = mons.Where(m => m.Index > primary).Select(m => m.Index).ToList();
        return right.Count == 0 ? 1 : right[position % right.Count];
    }

    /// <summary>The folder name of a laptop's streams: the laptop's own id, which stays the same when the desktop pairs again.
    /// Windows asks the firewall question once per program path, so the Moonlight copies must not move with each pairing.</summary>
    public static string StreamKey(HostProfile h) => string.IsNullOrWhiteSpace(h.HostId) ? h.Id : h.HostId;

    /// <summary>The Moonlight copy every display uses (one for all of them: one firewall answer, see <see cref="Paths.MoonlightCopy"/>).</summary>
    public static string StreamFolder(HostProfile h, StreamSettings s) => Paths.MoonlightCopy;
    private static string MoonlightExe(HostProfile h, StreamSettings s) => Path.Combine(StreamFolder(h, s), "Moonlight.exe");
    private static string HostAddr(HostProfile h, StreamSettings s) => $"{h.Address}:{s.Port}";
    /// <summary>The log of a display's stream (the output of its Moonlight), per laptop and display.</summary>
    private static string LogFile(HostProfile h, StreamSettings s) => Path.Combine(Paths.RunsDir, $"{StreamKey(h)}-{s.Name}.log");
    /// <summary>The ids of the Moonlight process running a display and of the command that started it, while it runs.</summary>
    private static string PidFile(HostProfile h, StreamSettings s) => Path.Combine(Paths.RunsDir, $"{StreamKey(h)}-{s.Name}.pid");

    private static void RequireMoonlight()
    {
        if (!Paths.ClientInstalled) throw new InvalidOperationException("the client component is not installed (Moonlight is missing)");
    }

    private static IEnumerable<StreamSettings> Selected(HostProfile h, string? only) =>
        h.Streams.Where(s => s.Enabled && (string.IsNullOrEmpty(only) || s.Name.Equals(only, StringComparison.OrdinalIgnoreCase)));

    public HostProfile? Host(string? nameOrId) => string.IsNullOrWhiteSpace(nameOrId)
        ? Cfg.DefaultHost
        : Cfg.Hosts.FirstOrDefault(h => h.Id == nameOrId || h.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));

    // ---- Moonlight copies ---------------------------------------------------------------------------------------------

    /// <summary>Makes sure the stream's own Moonlight copy exists and matches the installed client component.
    /// Pairing data (Moonlight.ini, in the copy) is never touched.</summary>
    public static void EnsureStream(HostProfile h, StreamSettings s)
    {
        var dir = StreamFolder(h, s);
        var marker = Path.Combine(dir, ".landock-client-version");
        var want = Paths.ReadVersion(Paths.ClientVersionFile);
        var have = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        if (File.Exists(MoonlightExe(h, s)) && have == want) return;
        Directory.CreateDirectory(dir);
        CopyTree(Paths.MoonlightSource, dir);
        File.WriteAllText(marker, want);
        Log.Info($"{h.Name} {s.Name}: Moonlight copy ready in {dir}");
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            // never replace the user's pairing data with the (empty) one of the component
            if (Path.GetFileName(f).Equals("Moonlight.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue;
            File.Copy(f, dest, true);
        }
    }

    // ---- running Moonlight --------------------------------------------------------------------------------------------

    private static async Task<string> RunMoonlightAsync(HostProfile h, StreamSettings s, IEnumerable<string> args, int timeoutSec)
    {
        var psi = new ProcessStartInfo(MoonlightExe(h, s))
        {
            WorkingDirectory = StreamFolder(h, s), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        return await so + "\n" + await se;
    }

    private async Task<bool> CanListAsync(HostProfile h, StreamSettings s) =>
        Regex.IsMatch(await RunMoonlightAsync(h, s, new[] { "list", HostAddr(h, s) }, 25), @"(?m)^VD\b");

    /// <summary>The Moonlight process running this display, from its pid file (the copy is shared by all displays, so the folder
    /// cannot tell them apart). Empty once it has ended.</summary>
    // ---- "still connected" heartbeat: the laptop keeps the notice away from a desktop that is using it ----------------------------

    /// <summary>How often the desktop tells the laptop it is still connected while a display of the laptop runs.</summary>
    public static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, CancellationTokenSource> _beating = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the heartbeat for this laptop (once): every few seconds while any of its displays runs, copy streams included. It ends by
    /// itself when no display runs any more.
    /// </summary>
    private void KeepConnected(HostProfile h)
    {
        lock (_beating)
        {
            if (_beating.ContainsKey(h.Id)) return;
            var stop = new CancellationTokenSource();
            _beating[h.Id] = stop;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested && h.Streams.Any(s => StreamProcesses(h, s).Count > 0))
                    {
                        try
                        {
                            using var c = new ControlClient(h.Address, h.ControlPort, h.Fingerprint);
                            await c.HeartbeatAsync(h.Token, stop.Token);
                        }
                        catch (ControlException) { }   // the laptop is not answering: the next beat tries again
                        await Task.Delay(BeatEvery, stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally { lock (_beating) _beating.Remove(h.Id); }
            });
        }
    }

    private static List<Process> StreamProcesses(HostProfile h, StreamSettings s)
    {
        var res = new List<Process>();
        try
        {
            var ids = File.ReadAllText(PidFile(h, s)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0 || !int.TryParse(ids[0], out var pid)) return res;
            var p = Process.GetProcessById(pid);
            if (p.ProcessName.Equals("Moonlight", StringComparison.OrdinalIgnoreCase)) res.Add(p);
            else p.Dispose();
        }
        catch { }
        return res;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The processes with this name that a process started (the Moonlight that a command interpreter runs).</summary>
    private static List<int> ChildrenNamed(int parent, string exeName)
    {
        var res = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return res;
        try
        {
            var entry = new ProcessEntry { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                if (entry.th32ParentProcessID == parent && entry.szExeFile.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    res.Add((int)entry.th32ProcessID);
        }
        finally { CloseHandle(snapshot); }
        return res;
    }

    public bool AnyRunning() => Cfg.Hosts.Any(h => h.Streams.Any(s => StreamProcesses(h, s).Count > 0));
    public bool IsRunning(HostProfile h) => h.Streams.Any(s => StreamProcesses(h, s).Count > 0);

    // ---- identity and pairing ----------------------------------------------------------------------------------------

    /// <summary>First contact: what the host at this address says about itself, and the fingerprint of its certificate.
    /// Nothing is saved; the user compares the fingerprint with the one on the laptop and then pairs.</summary>
    public static async Task<(RemoteIdentity identity, string fingerprint)> CheckHostAsync(string address, int port, string? pinnedFingerprint = null)
    {
        using var c = new ControlClient(address, port, pinnedFingerprint);
        var id = await c.IdentityAsync();
        return (id, c.SeenFingerprint ?? id.Fingerprint);
    }

    /// <summary>
    /// Pairs this PC with a host: the control port hands out a token for the PIN, then Moonlight pairs each stream with the same PIN
    /// (the laptop accepts it during its pairing window). The profile is saved with the token and the host's streams.
    /// The profile's fingerprint must already be set (CheckHostAsync, confirmed by the user).
    /// </summary>
    public async Task<bool> PairAsync(HostProfile h, string pin)
    {
        if (!Regex.IsMatch(pin ?? "", @"^\d{4}$")) throw new ArgumentException("the PIN is the 4 digits shown on the laptop");
        if (string.IsNullOrEmpty(h.Fingerprint)) throw new InvalidOperationException("check the host first: its certificate has not been confirmed");
        RequireMoonlight();
        Log.Info($"{h.Name}: pairing with {h.Address}:{h.ControlPort} (the host must be running on the laptop: it accepts new pairings for a short window)");

        RemotePairing reply;
        using (var c = new ControlClient(h.Address, h.ControlPort, h.Fingerprint))
            reply = await c.PairAsync(pin!, Environment.MachineName, _settings.Client.UltrasoundId);
        h.Token = reply.Token;
        h.HostId = reply.HostId;
        if (string.IsNullOrWhiteSpace(h.Name)) h.Name = reply.HostName;
        SyncStreams(h, reply.Streams);
        h.LastConnected = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _settings.Save();
        Log.Info($"{h.Name}: the host accepted this PC; {reply.Streams.Count} display(s) offered");

        // the handshake starts the displays this PC uses (a display the laptop does not run yet is started now), so that each
        // one listens and can be paired with Moonlight
        // every display of the laptop is paired now, not only the ones in use: a display turned on later (a screen plugged in while
        // connected, plan-monitor-layout.md step 6) then needs no PIN. The enabled displays come first.
        var mons = Monitors();
        var all = h.Streams.OrderByDescending(s => s.Enabled).ToList();
        try
        {
            using var c = new ControlClient(h.Address, h.ControlPort, h.Fingerprint);
            await c.SessionAsync(h.Token, OffersFor(all, mons), _settings.Client.UltrasoundId, ScreenReport(ScreensInUse(h, mons)));
        }
        catch (ControlException e) { Log.Error($"{h.Name}: the displays could not be started: {e.Message}", null); }

        var inUse = all.Count(s => s.Enabled && ScreenOf(s, mons) != null);
        Log.Info($"{h.Name}: pairing all {all.Count} displays of the laptop: {inUse} for this PC's screens, {all.Count - inUse} spare " +
                 "(a screen added later then needs no PIN)");
        var bad = 0;
        var done = 0;
        foreach (var s in all)
        {
            EnsureStream(h, s);
            done++;
            Log.Info($"{h.Name} {s.Name}: pairing {done} of {all.Count}...");
            if (await PairOneAsync(h, s, pin!)) Log.Info($"{h.Name} {s.Name}: paired");
            else bad++;
        }
        if (bad > 0) Log.Info($"{h.Name}: {bad} display(s) not paired with Moonlight; pair again with the PIN shown on the laptop");
        else Log.Info($"{h.Name}: all displays paired");
        return bad == 0;
    }

    /// <summary>A display that this PC did not have before: on the screen of its position, enabled when this PC has that screen.</summary>
    private static StreamSettings NewStream(string name, int position, bool enabled)
    {
        var s = new StreamSettings { Name = name, Audio = "client", Enabled = enabled };
        AssignMonitor(s, DefaultMonitor(position));
        return s;
    }

    /// <summary>Takes the host's stream list (names and ports) as the truth, keeping the monitor choices of streams that still exist.</summary>
    private void SyncStreams(HostProfile h, List<RemoteStream> remote)
    {
        var list = new List<StreamSettings>();
        // a new display is in use when this PC has a screen for it: a desktop with three screens gets three displays at first
        var screens = Monitors().Count;
        for (var i = 0; i < remote.Count; i++)
        {
            var r = remote[i];
            var s = h.Streams.FirstOrDefault(x => x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                    ?? NewStream(r.Name, i, i < screens);
            s.Name = r.Name;
            s.Port = r.Port;
            list.Add(s);
        }
        h.Streams = list;
    }

    /// <summary>
    /// Moonlight's "pair" command keeps running after the pairing succeeded, so it is watched instead of waited for:
    /// as soon as the last step went through and the laptop lists the stream's app, the pairing is done and Moonlight is ended.
    /// A pairing can fail by timing (the PIN arrived late), so it is tried up to three times.
    /// </summary>
    /// <summary>True when something listens on the port (a short connect attempt).</summary>
    private static async Task<bool> PortAnswersAsync(string address, int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync(address, port);
            return await Task.WhenAny(connect, Task.Delay(2000)) == connect && tcp.Connected;
        }
        catch { return false; }
    }

    private async Task<bool> PairOneAsync(HostProfile h, StreamSettings s, string pin)
    {
        // a display whose port does not answer cannot be paired: say so at once instead of waiting for Moonlight to time out
        if (!await PortAnswersAsync(h.Address, s.Port))
        {
            Log.Info($"{h.Name} {s.Name}: the laptop does not run this display (port {s.Port} does not answer); start the displays, then pair");
            return false;
        }
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await CanListAsync(h, s)) { if (attempt == 1) Log.Info($"{h.Name} {s.Name}: already paired and reachable"); return true; }
            Log.Info($"{h.Name} {s.Name}: pairing on port {s.Port}{(attempt > 1 ? $" (attempt {attempt})" : "")} ...");
            var output = new System.Text.StringBuilder();
            var psi = new ProcessStartInfo(MoonlightExe(h, s))
            {
                WorkingDirectory = StreamFolder(h, s), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "pair", HostAddr(h, s), "--pin", pin }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            void Collect(object _, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); }
            p.OutputDataReceived += Collect; p.ErrorDataReceived += Collect;
            p.BeginOutputReadLine(); p.BeginErrorReadLine();

            var ok = false;
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000);
                string text; lock (output) text = output.ToString();
                if (text.Contains("Critical")) break;                                        // Moonlight says it failed
                if (text.Contains("phrase=pairchallenge") && await CanListAsync(h, s)) { ok = true; break; }   // last step done and the laptop knows us
                if (p.HasExited) { ok = await CanListAsync(h, s); break; }
            }
            try { if (!p.HasExited) p.Kill(true); } catch { }
            if (ok) return true;
            string last; lock (output) last = output.ToString();
            foreach (var l in last.Split(Environment.NewLine).Where(l => Regex.IsMatch(l, "rror|fail|denied|Critical|offline")).Take(4)) Log.Info("    " + l.Trim());
            await Task.Delay(2000);
        }
        Log.Info($"{h.Name} {s.Name}: NOT paired");
        return false;
    }

    // ---- starting, stopping ------------------------------------------------------------------------------------------

    /// <summary>
    /// Connects to the host (the handshake): asks for each display at the size of the monitor it is shown on, takes the
    /// sizes and ports the host agrees to, and saves them. Then starts every display (or one) and places its window.
    /// Without a token (never paired, or the host forgot this PC) nothing starts: pair again with the PIN from the laptop.
    /// </summary>
    public async Task<bool> StartAsync(HostProfile h, string? only = null)
    {
        RequireMoonlight();
        if (!h.Paired) throw new InvalidOperationException($"{h.Name} is not paired yet: press Pair and enter the PIN shown on the laptop");
        var mons = Monitors();
        if (string.IsNullOrEmpty(only)) SyncDisplaysToScreens(h, mons);
        var chosen = Selected(h, only).ToList();
        foreach (var s in chosen.Where(s => ScreenOf(s, mons) == null)) Log.Info($"{h.Name} {s.Name}: its screen is not connected; not started");
        chosen = chosen.Where(s => ScreenOf(s, mons) != null).ToList();
        if (chosen.Count == 0) throw new InvalidOperationException($"{h.Name} has no displays on a connected screen (check the connection first)");

        var offers = OffersFor(chosen, mons);

        List<RemoteStream> agreed;
        try
        {
            using var c = new ControlClient(h.Address, h.ControlPort, h.Fingerprint);
            // every screen the user has set up for this laptop, not only the ones started now: the laptop lays out all of them
            agreed = await c.SessionAsync(h.Token, offers, _settings.Client.UltrasoundId, ScreenReport(ScreensInUse(h, mons)));
        }
        catch (ControlException e) when (e.Status == 401)
        {
            throw new InvalidOperationException($"this PC is no longer paired with {h.Name}: press Pair and enter the PIN from the laptop");
        }
        foreach (var a in agreed)
        {
            var s = h.Streams.FirstOrDefault(x => x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
            if (s == null) continue;
            s.Port = a.Port; s.Resolution = $"{a.Width}x{a.Height}"; s.Fps = a.Fps;
        }
        h.LastConnected = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _settings.Save();
        Log.Info($"{h.Name}: connected; agreed " + string.Join(", ", agreed.Select(a => $"{a.Name} {a.Width}x{a.Height}@{a.Fps}")));
        // the laptop may leave out displays that are off for this PC: those are not started
        var offered = agreed.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = chosen.Where(x => !offered.Contains(x.Name)).Select(x => x.Name).ToList();
        if (left.Count > 0) Log.Info($"{h.Name}: not offered to this PC (off in the laptop's Host tab): {string.Join(", ", left)}");
        chosen = chosen.Where(x => offered.Contains(x.Name)).ToList();
        if (chosen.Count == 0)
            throw new InvalidOperationException($"{h.Name} offers no display to this PC: turn one on in the laptop's Host tab (Displays for this desktop)");

        var allOk = true;
        foreach (var s in chosen)
        {
            var procs = StreamProcesses(h, s);
            var p = procs.FirstOrDefault();
            var launched = false;
            var hw = IntPtr.Zero;
            if (p != null) { Log.Info($"{h.Name} {s.Name}: already running, placing its window"); p.Refresh(); hw = p.MainWindowHandle; }
            else
            {
                // the stream's own Moonlight copy comes first: the pairing check runs that copy
                EnsureStream(h, s);
                if (!await CanListAsync(h, s))
                {
                    Log.Info($"{h.Name} {s.Name}: not paired with Moonlight on port {s.Port}; press Pair");
                    allOk = false; continue;
                }
                var args = new List<string> { "stream", HostAddr(h, s), "VD", "--display-mode", "windowed", "--absolute-mouse" };
                if (!string.IsNullOrWhiteSpace(s.Resolution)) args.AddRange(new[] { "--resolution", s.Resolution });
                if (s.Fps > 0) args.AddRange(new[] { "--fps", s.Fps.ToString() });
                if (s.Bitrate > 0) args.AddRange(new[] { "--bitrate", s.Bitrate.ToString() });
                args.Add(s.Audio == "host" ? "--audio-on-host" : "--no-audio-on-host");
                var argText = string.Join(' ', args.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
                // run through cmd so Moonlight's console chatter goes to a log file, on pipes of its own
                // (so the stream does not keep this program's output open)
                var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                {
                    WorkingDirectory = StreamFolder(h, s), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    Arguments = $"/c \"\"{MoonlightExe(h, s)}\" {argText} > \"{LogFile(h, s)}\" 2>&1\"",
                };
                // the redirection to the log file fails (and Moonlight never starts) when the folder is missing
                Directory.CreateDirectory(Paths.RunsDir);
                var command = Process.Start(psi)!;
                launched = true;
                // the Moonlight that the command interpreter started is this display's stream: its id is kept for the display
                for (var i = 0; i < 80 && !command.HasExited; i++)
                {
                    var moonlight = ChildrenNamed(command.Id, "Moonlight.exe").FirstOrDefault();
                    if (moonlight != 0)
                    {
                        File.WriteAllText(PidFile(h, s), $"{moonlight} {command.Id}");
                        break;
                    }
                    await Task.Delay(250);
                }
                for (var i = 0; i < 80 && hw == IntPtr.Zero; i++)
                {
                    await Task.Delay(500);
                    p = StreamProcesses(h, s).FirstOrDefault();
                    if (p != null) { p.Refresh(); hw = p.MainWindowHandle; }
                }
                if (p == null) { Log.Info($"{h.Name} {s.Name}: Moonlight did not start or exited; see {LogFile(h, s)}"); allOk = false; continue; }
                if (hw == IntPtr.Zero) { Log.Info($"{h.Name} {s.Name}: no window appeared within 40 s; see {LogFile(h, s)}"); allOk = false; continue; }
            }
            var mon = ScreenOf(s, mons);
            if (mon == null) { Log.Info($"{h.Name} {s.Name}: its screen is not connected; leaving the window where it is"); allOk = false; continue; }
            if (launched)
            {
                if (!await WaitVideoAsync(LogFile(h, s), 40)) Log.Info($"{h.Name} {s.Name}: no video within 40 s (see {LogFile(h, s)}); placing the window anyway");
                await Task.Delay(1500);
            }
            var mode = string.IsNullOrEmpty(s.DisplayMode) ? Cfg.DisplayMode : s.DisplayMode;
            var ok = await PlaceWindowAsync(p!, mon.Bounds, mode, s.WindowedRect);
            allOk &= ok;
            p!.Refresh();
            Native.GetWindowRect(p.MainWindowHandle, out var r);
            Log.Info($"{h.Name} {s.Name}: on monitor {s.Monitor} at ({r.L},{r.T}) {r.Width}x{r.Height} - " +
                     (ok ? (mode == "fullscreen" ? "fullscreen, covers the monitor" : "windowed, as configured")
                         : "could not be placed; use Ctrl+Alt+Shift+X in that window to toggle fullscreen"));
            await Task.Delay(2000);
        }
        Log.Info($"{h.Name}: done. Click once in a window to give it keyboard focus; moving the mouse between monitors moves the laptop's pointer between its displays");
        Follow(h);
        KeepConnected(h);
        return allOk;
    }

    public async Task StopAsync(HostProfile h, string? only = null)
    {
        if (only == null) StopFollowing(h);
        // a display is stopped whether or not it is turned on (a display whose screen was removed is turned off, and may run still)
        foreach (var s in h.Streams.Where(s => string.IsNullOrEmpty(only) || s.Name.Equals(only, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (File.Exists(MoonlightExe(h, s)) && s.Port > 0)
                    await RunMoonlightAsync(h, s, new[] { "quit", HostAddr(h, s) }, 20);
            }
            catch (Exception ex) { Log.Error($"{h.Name} {s.Name}: quit", ex); }
            foreach (var p in StreamProcesses(h, s)) { try { p.Kill(true); } catch { } p.Dispose(); }
            try
            {
                var ids = File.ReadAllText(PidFile(h, s)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length > 1 && int.TryParse(ids[1], out var cmd)) { try { Process.GetProcessById(cmd).Kill(true); } catch { } }
                File.Delete(PidFile(h, s));
            }
            catch { }
            Log.Info($"{h.Name} {s.Name}: stopped (the laptop removes its virtual display)");
        }
    }

    public List<StreamStatus> Status(HostProfile h)
    {
        var mons = Monitors();
        var res = new List<StreamStatus>();
        foreach (var s in h.Streams)
        {
            var p = StreamProcesses(h, s).FirstOrDefault();
            if (p == null) { res.Add(new StreamStatus(s.Name, false, 0, Rectangle.Empty, 0)); continue; }
            Native.GetWindowRect(p.MainWindowHandle, out var r);
            var rect = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
            var c = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            var on = mons.FirstOrDefault(m => m.Bounds.Contains(c))?.Index ?? 0;
            res.Add(new StreamStatus(s.Name, true, p.Id, rect, on));
        }
        return res;
    }

    /// <summary>Forgets a host on this PC: its streams stop and its saved data is removed. The host keeps its list of paired
    /// desktops; remove this PC there too if you want it to be refused.</summary>
    public async Task RemoveAsync(HostProfile h)
    {
        await StopAsync(h);
        Cfg.Hosts.Remove(h);
        if (Cfg.DefaultHostId == h.Id) Cfg.DefaultHostId = "";
        _settings.Save();
        // the Moonlight copy is shared with the other laptops and holds their pairings too, so it stays (Windows remembers its
        // firewall answer); this laptop's pairing in it is left in place and is replaced when the laptop is paired again
        foreach (var s in h.Streams)
            try { File.Delete(PidFile(h, s)); } catch { }
        Log.Info($"{h.Name}: removed from this PC");
    }

    // ---- following the screens (plan-monitor-layout.md, step 6) -----------------------------------------------------------

    /// <summary>How long a screen may be gone before its display is stopped: a screen that is unplugged for a moment is not a reason
    /// to stop its display.</summary>
    public static readonly TimeSpan MissingGrace = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, CancellationTokenSource> _following = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Follows the screens of this PC while the laptop's displays run. A screen added gets a display (the first free one, or the
    /// display it had before); a screen removed has its display stopped after <see cref="MissingGrace"/>; a screen whose size or
    /// position changed has its display restarted. The laptop is told the new screens at once. Started with the displays, and stopped
    /// when they all stop.</summary>
    public void Follow(HostProfile h)
    {
        lock (_following)
        {
            if (_following.ContainsKey(h.Id)) return;
            var cts = new CancellationTokenSource();
            _following[h.Id] = cts;
            _ = Task.Run(() => FollowAsync(h, cts.Token));
        }
    }

    public void StopFollowing(HostProfile h)
    {
        lock (_following)
        {
            if (_following.Remove(h.Id, out var cts)) cts.Cancel();
        }
    }

    /// <summary>The screens of this PC as one comparable text: a change of name, position, size or rotation changes it.</summary>
    public static string LayoutOf(IEnumerable<MonitorInfo> mons) =>
        string.Join("|", mons.Select(m => $"{m.Key}@{m.Bounds.X},{m.Bounds.Y},{m.Bounds.Width}x{m.Bounds.Height}r{ScreenRotation(m.Name)}"));

    /// <summary>What changed between two lists of screens: the screens added, the ones removed, and those whose size or position changed.</summary>
    public static (List<MonitorInfo> added, List<MonitorInfo> removed, List<MonitorInfo> changed) ScreenChanges(IReadOnlyList<MonitorInfo> before, IReadOnlyList<MonitorInfo> now)
    {
        var old = before.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        var cur = now.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        var added = now.Where(m => !old.ContainsKey(m.Key)).ToList();
        var removed = before.Where(m => !cur.ContainsKey(m.Key)).ToList();
        // a size change restarts the display (its stream has the new size); a move only changes the layout the laptop uses
        var changed = now.Where(m => old.TryGetValue(m.Key, out var o) && (o.Bounds.Width != m.Bounds.Width || o.Bounds.Height != m.Bounds.Height)).ToList();
        return (added, removed, changed);
    }

    /// <summary>The display that takes a new screen: the first display that is not showing a connected screen and is not waiting for
    /// its own screen to come back. A display that never had a screen comes first.</summary>
    public static StreamSettings? FreeStream(HostProfile h, IReadOnlyList<MonitorInfo> mons, IReadOnlyDictionary<string, DateTime> missing) =>
        h.Streams.Where(s => !missing.ContainsKey(s.Name) && !(s.Enabled && ScreenOf(s, mons) != null))
            .OrderBy(s => string.IsNullOrEmpty(s.ScreenId) ? 0 : 1).FirstOrDefault();

    private async Task FollowAsync(HostProfile h, CancellationToken ct)
    {
        var last = Monitors();
        var lastLayout = LayoutOf(last);
        var missing = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);   // display -> since when its screen is gone
        Log.Info($"{h.Name}: following this PC's screens (a change is picked up within 2 seconds)");
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            try
            {
                var now = Monitors();
                var layout = LayoutOf(now);
                if (layout != lastLayout)
                {
                    lastLayout = layout;
                    var (added, removed, changed) = ScreenChanges(last, now);
                    var moved = now.Where(m => last.Any(o => o.Key.Equals(m.Key, StringComparison.OrdinalIgnoreCase) && o.Bounds.Location != m.Bounds.Location)).ToList();
                    last = now;
                    Log.Info($"{h.Name}: screens changed: " + Describe(added, "added") + Describe(removed, "removed") + Describe(changed, "resized") + Describe(moved, "moved"));
                    // a move changes no display, but the laptop lays its displays out by the desktop's arrangement: every change is reported
                    await ApplyScreenChangesAsync(h, now, added, removed, changed, missing, ct);
                }
                await ExpireMissingAsync(h, now, missing, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error($"{h.Name}: following the screens", ex); }
        }
        Log.Info($"{h.Name}: no longer following this PC's screens");
    }

    private static string Describe(IEnumerable<MonitorInfo> screens, string what)
    {
        var list = screens.Select(m => $"{m.Name} {m.Bounds.Width}x{m.Bounds.Height} [{m.Key}]").ToList();
        return list.Count == 0 ? "" : $"{what} {string.Join(", ", list)}; ";
    }

    private async Task ApplyScreenChangesAsync(HostProfile h, IReadOnlyList<MonitorInfo> now, List<MonitorInfo> added, List<MonitorInfo> removed,
        List<MonitorInfo> changed, Dictionary<string, DateTime> missing, CancellationToken ct)
    {
        // a screen that is gone: its display waits for the grace period
        foreach (var m in removed)
            foreach (var s in h.Streams.Where(x => x.Enabled && x.ScreenId.Equals(m.Key, StringComparison.OrdinalIgnoreCase)))
                missing.TryAdd(s.Name, DateTime.Now);
        // a screen that is connected again: its display is no longer waiting
        foreach (var m in now)
            foreach (var s in h.Streams.Where(x => x.ScreenId.Equals(m.Key, StringComparison.OrdinalIgnoreCase)))
                missing.Remove(s.Name);
        // a new screen (or one that came back): its display is started; a new screen gets the first free display
        foreach (var m in added) await ShowScreenAsync(h, m, now, missing);
        // a screen whose size or position changed: its display is started again with the size it now needs
        foreach (var m in changed)
            foreach (var s in h.Streams.Where(x => x.Enabled && x.ScreenId.Equals(m.Key, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try { await StopAsync(h, s.Name); await StartAsync(h, s.Name); }
                catch (Exception ex) { Log.Error($"{h.Name} {s.Name}: restarting for the new screen", ex); }
            }
        await SendScreensAsync(h, now, ct);
    }

    private async Task ShowScreenAsync(HostProfile h, MonitorInfo m, IReadOnlyList<MonitorInfo> now, Dictionary<string, DateTime> missing)
    {
        var display = h.Streams.FirstOrDefault(x => x.ScreenId.Equals(m.Key, StringComparison.OrdinalIgnoreCase))
                      ?? FreeStream(h, now, missing);
        if (display == null)
        {
            Log.Info($"{h.Name}: {m.Name} is new, but all {h.Streams.Count} displays of the laptop are in use");
            return;
        }
        display.ScreenId = m.Key;
        display.Monitor = m.Index;
        display.Enabled = true;
        _settings.Save();
        Log.Info($"{h.Name} {display.Name}: shown on {m.Name} now");
        try { await StartAsync(h, display.Name); }
        catch (Exception ex) { Log.Error($"{h.Name} {display.Name}: starting for {m.Name}", ex); }
    }

    /// <summary>Stops the displays whose screens have been gone for the grace period; they are turned off until their screen returns.</summary>
    private async Task ExpireMissingAsync(HostProfile h, IReadOnlyList<MonitorInfo> now, Dictionary<string, DateTime> missing, CancellationToken ct)
    {
        foreach (var (name, since) in missing.ToList())
        {
            if (DateTime.Now - since < MissingGrace) continue;
            missing.Remove(name);
            var s = h.Streams.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s == null) continue;
            Log.Info($"{h.Name} {s.Name}: its screen {s.ScreenId} has been gone for {MissingGrace.TotalSeconds:0} s; the display is stopped");
            await StopAsync(h, s.Name);
            s.Enabled = false;
            _settings.Save();
            await SendScreensAsync(h, now, ct);
        }
    }

    /// <summary>Tells the laptop the screens now in use (and agrees the sizes of the enabled displays again). The laptop lays out the
    /// displays by them.</summary>
    private async Task SendScreensAsync(HostProfile h, IReadOnlyList<MonitorInfo> now, CancellationToken ct)
    {
        try
        {
            var offers = OffersFor(h.Streams.Where(s => s.Enabled && ScreenOf(s, now) != null), now);
            using var c = new ControlClient(h.Address, h.ControlPort, h.Fingerprint);
            await c.SessionAsync(h.Token, offers, _settings.Client.UltrasoundId, ScreenReport(ScreensInUse(h, now)), ct);
            Log.Info($"{h.Name}: the laptop knows the screens now in use ({ScreensInUse(h, now).Count})");
        }
        catch (Exception ex) when (ex is ControlException or HttpRequestException or TaskCanceledException) { Log.Error($"{h.Name}: could not tell the laptop about the screens", ex); }
    }

    // ---- window placement ---------------------------------------------------------------------------------------------

    private static async Task<bool> WaitVideoAsync(string logFile, int timeoutSec)
    {
        for (var i = 0; i < timeoutSec * 2; i++)
        {
            if (ReadShared(logFile).Contains("first video packet")) return true;
            await Task.Delay(500);
        }
        return false;
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    /// <summary>True when the window rectangle fills the monitor (small tolerances for borders and rounding).</summary>
    public static bool Covers(Native.RECT r, Rectangle b) =>
        Math.Abs(r.L - b.X) <= 8 && Math.Abs(r.T - b.Y) <= 8 && Math.Abs(r.Width - b.Width) <= 16 && Math.Abs(r.Height - b.Height) <= 16;

    /// <summary>Puts the window on its monitor (windowed rectangle, or fullscreen), verifies, retries.</summary>
    private static async Task<bool> PlaceWindowAsync(Process proc, Rectangle b, string mode, int[]? rect)
    {
        var (wx, wy, ww, wh) = rect is { Length: 4 } ? (rect[0], rect[1], rect[2], rect[3]) : (80, 80, 1296, 759);   // where a windowed stream sits on its monitor
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            proc.Refresh();
            var h = proc.MainWindowHandle;   // Moonlight swaps windows while connecting, so read it again every time
            Native.GetWindowRect(h, out var r);
            if (Covers(r, b))
            {
                if (mode == "fullscreen") return true;   // already where we want it
                Native.Activate(h); await Task.Delay(300); Native.ToggleFullscreen(); await Task.Delay(1500);   // leave fullscreen so it can be moved
            }
            Native.MoveWindow(h, b.X + wx, b.Y + wy, ww, wh, true); await Task.Delay(1200);
            if (mode == "fullscreen")
            {
                Native.Activate(h); await Task.Delay(500); Native.ToggleFullscreen(); await Task.Delay(1800);
                Native.GetWindowRect(h, out r);
                if (Covers(r, b)) return true;
            }
            else
            {
                Native.GetWindowRect(h, out r);
                if (Math.Abs(r.L - (b.X + wx)) <= 12 && Math.Abs(r.T - (b.Y + wy)) <= 12) return true;
            }
        }
        return false;
    }
}
