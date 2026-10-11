using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using LANDock.Core;

namespace LANDock.Client;

public sealed record MonitorInfo(int Index, string Name, Rectangle Bounds, bool Primary);
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
            .Select(s => new MonitorInfo(++i, s.DeviceName, s.Bounds, s.Primary)).ToList();
    }

    /// <summary>The screens that the given monitors are, as the handshake reports them (plan-monitor-layout.md, step 1). Only the
    /// screens the streams use are reported: the user extends to those.</summary>
    public static List<LANDock.Host.MonitorOffer> ScreenReport(IEnumerable<int> monitorIndexes)
    {
        var used = monitorIndexes.ToHashSet();
        return Monitors().Where(m => used.Contains(m.Index))
            .Select(m => new LANDock.Host.MonitorOffer(m.Name, m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height,
                ScreenRotation(m.Name), m.Primary))
            .ToList();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, byte[] devMode);

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

    public static string StreamFolder(HostProfile h, StreamSettings s) => Path.Combine(Paths.StreamsDir, StreamKey(h), s.Name);
    private static string MoonlightExe(HostProfile h, StreamSettings s) => Path.Combine(StreamFolder(h, s), "Moonlight.exe");
    private static string HostAddr(HostProfile h, StreamSettings s) => $"{h.Address}:{s.Port}";
    private static string LogFile(HostProfile h, StreamSettings s) => Path.Combine(StreamFolder(h, s), $"stream-{s.Name}.log");

    private static void RequireMoonlight()
    {
        if (!Paths.ClientInstalled) throw new InvalidOperationException("the client component is not installed (Moonlight is missing)");
    }

    private static IEnumerable<StreamSettings> Selected(HostProfile h, string? only) =>
        h.Streams.Where(s => string.IsNullOrEmpty(only) || s.Name.Equals(only, StringComparison.OrdinalIgnoreCase));

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

    private static List<Process> StreamProcesses(HostProfile h, StreamSettings s)
    {
        var dir = StreamFolder(h, s).TrimEnd('\\') + "\\";
        var res = new List<Process>();
        foreach (var p in Process.GetProcessesByName("Moonlight"))
        {
            try { if (p.MainModule?.FileName?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true) { res.Add(p); continue; } } catch { }
            p.Dispose();
        }
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

        var bad = 0;
        foreach (var s in Selected(h, null))
        {
            EnsureStream(h, s);
            if (await PairOneAsync(h, s, pin)) Log.Info($"{h.Name} {s.Name}: paired");
            else bad++;
        }
        if (bad > 0) Log.Info($"{h.Name}: {bad} display(s) not paired with Moonlight; pair again with the PIN shown on the laptop");
        else Log.Info($"{h.Name}: all displays paired");
        return bad == 0;
    }

    /// <summary>Takes the host's stream list (names and ports) as the truth, keeping the monitor choices of streams that still exist.</summary>
    private void SyncStreams(HostProfile h, List<RemoteStream> remote)
    {
        var list = new List<StreamSettings>();
        for (var i = 0; i < remote.Count; i++)
        {
            var r = remote[i];
            var s = h.Streams.FirstOrDefault(x => x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                    ?? new StreamSettings { Name = r.Name, Monitor = DefaultMonitor(i), Audio = "client" };
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
    private async Task<bool> PairOneAsync(HostProfile h, StreamSettings s, string pin)
    {
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
        var chosen = Selected(h, only).ToList();
        if (chosen.Count == 0) throw new InvalidOperationException($"{h.Name} has no displays (check the connection first)");

        var mons = Monitors();
        var offers = chosen.Select(s =>
        {
            var m = mons.FirstOrDefault(x => x.Index == s.Monitor) ?? mons.First();
            return new DisplayOffer(s.Name, m.Bounds.Width, m.Bounds.Height, s.Fps, m.Name);
        }).ToList();

        List<RemoteStream> agreed;
        try
        {
            using var c = new ControlClient(h.Address, h.ControlPort, h.Fingerprint);
            // every screen the user has set up for this laptop, not only the ones started now: the laptop lays out all of them
            agreed = await c.SessionAsync(h.Token, offers, _settings.Client.UltrasoundId, ScreenReport(h.Streams.Select(s => s.Monitor)));
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
                if (!await CanListAsync(h, s))
                {
                    Log.Info($"{h.Name} {s.Name}: not paired with Moonlight on port {s.Port}; press Pair");
                    allOk = false; continue;
                }
                EnsureStream(h, s);
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
                Process.Start(psi);
                launched = true;
                for (var i = 0; i < 80 && hw == IntPtr.Zero; i++)
                {
                    await Task.Delay(500);
                    p = StreamProcesses(h, s).FirstOrDefault();
                    if (p != null) { p.Refresh(); hw = p.MainWindowHandle; }
                }
                if (p == null) { Log.Info($"{h.Name} {s.Name}: Moonlight did not start or exited; see {LogFile(h, s)}"); allOk = false; continue; }
                if (hw == IntPtr.Zero) { Log.Info($"{h.Name} {s.Name}: no window appeared within 40 s; see {LogFile(h, s)}"); allOk = false; continue; }
            }
            var mon = mons.FirstOrDefault(m => m.Index == s.Monitor);
            if (mon == null) { Log.Info($"{h.Name} {s.Name}: monitor {s.Monitor} does not exist on this PC (see the Client tab); leaving the window where it is"); allOk = false; continue; }
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
        return allOk;
    }

    public async Task StopAsync(HostProfile h, string? only = null)
    {
        foreach (var s in Selected(h, only))
        {
            try
            {
                if (File.Exists(MoonlightExe(h, s)) && s.Port > 0)
                    await RunMoonlightAsync(h, s, new[] { "quit", HostAddr(h, s) }, 20);
            }
            catch (Exception ex) { Log.Error($"{h.Name} {s.Name}: quit", ex); }
            foreach (var p in StreamProcesses(h, s)) { try { p.Kill(true); } catch { } p.Dispose(); }
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
        // the Moonlight copies stay (Windows remembers their firewall answer); only their pairing data goes
        try
        {
            var folder = Path.Combine(Paths.StreamsDir, StreamKey(h));
            if (Directory.Exists(folder))
                foreach (var ini in Directory.GetFiles(folder, "Moonlight.ini", SearchOption.AllDirectories)) File.Delete(ini);
        }
        catch { }
        Log.Info($"{h.Name}: removed from this PC");
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
