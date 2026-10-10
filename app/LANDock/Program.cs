using LANDock.Core;
using LANDock.Ui;
using LANDock.Update;

namespace LANDock;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));   // per-monitor v2: real pixel coordinates on every monitor, also on the command line
        var tray = args.Length == 0 || args[0] is "--tray" or "--settings";
        if (!tray) return Cli.Run(args);

        // one tray icon per user: a second start just asks the first to show its window
        using var mutex = new Mutex(true, @"Local\LANDock.Tray", out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LANDock.ShowSettings");
        if (!first)
        {
            show.Set();
            return 0;
        }

        Updater.CleanupLeftovers();
        ApplicationConfiguration.Initialize();
        var app = new AppServices(Settings.Load());
        var afterUpdate = args.Contains("--after-update");
        var context = new TrayApp(app, afterUpdate);
        if (args.Contains("--settings")) context.ShowSettings();

        var waiter = new Thread(() =>
        {
            while (show.WaitOne()) context.ShowSettings();
        }) { IsBackground = true, Name = "show-settings" };
        waiter.Start();

        Application.Run(context);
        return 0;
    }
}
