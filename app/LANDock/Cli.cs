using LANDock.Client;
using LANDock.Core;
using LANDock.Host;
using LANDock.Ui;
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
        LANDock client set-host ADDRESS          remember the laptop's address
        LANDock client pair --pin 1234 [--only NAME]
        LANDock client start|stop|status [--only NAME]
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
                using var mutex = new Mutex(true, @"Local\LANDock.Host", out var first);
                if (!first) { Console.WriteLine("a host is already running (LANDock tray or another window)"); return 1; }
                using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\LANDock.HostStop");
                stop.Reset();
                using var host = new HostService(settings);
                var cancel = new ManualResetEvent(false);
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Set(); };
                using var startCts = new CancellationTokenSource();
                _ = Task.Run(() => { WaitHandle.WaitAny(new WaitHandle[] { stop, cancel }); startCts.Cancel(); });   // "host down" also works while it is still starting
                try { await host.StartAsync(startCts.Token); }
                catch (OperationCanceledException) { Console.WriteLine("stopped before the host was ready"); return 0; }
                var pinFile = Opt(args, "--pin-file");
                if (pinFile != null) File.WriteAllText(pinFile, host.Pin);
                Console.WriteLine();
                Console.WriteLine("Laptop addresses (use the one on the same network as the desktop PC):");
                foreach (var a in HostSetup.LocalAddresses()) Console.WriteLine($"  {a.address}  ({a.name})");
                Console.WriteLine($"Pairing PIN for this session: {host.Pin}   (new clients can pair for the next {settings.Host.PairingWindowMinutes} minutes)");
                Console.WriteLine("Host is ready. Press Ctrl+C or run \"LANDock host down\" to stop.");
                WaitHandle.WaitAny(new WaitHandle[] { stop, cancel });
                await host.StopAsync();
                return 0;
            }
            case "down":
            {
                using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\LANDock.HostStop");
                stop.Set();
                Console.WriteLine("asked the running host to stop");
                return 0;
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

    private static async Task<int> ClientCommand(string[] args, Settings settings)
    {
        var client = new ClientService(settings);
        var only = Opt(args, "--only");
        switch (args[1])
        {
            case "monitors":
                foreach (var m in ClientService.Monitors())
                    Console.WriteLine($"{m.Index}: {m.Name}  {m.Bounds.Width}x{m.Bounds.Height} at ({m.Bounds.X},{m.Bounds.Y}){(m.Primary ? "  [primary]" : "")}");
                return 0;
            case "set-host" when args.Length > 2:
                settings.Client.HostAddress = args[2];
                settings.Save();
                Console.WriteLine("laptop address set to " + settings.Client.HostAddress);
                return 0;
            case "pair":
                return await client.PairAsync(Opt(args, "--pin") ?? throw new ArgumentException("--pin 1234 is required"), only) ? 0 : 1;
            case "start":
                return await client.StartAsync(only) ? 0 : 1;
            case "stop":
                await client.StopAsync(only);
                return 0;
            case "status":
                foreach (var s in client.Status())
                    Console.WriteLine(s.Running ? $"stream {s.Name}: running (pid {s.Pid}), window {s.Window.Width}x{s.Window.Height} at ({s.Window.X},{s.Window.Y}) on monitor {s.Monitor}" : $"stream {s.Name}: not running");
                return 0;
            default:
                Console.WriteLine(Usage);
                return 2;
        }
    }

    private static string OrIfEmpty(this string s, string fallback) => s == "" ? fallback : s;
}
