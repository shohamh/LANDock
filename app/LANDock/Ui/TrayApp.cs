using System.Drawing;
using System.Reflection;
using LANDock.Client;
using LANDock.Core;
using LANDock.Host;
using LANDock.Update;
using Microsoft.Win32;

namespace LANDock.Ui;

/// <summary>Everything the windows and the command line share.</summary>
public sealed class AppServices
{
    public Settings Settings { get; }
    public HostService Host { get; }
    public ClientService Client { get; }
    public Updater Updater { get; }

    public AppServices(Settings settings)
    {
        Settings = settings;
        Host = new HostService(settings);
        Client = new ClientService(settings);
        Updater = new Updater(settings, Host, Client);
    }
}

/// <summary>Starts or stops "run at sign-in" (a per-user registry entry; no administrator rights needed).</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("LANDock") != null;
        }
        catch { return false; }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            var exe = Environment.ProcessPath;
            if (enabled && exe != null) key.SetValue("LANDock", $"\"{exe}\" --tray");
            else key.DeleteValue("LANDock", false);
        }
        catch (Exception ex) { Log.Error("start with Windows", ex); }
    }
}

/// <summary>The notification-area icon: click for the settings, right-click for the menu.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AppServices _app;
    private readonly NotifyIcon _icon;
    private readonly SettingsForm _form;
    private readonly ToolStripMenuItem _hostItem, _streamsItem, _updateItem, _statusItem;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 2000 };
    private readonly SynchronizationContext _ui;

    public TrayApp(AppServices app, bool afterUpdate)
    {
        _app = app;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _form = new SettingsForm(app);

        _statusItem = new ToolStripMenuItem($"LANDock {AppInfo.Version}") { Enabled = false };
        _hostItem = new ToolStripMenuItem("Start host", null, async (_, _) => await ToggleHost());
        _streamsItem = new ToolStripMenuItem("Start displays", null, async (_, _) => await ToggleStreams());
        _updateItem = new ToolStripMenuItem("Install update", null, (_, _) => _form.ShowTab("updates")) { Visible = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => _form.ShowTab("host")) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        menu.Items.Add(_hostItem);
        menu.Items.Add(_streamsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripMenuItem("Check for updates", null, async (_, _) => await CheckNow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, async (_, _) => await Quit()));
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon { Icon = LoadIcon(16), Text = "LANDock", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _form.ShowTab(Paths.HostInstalled && !Paths.ClientInstalled ? "host" : Paths.ClientInstalled && !Paths.HostInstalled ? "client" : "host"); };
        _icon.BalloonTipClicked += (_, _) => _form.ShowTab("updates");

        app.Updater.UpdateAvailable += c => _ui.Post(_ => Balloon("LANDock update available", $"{c.Summary}. Click to review it.", ToolTipIcon.Info), null);
        app.Updater.Changed += () => _ui.Post(_ => RefreshMenu(), null);
        _tick.Tick += (_, _) => RefreshMenu();
        _tick.Start();

        app.Updater.Start();
        _ = AutoStartAsync(afterUpdate);
        RefreshMenu();
        // first run: nothing configured yet, so show the window instead of only an icon
        if (!File.Exists(Paths.SettingsFile) && !afterUpdate) _form.ShowTab(Paths.ClientInstalled && !Paths.HostInstalled ? "client" : "host");
    }

    private async Task AutoStartAsync(bool afterUpdate)
    {
        try
        {
            var resumeHost = File.Exists(Path.Combine(Paths.DataDir, "resume-host"));
            var resumeStreams = File.Exists(Path.Combine(Paths.DataDir, "resume-streams"));
            foreach (var f in new[] { "resume-host", "resume-streams" }) try { File.Delete(Path.Combine(Paths.DataDir, f)); } catch { }
            if (Paths.HostInstalled && (_app.Settings.Host.AutoStart || resumeHost)) await _app.Host.StartAsync();
            if (Paths.ClientInstalled && (_app.Settings.Client.AutoStartStreams || resumeStreams) && _app.Settings.Client.HostAddress != "") await _app.Client.StartAsync();
            if (afterUpdate) Balloon("LANDock updated", $"Now running version {AppInfo.Version}.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("auto start", ex);
            Balloon("LANDock", ex.Message, ToolTipIcon.Warning);
        }
    }

    private async Task ToggleHost()
    {
        try { if (_app.Host.Running) await _app.Host.StopAsync(); else await _app.Host.StartAsync(); }
        catch (Exception ex) { Balloon("LANDock host", ex.Message, ToolTipIcon.Warning); }
        RefreshMenu();
        if (_app.Host.Running) Balloon("LANDock host is ready", $"Pairing PIN {_app.Host.Pin} (open {_app.Settings.Host.PairingWindowMinutes} minutes).", ToolTipIcon.Info);
    }

    private async Task ToggleStreams()
    {
        try { if (_app.Client.AnyRunning()) await _app.Client.StopAsync(); else await _app.Client.StartAsync(); }
        catch (Exception ex) { Balloon("LANDock client", ex.Message, ToolTipIcon.Warning); _form.ShowTab("client"); }
        RefreshMenu();
    }

    private async Task CheckNow()
    {
        var c = await _app.Updater.CheckAsync();
        if (c is { Any: true }) _form.ShowTab("updates");
        else Balloon("LANDock", string.IsNullOrEmpty(_app.Updater.LastError) ? "LANDock is up to date." : _app.Updater.LastError, ToolTipIcon.Info);
    }

    private async Task Quit()
    {
        _tick.Stop();
        _icon.Visible = false;
        try { await _app.Host.StopAsync(); } catch { }
        try { await _app.Client.StopAsync(); } catch { }
        _app.Updater.Stop();
        ExitThread();
    }

    private void RefreshMenu()
    {
        var host = _app.Host;
        _hostItem.Visible = Paths.HostInstalled;
        _hostItem.Text = host.Starting ? "Host starting…" : host.Running ? "Stop host" : "Start host";
        _hostItem.Enabled = !host.Starting;
        _streamsItem.Visible = Paths.ClientInstalled;
        var streaming = Paths.ClientInstalled && _app.Client.AnyRunning();
        _streamsItem.Text = streaming ? "Stop displays" : "Start displays";
        var latest = _app.Updater.Latest;
        var pending = latest is { Any: true };
        _updateItem.Visible = pending;
        if (pending) _updateItem.Text = $"Update available: {latest!.Manifest.Tag}";
        var state = host.Running ? "host running" : streaming ? "displays running" : "idle";
        _statusItem.Text = $"LANDock {AppInfo.Version} — {state}";
        var tip = $"LANDock — {state}{(pending ? " — update available" : "")}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void Balloon(string title, string text, ToolTipIcon icon)
    {
        try { _icon.ShowBalloonTip(8000, title, text, icon); } catch { }
    }

    public void ShowSettings(string tab = "host") => _ui.Post(_ => _form.ShowTab(tab), null);

    public static Icon LoadIcon(int size)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LANDock.Assets.landock.ico");
        return s == null ? SystemIcons.Application : new Icon(s, new Size(size, size));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _icon.Dispose(); _form.Dispose(); _tick.Dispose(); }
        base.Dispose(disposing);
    }
}
