using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using LANDock.Core;

namespace LANDock.Client;

public sealed record MonitorInfo(int Index, string Name, Rectangle Bounds, bool Primary);
public sealed record StreamStatus(string Name, bool Running, int Pid, Rectangle Window, int Monitor);

/// <summary>
/// The desktop PC side: one Moonlight window per virtual display of the laptop, each placed on its own monitor.
/// Moonlight has no command-line option for which monitor to use, so after the video starts the window is moved
/// there and switched to fullscreen with Moonlight's own hotkey. Every stream has its own Moonlight copy, because
/// each stream is a separate "computer" (separate port) with its own pairing.
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

    public static string StreamFolder(StreamSettings s) => Path.Combine(Paths.StreamsDir, s.Name);
    private static string MoonlightExe(StreamSettings s) => Path.Combine(StreamFolder(s), "Moonlight.exe");
    private string HostAddr(StreamSettings s) => $"{Cfg.HostAddress}:{s.Port}";
    private static string LogFile(StreamSettings s) => Path.Combine(StreamFolder(s), $"stream-{s.Name}.log");

    private void RequireReady()
    {
        if (!Paths.ClientInstalled) throw new InvalidOperationException("the client component is not installed (Moonlight is missing)");
        if (string.IsNullOrWhiteSpace(Cfg.HostAddress)) throw new InvalidOperationException("set the laptop's address first (Client settings)");
        if (Cfg.Streams.Count == 0) throw new InvalidOperationException("no streams configured");
    }

    private IEnumerable<StreamSettings> Selected(string? only) =>
        Cfg.Streams.Where(s => string.IsNullOrEmpty(only) || s.Name.Equals(only, StringComparison.OrdinalIgnoreCase));

    // ---- Moonlight copies ---------------------------------------------------------------------------------------------

    /// <summary>Makes sure the stream's own Moonlight copy exists and matches the installed client component.
    /// Pairing data (Moonlight.ini, in the copy) is never touched.</summary>
    public static void EnsureStream(StreamSettings s)
    {
        var dir = StreamFolder(s);
        var marker = Path.Combine(dir, ".landock-client-version");
        var want = Paths.ReadVersion(Paths.ClientVersionFile);
        var have = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        if (File.Exists(MoonlightExe(s)) && have == want) return;
        Directory.CreateDirectory(dir);
        CopyTree(Paths.MoonlightSource, dir);
        File.WriteAllText(marker, want);
        Log.Info($"stream {s.Name}: Moonlight copy ready in {dir}");
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

    private static async Task<string> RunMoonlightAsync(StreamSettings s, IEnumerable<string> args, int timeoutSec)
    {
        var psi = new ProcessStartInfo(MoonlightExe(s))
        {
            WorkingDirectory = StreamFolder(s), UseShellExecute = false, CreateNoWindow = true,
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

    private async Task<bool> CanListAsync(StreamSettings s) =>
        Regex.IsMatch(await RunMoonlightAsync(s, new[] { "list", HostAddr(s) }, 25), @"(?m)^VD\b");

    private static List<Process> StreamProcesses(StreamSettings s)
    {
        var dir = StreamFolder(s).TrimEnd('\\') + "\\";
        var res = new List<Process>();
        foreach (var p in Process.GetProcessesByName("Moonlight"))
        {
            try { if (p.MainModule?.FileName?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true) { res.Add(p); continue; } } catch { }
            p.Dispose();
        }
        return res;
    }

    public bool AnyRunning() => Cfg.Streams.Any(s => StreamProcesses(s).Count > 0);

    // ---- commands -----------------------------------------------------------------------------------------------------

    /// <summary>Pairs every stream with the laptop. The laptop accepts the PIN by itself during its pairing window.</summary>
    public async Task<bool> PairAsync(string pin, string? only = null)
    {
        RequireReady();
        if (!Regex.IsMatch(pin ?? "", @"^\d{4}$")) throw new ArgumentException("the PIN is the 4 digits shown on the laptop");
        Log.Info($"pairing with {Cfg.HostAddress}; the host must be running on the laptop (it accepts new pairings for a short window)");
        var bad = 0;
        foreach (var s in Selected(only))
        {
            EnsureStream(s);
            if (await PairOneAsync(s, pin!)) Log.Info($"stream {s.Name}: paired");
            else bad++;
        }
        if (bad > 0) Log.Info($"{bad} stream(s) not paired: is the host running on the laptop, is its firewall open, is the PIN current?");
        else Log.Info("all streams paired");
        return bad == 0;
    }

    /// <summary>
    /// Moonlight's "pair" command keeps running after the pairing succeeded, so it is watched instead of waited for:
    /// as soon as the last step went through and the laptop lists the stream's app, the pairing is done and Moonlight is ended.
    /// A pairing can fail by timing (the PIN arrived late), so it is tried up to three times.
    /// </summary>
    private async Task<bool> PairOneAsync(StreamSettings s, string pin)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await CanListAsync(s)) { if (attempt == 1) Log.Info($"stream {s.Name}: already paired and reachable"); return true; }
            Log.Info($"stream {s.Name}: pairing on port {s.Port}{(attempt > 1 ? $" (attempt {attempt})" : "")} ...");
            var output = new System.Text.StringBuilder();
            var psi = new ProcessStartInfo(MoonlightExe(s))
            {
                WorkingDirectory = StreamFolder(s), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "pair", HostAddr(s), "--pin", pin }) psi.ArgumentList.Add(a);
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
                if (text.Contains("phrase=pairchallenge") && await CanListAsync(s)) { ok = true; break; }   // last step done and the laptop knows us
                if (p.HasExited) { ok = await CanListAsync(s); break; }
            }
            try { if (!p.HasExited) p.Kill(true); } catch { }
            if (ok) return true;
            string last; lock (output) last = output.ToString();
            foreach (var l in last.Split(Environment.NewLine).Where(l => Regex.IsMatch(l, "rror|fail|denied|Critical|offline")).Take(4)) Log.Info("    " + l.Trim());
            await Task.Delay(2000);
        }
        Log.Info($"stream {s.Name}: NOT paired");
        return false;
    }

    /// <summary>Starts every stream (or one) and puts each Moonlight window on its monitor.</summary>
    public async Task<bool> StartAsync(string? only = null)
    {
        RequireReady();
        var allOk = true;
        foreach (var s in Selected(only))
        {
            var procs = StreamProcesses(s);
            var p = procs.FirstOrDefault();
            var launched = false;
            var h = IntPtr.Zero;
            if (p != null) { Log.Info($"stream {s.Name}: already running, placing its window"); p.Refresh(); h = p.MainWindowHandle; }
            else
            {
                EnsureStream(s);
                var args = new List<string> { "stream", HostAddr(s), "VD", "--display-mode", "windowed", "--absolute-mouse" };
                if (!string.IsNullOrWhiteSpace(s.Resolution)) args.AddRange(new[] { "--resolution", s.Resolution });
                if (s.Fps > 0) args.AddRange(new[] { "--fps", s.Fps.ToString() });
                if (s.Bitrate > 0) args.AddRange(new[] { "--bitrate", s.Bitrate.ToString() });
                args.Add(s.Audio == "host" ? "--audio-on-host" : "--no-audio-on-host");
                var argText = string.Join(' ', args.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
                // run through cmd so Moonlight's console chatter goes to a log file, on pipes of its own
                // (so the stream does not keep this program's output open)
                var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                {
                    WorkingDirectory = StreamFolder(s), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    Arguments = $"/c \"\"{MoonlightExe(s)}\" {argText} > \"{LogFile(s)}\" 2>&1\"",
                };
                Process.Start(psi);
                launched = true;
                for (var i = 0; i < 80 && h == IntPtr.Zero; i++)
                {
                    await Task.Delay(500);
                    p = StreamProcesses(s).FirstOrDefault();
                    if (p != null) { p.Refresh(); h = p.MainWindowHandle; }
                }
                if (p == null) { Log.Info($"stream {s.Name}: Moonlight did not start or exited; see {LogFile(s)}"); allOk = false; continue; }
                if (h == IntPtr.Zero) { Log.Info($"stream {s.Name}: no window appeared within 40 s; see {LogFile(s)}"); allOk = false; continue; }
            }
            var mon = Monitors().FirstOrDefault(m => m.Index == s.Monitor);
            if (mon == null) { Log.Info($"stream {s.Name}: monitor {s.Monitor} does not exist on this PC (see the Client tab); leaving the window where it is"); allOk = false; continue; }
            if (launched)
            {
                if (!await WaitVideoAsync(LogFile(s), 40)) Log.Info($"stream {s.Name}: no video within 40 s (see {LogFile(s)}); placing the window anyway");
                await Task.Delay(1500);
            }
            var mode = string.IsNullOrEmpty(s.DisplayMode) ? Cfg.DisplayMode : s.DisplayMode;
            var ok = await PlaceWindowAsync(p!, mon.Bounds, mode, s.WindowedRect);
            allOk &= ok;
            p!.Refresh();
            Native.GetWindowRect(p.MainWindowHandle, out var r);
            Log.Info($"stream {s.Name}: on monitor {s.Monitor} at ({r.L},{r.T}) {r.Width}x{r.Height} - " +
                     (ok ? (mode == "fullscreen" ? "fullscreen, covers the monitor" : "windowed, as configured")
                         : "could not be placed; use Ctrl+Alt+Shift+X in that window to toggle fullscreen"));
            await Task.Delay(2000);
        }
        Log.Info("done. Click once in a window to give it keyboard focus; moving the mouse between monitors moves the laptop's pointer between its displays");
        return allOk;
    }

    public async Task StopAsync(string? only = null)
    {
        foreach (var s in Selected(only))
        {
            try
            {
                if (File.Exists(MoonlightExe(s)) && !string.IsNullOrWhiteSpace(Cfg.HostAddress))
                    await RunMoonlightAsync(s, new[] { "quit", HostAddr(s) }, 20);
            }
            catch (Exception ex) { Log.Error($"stream {s.Name}: quit", ex); }
            foreach (var p in StreamProcesses(s)) { try { p.Kill(true); } catch { } p.Dispose(); }
            Log.Info($"stream {s.Name}: stopped (the laptop removes its virtual display)");
        }
    }

    public List<StreamStatus> Status()
    {
        var mons = Monitors();
        var res = new List<StreamStatus>();
        foreach (var s in Cfg.Streams)
        {
            var p = StreamProcesses(s).FirstOrDefault();
            if (p == null) { res.Add(new StreamStatus(s.Name, false, 0, Rectangle.Empty, 0)); continue; }
            Native.GetWindowRect(p.MainWindowHandle, out var r);
            var rect = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
            var c = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            var on = mons.FirstOrDefault(m => m.Bounds.Contains(c))?.Index ?? 0;
            res.Add(new StreamStatus(s.Name, true, p.Id, rect, on));
        }
        return res;
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
