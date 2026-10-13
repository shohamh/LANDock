using LANDock.Client;
using LANDock.Core;
using LANDock.Host;
using LANDock.Ui;
using LANDock.Ultrasound;
using LANDock.Update;

namespace LANDock;

/// <summary>The command line: the same actions as the tray icon, for scripts and tests.</summary>
internal static class Cli
{
    private const string Usage = """
        LANDock                                  start the tray app
        LANDock host up [--pin-file FILE]        run the host in this window (Ctrl+C or "host down" stops it)
        LANDock host down                        stop a host started with "host up"
        LANDock host cleanup                     remove virtual displays left behind by clients that vanished
        LANDock client monitors                  list this PC's monitors
        LANDock client hosts                     list the saved hosts (the default one is marked)
        LANDock client check ADDRESS [--port 49900]
                                                 ask a host who it is and show its certificate fingerprint (saves nothing)
        LANDock client add-host ADDRESS --pin 1234 [--port 49900] [--name NAME] [--trust]
                                                 pair with a host; --trust accepts the fingerprint that "check" showed
        LANDock client pair --host NAME --pin 1234      pair again (the host forgot this PC, or the PIN changed)
        LANDock client remove-host NAME          forget a host on this PC
        LANDock client assign HOST STREAM MONITOR [--mode windowed] [--rect x,y,w,h]
                                                 send a display to a monitor of this PC
        LANDock client start|stop|status|follow [--host NAME] [--only STREAM]
        LANDock update check                     look for a newer release now
        LANDock update apply                     check, then install everything that is newer
        LANDock --version

        Data lives in %LOCALAPPDATA%\LANDock (LANDOCK_DATA_DIR overrides it). Exit code 0 = success.
        """;

    public static int Run(string[] args)
    {
        Native.AttachConsole(-1);   // print into the console that started us (no console window of our own)
        Log.Line += Console.WriteLine;
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); Log.Error("command failed", ex); return 1; }
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var settings = Settings.Load();
        switch (args[0])
        {
            case "--version" or "-v":
                Console.WriteLine($"LANDock {AppInfo.Version}  host {Updater.InstalledHost.OrIfEmpty("not installed")}  client {Updater.InstalledClient.OrIfEmpty("not installed")}");
                return 0;

            case "host" when args.Length > 1:
                return await HostCommand(args, settings);

            case "ultrasound" when args.Length > 1:
                return await UltrasoundCommand(args, settings);

            case "input-level":
            {
                // input-level [set PERCENT]: reads or sets the microphone's input level (Windows volume of the device)
                var mic = AudioDevices.Input(settings.Sound.InputDeviceId);
                var vol = mic.AudioEndpointVolume;
                if (args.Length > 2 && args[1] == "set")
                {
                    var before = vol.MasterVolumeLevelScalar;
                    vol.MasterVolumeLevelScalar = Math.Clamp(float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) / 100f, 0f, 1f);
                    Console.WriteLine($"microphone '{mic.FriendlyName}': input level {before * 100:0}% -> {vol.MasterVolumeLevelScalar * 100:0}%");
                }
                else Console.WriteLine($"microphone '{mic.FriendlyName}': input level {vol.MasterVolumeLevelScalar * 100:0}%, muted {vol.Mute}, boost channels {vol.Channels.Count}");
                return 0;
            }

            case "devices":
                Console.WriteLine("microphones:");
                foreach (var d in AudioDevices.Inputs()) Console.WriteLine($"  {d.Name}   [{d.Id}]");
                Console.WriteLine("speakers:");
                foreach (var d in AudioDevices.Outputs()) Console.WriteLine($"  {d.Name}   [{d.Id}]");
                return 0;

            case "client" when args.Length > 1:
                return await ClientCommand(args, settings);

            case "update" when args.Length > 1:
            {
                var host = new HostService(settings); var client = new ClientService(settings);
                var updater = new Updater(settings, host, client);
                var check = await updater.CheckAsync();
                if (check == null) { Console.WriteLine("no release information: " + updater.LastError); return 1; }
                Console.WriteLine(check.Any ? $"update available ({check.Manifest.Tag}): {check.Summary}" : $"up to date ({check.Manifest.Tag})");
                if (args[1] == "apply" && check.Any) return await updater.ApplyAsync(check) ? 0 : 1;
                return updater.LastError == "" ? 0 : 1;
            }

            default:
                Console.WriteLine(Usage);
                return args[0] is "help" or "--help" or "-h" ? 0 : 2;
        }
    }

    private static async Task<int> HostCommand(string[] args, Settings settings)
    {
        switch (args[1])
        {
            case "up":
            {
                // the host itself holds the one-host mutex and watches the stop signal (HostService.StartAsync)
                using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\LANDock.HostStop");
                stop.Reset();
                using var host = new HostService(settings);
                var cancel = new ManualResetEvent(false);
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Set(); };
                // "host down" or Ctrl+C cancels the start while it runs; once the host is up they are handled below (the host's
                // own token must not be cancelled by them, or its watchers end while it still runs)
                using var startCts = new CancellationTokenSource();
                var started = false;
                _ = Task.Run(() => { WaitHandle.WaitAny(new WaitHandle[] { stop, cancel }); if (!started) startCts.Cancel(); });
                try { await host.StartAsync(startCts.Token); started = true; }
                catch (OperationCanceledException) { Console.WriteLine("stopped before the host was ready"); return 0; }
                catch (InvalidOperationException ex) { Console.WriteLine(ex.Message); return 1; }
                var pinFile = Opt(args, "--pin-file");
                if (pinFile != null) File.WriteAllText(pinFile, host.Pin);
                Console.WriteLine();
                Console.WriteLine($"Control port (desktop PCs connect here): {settings.Host.ControlPort}");
                Console.WriteLine("Laptop addresses (use the one on the same network as the desktop PC):");
                foreach (var a in HostSetup.LocalAddresses()) Console.WriteLine($"  {a.address}  ({a.name})");
                Console.WriteLine($"Pairing PIN for this session: {host.Pin}   (new clients can pair for the next {settings.Host.PairingWindowMinutes} minutes)");
                Console.WriteLine("Host is ready. Press Ctrl+C or run \"LANDock host down\" to stop.");
                // until "host down" or Ctrl+C: the host ends itself on "host down" (its stop watcher), so the loop checks it too
                while (host.Running && !cancel.WaitOne(500)) { }
                await host.StopAsync();
                return 0;
            }
            case "down":
            {
                using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\LANDock.HostStop");
                stop.Set();
                // the host still holds its single-instance mutex until it has really stopped: wait for that, so a "host up" right
                // after this one is not refused as "already running"
                using var running = new Mutex(false, @"Local\LANDock.Host." + Paths.InstanceId);
                var gone = false;
                for (var i = 0; i < 120 && !gone; i++)
                {
                    try { gone = running.WaitOne(TimeSpan.FromMilliseconds(500)); }
                    catch (AbandonedMutexException) { gone = true; }   // the host ended without releasing it: it is gone too
                    if (gone) running.ReleaseMutex();
                }
                Console.WriteLine(gone ? "the host stopped" : "asked the running host to stop (it is still shutting down)");
                return gone ? 0 : 1;
            }
            case "cleanup":
                HostService.StopLeftovers();
                Console.WriteLine(HostService.Cleanup());
                return 0;
            default:
                Console.WriteLine(Usage);
                return 2;
        }
    }

    /// <summary>
    /// ultrasound test [SECONDS]: plays this PC's id through the speakers and listens on the microphone at the same time,
    /// printing the level of the ultrasound bars every second. Use it to check the speakers and microphone of this PC.
    /// </summary>
    private static async Task<int> UltrasoundCommand(string[] args, Settings settings)
    {
        if (args[1] != "test") { Console.WriteLine(Usage); return 2; }
        var seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 30;
        var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var rd = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
        var cd = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
        Console.WriteLine($"mode: {SoundMode.Label(settings.Sound.Mode)}");
        Console.WriteLine("speakers available: " + string.Join(" | ", AudioDevices.Outputs().Select(d => d.Name)));
        Console.WriteLine("microphones available: " + string.Join(" | ", AudioDevices.Inputs().Select(d => d.Name)));
        var used = AudioDevices.Output(settings.Sound.OutputDeviceId);
        Console.WriteLine($"speakers used: '{used.FriendlyName}': volume {used.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}%, muted {used.AudioEndpointVolume.Mute}, mix {used.AudioClient.MixFormat.SampleRate} Hz");
        Console.WriteLine($"microphone '{cd.FriendlyName}': volume {cd.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}%, muted {cd.AudioEndpointVolume.Mute}, mix {cd.AudioClient.MixFormat.SampleRate} Hz");
        var id = settings.Client.UltrasoundId;
        var toneHz = args.Length > 3 && double.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hz) ? hz : 0;
        using var listener = new UltraListener();
        using var announcer = new UltraAnnouncer();
        listener.Start(settings.Sound.Mode, settings.Sound.InputDeviceId);
        if (toneHz > 0)
        {
            // a plain tone at one frequency: shows which frequencies the microphone really records
            announcer.Start(null, settings.Sound.Mode, settings.Sound.OutputDeviceId);
            announcer.EnqueueTone(toneHz, seconds, 0.5);
            Console.WriteLine($"listening on '{listener.Device}' while playing a {toneHz:0} Hz tone for {seconds} s");
        }
        else
        {
            announcer.Start(id, settings.Sound.Mode, settings.Sound.OutputDeviceId);
            Console.WriteLine($"listening on '{listener.Device}' and playing id {id} for {seconds} s");
        }
        if (listener.Problem != null) Console.WriteLine("microphone: " + listener.Problem);
        if (announcer.Problem != null) Console.WriteLine("speakers: " + announcer.Problem);
        // the speakers' own output, recorded digitally (loopback): shows whether the tone is played at all
        var band = SoundMode.Band(settings.Sound.Mode);
        var loopMeter = new BandMeter(used.AudioClient.MixFormat.SampleRate, band.low, band.high);
        using var loop = new NAudio.Wave.WasapiLoopbackCapture(used);
        long loopBytes = 0; double loopPeak = 0;
        loop.DataAvailable += (_, e) =>
        {
            var mono = UltraListener.ToMono(e.Buffer, e.BytesRecorded, loop.WaveFormat);
            loopBytes += e.BytesRecorded;
            foreach (var v in mono) loopPeak = Math.Max(loopPeak, Math.Abs(v));
            loopMeter.Process(mono);
        };
        loop.StartRecording();
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            await Task.Delay(1000);
            var bars = listener.HoldDb();
            var line = string.Join(" ", bars.Select(b => Math.Clamp(b, 0, 99).ToString("00")));
            var outLine = string.Join(" ", loopMeter.HoldDb.Select(b => Math.Clamp(b, 0, 99).ToString("00")));
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} mic  {listener.BandDb,5:0.0} dB | {line}  ({band.low / 1000:0.0}-{band.high / 1000:0.0} kHz) | whole signal {listener.RmsDbfs,6:0.0} dBFS");
            string Peak(Func<float> f) { try { return f().ToString("0.0000"); } catch (Exception ex) { return "n/a (" + ex.GetType().Name + ")"; } }
            var inPeak = Peak(() => AudioDevices.Input(settings.Sound.InputDeviceId).AudioMeterInformation.MasterPeakValue);
            var outPeak = Peak(() => used.AudioMeterInformation.MasterPeakValue);
            Console.WriteLine($"         device peaks: microphone {inPeak}, speakers {outPeak}");
            Console.WriteLine($"         out  {loopMeter.BandDb,5:0.0} dB | {outLine} | loopback {loopBytes} bytes, peak {loopPeak:0.0000}");
        }
        loop.StopRecording();
        Console.WriteLine("speakers: " + announcer.Diagnostics);
        Console.WriteLine($"heard: {string.Join(", ", Heard.Snapshot().Select(kv => kv.Key))}");
        return 0;
    }

    private static async Task<int> ClientCommand(string[] args, Settings settings)
    {
        var client = new ClientService(settings);
        var hostName = Opt(args, "--host");
        var only = Opt(args, "--only");
        switch (args[1])
        {
            case "monitors":
                foreach (var m in ClientService.Monitors())
                    Console.WriteLine($"{m.Index}: {m.Name}  {m.Bounds.Width}x{m.Bounds.Height} at ({m.Bounds.X},{m.Bounds.Y}){(m.Primary ? "  [primary]" : "")}");
                return 0;

            case "found":
            {
                // the laptops seen on the LAN right now (listens for about 6 seconds; plays nothing)
                using var browser = new LANDock.Discovery.LanBrowser(settings.Client.UltrasoundId);
                browser.Start();
                await Task.Delay(6000);
                var list = browser.Snapshot();
                if (list.Count == 0) Console.WriteLine("no laptop seen on the LAN");
                foreach (var d in list)
                    Console.WriteLine($"{d.Name}  {d.Address}:{d.ControlPort}  id {d.HostId}  pairing {d.Pairing}  seen {(DateTime.Now - d.LastSeen).TotalSeconds:0} s ago");
                return 0;
            }

            case "hosts":
                if (settings.Client.Hosts.Count == 0) { Console.WriteLine("no hosts yet: LANDock client add-host ADDRESS --pin 1234"); return 0; }
                foreach (var h in settings.Client.Hosts)
                {
                    var mark = h.Id == settings.Client.DefaultHostId ? "*" : " ";
                    Console.WriteLine($"{mark} {h.Name}  {h.Address}:{h.ControlPort}  {(h.Paired ? "paired" : "not paired")}  displays: {string.Join(", ", h.Streams.Select(s => $"{s.Name}->monitor {s.Monitor}"))}");
                }
                return 0;

            case "check" when args.Length > 2:
            {
                var (id, fp) = await ClientService.CheckHostAsync(args[2], int.Parse(Opt(args, "--port") ?? "49900"));
                Console.WriteLine($"host:        {id.HostName}  (LANDock {id.Version})");
                Console.WriteLine($"host id:     {id.HostId}");
                Console.WriteLine($"fingerprint: {fp}");
                Console.WriteLine($"pairing:     {(id.PairingOpen ? "open: enter the PIN from the laptop" : "closed: press New pairing PIN on the laptop")}");
                return 0;
            }

            case "add-host" when args.Length > 2:
            {
                var address = args[2];
                var port = int.Parse(Opt(args, "--port") ?? "49900");
                var pin = Opt(args, "--pin") ?? throw new ArgumentException("--pin 1234 is required");
                var (id, fp) = await ClientService.CheckHostAsync(address, port);
                Console.WriteLine($"host {id.HostName}, fingerprint {fp}");
                if (!args.Contains("--trust")) { Console.WriteLine("compare the fingerprint with the one on the laptop, then add --trust to pair"); return 2; }
                var h = new HostProfile { Name = Opt(args, "--name") ?? id.HostName, Address = address, ControlPort = port, Fingerprint = fp };
                settings.Client.Hosts.Add(h);
                if (settings.Client.DefaultHost == null || settings.Client.DefaultHostId == "") settings.Client.DefaultHostId = h.Id;
                settings.Save();
                return await client.PairAsync(h, pin) ? 0 : 1;
            }

            case "pair":
            {
                var h = client.Host(hostName) ?? throw new ArgumentException("no such host: use --host NAME (see: client hosts)");
                return await client.PairAsync(h, Opt(args, "--pin") ?? throw new ArgumentException("--pin 1234 is required")) ? 0 : 1;
            }

            case "remove-host" when args.Length > 2:
            {
                var h = client.Host(args[2]) ?? throw new ArgumentException("no such host");
                await client.RemoveAsync(h);
                Console.WriteLine($"removed {h.Name}");
                return 0;
            }

            case "assign" when args.Length > 4:
            {
                var h = client.Host(args[2]) ?? throw new ArgumentException("no such host");
                var s = h.Streams.FirstOrDefault(x => x.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"no display {args[3]}");
                LANDock.Client.ClientService.AssignMonitor(s, int.Parse(args[4]));
                // a display given a screen is in use; --off keeps it set up but not started
                s.Enabled = !args.Contains("--off");
                if (Opt(args, "--mode") is { } mode) s.DisplayMode = mode;
                if (Opt(args, "--rect") is { } rect) s.WindowedRect = rect.Split(',').Select(int.Parse).ToArray();
                settings.Save();
                Console.WriteLine($"{h.Name} {s.Name} -> monitor {s.Monitor}{(s.DisplayMode == "" ? "" : " " + s.DisplayMode)}");
                return 0;
            }

            case "start":
            {
                var h = client.Host(hostName) ?? throw new ArgumentException("no host: add one with client add-host");
                return await client.StartAsync(h, only) ? 0 : 1;
            }

            case "follow":
            {
                // follows this PC's screens until Ctrl+C: a screen plugged in or removed changes the displays on the laptop (step 6)
                var h = client.Host(hostName) ?? throw new ArgumentException("no host: add one with client add-host");
                var done = new TaskCompletionSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                client.Follow(h);
                Console.WriteLine($"following the screens of this PC for {h.Name} (Ctrl+C stops)");
                await done.Task;
                client.StopFollowing(h);
                return 0;
            }

            case "stop":
            {
                var h = client.Host(hostName) ?? throw new ArgumentException("no host");
                await client.StopAsync(h, only);
                return 0;
            }

            case "status":
            {
                var h = client.Host(hostName) ?? throw new ArgumentException("no host");
                foreach (var s in client.Status(h))
                    Console.WriteLine(s.Running ? $"{h.Name} {s.Name}: running (pid {s.Pid}), window {s.Window.Width}x{s.Window.Height} at ({s.Window.X},{s.Window.Y}) on monitor {s.Monitor}" : $"{h.Name} {s.Name}: not running");
                return 0;
            }

            default:
                Console.WriteLine(Usage);
                return 2;
        }
    }

    private static string OrIfEmpty(this string s, string fallback) => s == "" ? fallback : s;
}
