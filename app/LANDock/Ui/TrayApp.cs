using System.Drawing;
using System.Reflection;
using LANDock.Client;
using LANDock.Core;
using LANDock.Discovery;
using LANDock.Host;
using LANDock.Ultrasound;
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
    /// <summary>The laptop's microphone, listening for desktops (only when the host setting is on).</summary>
    public UltraListener Listener { get; } = new();
    /// <summary>The desktop's sound: its id played as ultrasound (only when the client setting is on).</summary>
    public UltraAnnouncer Announcer { get; } = new();
    /// <summary>The desktop's view of the LAN: the laptops found by mDNS (only when the client setting is on).</summary>
    public LanBrowser Browser { get; }
    /// <summary>The desktop's presence on the LAN, so that a paired laptop recognizes it (only while a laptop is paired).</summary>
    public PresenceAdvertiser Presence { get; } = new();
    /// <summary>The laptop's view of the LAN: the paired desktops that show their presence.</summary>
    public PresenceBrowser LaptopPresence { get; } = new();
    /// <summary>The laptop's notice about nearby paired desktops (see NoticeService).</summary>
    public NoticeService Notices { get; }
    private string _listenSig = "", _announceSig = "";
    private readonly HashSet<string> _startHandled = new(StringComparer.OrdinalIgnoreCase);
    private bool _startsBusy;

    public AppServices(Settings settings)
    {
        Settings = settings;
        Host = new HostService(settings);
        Client = new ClientService(settings);
        Updater = new Updater(settings, Host, Client);
        Browser = new LanBrowser(settings.Client.UltrasoundId);
        Notices = new NoticeService(Host, LaptopPresence);
    }

    /// <summary>
    /// A laptop asked this desktop to start its windows (its Connect button): the desktop starts them once per request, for a
    /// paired laptop, unless windows already run.
    /// </summary>
    public async Task HandleStartRequestsAsync()
    {
        if (_startsBusy || !Paths.ClientInstalled) return;
        _startsBusy = true;
        try
        {
            var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var laptop in Browser.Snapshot())
            {
                if (!laptop.StartFor.Contains(Settings.Client.UltrasoundId, StringComparer.OrdinalIgnoreCase)) continue;
                asked.Add(laptop.HostId);
                if (_startHandled.Contains(laptop.HostId)) continue;
                var profile = Settings.Client.Hosts.FirstOrDefault(h => h.Paired && string.Equals(h.HostId, laptop.HostId, StringComparison.OrdinalIgnoreCase));
                if (profile == null) continue;
                _startHandled.Add(laptop.HostId);
                if (Client.AnyRunning()) continue;
                Log.Info($"'{laptop.Name}' asked this desktop to start its windows");
                try { await Client.StartAsync(profile); }
                catch (Exception ex) { Log.Error("start request", ex); }
            }
            _startHandled.IntersectWith(asked);
        }
        finally { _startsBusy = false; }
    }

    /// <summary>Starts or stops the sensing services to match the settings. Safe to call at any time.</summary>
    public void ApplySensing()
    {
        // restarts the microphone or speakers when the mode or the chosen device changes
        var listenSig = $"{Settings.Sound.Mode}|{Settings.Sound.InputDeviceId}";
        if (Settings.Host.ListenUltrasound)
        {
            if (!Listener.Running || listenSig != _listenSig) { Listener.Stop(); Listener.Start(Settings.Sound.Mode, Settings.Sound.InputDeviceId); }
            _listenSig = listenSig;
        }
        else { Listener.Stop(); _listenSig = ""; }

        var announceSig = $"{Settings.Sound.Mode}|{Settings.Sound.OutputDeviceId}|{Settings.Client.UltrasoundId}";
        if (Settings.Client.DiscoverUltrasound)
        {
            if (!Announcer.Running || announceSig != _announceSig)
            {
                Announcer.Stop();
                Announcer.Start(Settings.Client.UltrasoundId, Settings.Sound.Mode, Settings.Sound.OutputDeviceId);
            }
            _announceSig = announceSig;
        }
        else { Announcer.Stop(); _announceSig = ""; }
        if (Settings.Client.DiscoverLan) { if (!Browser.Running) Browser.Start(); }
        else Browser.Stop();

        // the desktop shows its presence to the laptops on the LAN, while one of them is paired with it
        if (Paths.ClientInstalled && Settings.Client.DiscoverLan && Settings.Client.Hosts.Any(h => h.Paired)) { if (!Presence.Running) Presence.Start(Settings.Client.UltrasoundId, Environment.MachineName); }
        else Presence.Stop();

        // the laptop watches for the paired desktops and shows the notice (the notice needs the host running)
        if (Paths.HostInstalled && Settings.Host.ShowNotices)
        {
            if (!LaptopPresence.Running) LaptopPresence.Start();
            if (!Notices.Running) Notices.Start();
        }
        else
        {
            Notices.Stop();
            LaptopPresence.Stop();
        }
    }

    /// <summary>The laptops in the order the desktop should connect to them.</summary>
    public List<ClientEntry> ClientEntries() =>
        ClientRanking.Build(Settings.Client.Hosts, Browser.Snapshot(), Settings.Client.ManualOrder, DateTime.Now);

    public void StopSensing()
    {
        Listener.Dispose();
        Announcer.Dispose();
        Browser.Dispose();
        Presence.Dispose();
        Notices.Dispose();
        LaptopPresence.Dispose();
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
        _tick.Tick += (_, _) => { RefreshMenu(); _ = _app.HandleStartRequestsAsync(); };
        _tick.Start();
        app.Notices.Recognized += info => _ui.Post(_ => ShowNotice(info), null);

        app.Updater.Start();
        app.ApplySensing();
        _ = AutoStartAsync(afterUpdate);
        RefreshMenu();
        // first run: nothing configured yet, so show the window instead of only an icon
        if (!File.Exists(Paths.SettingsFile) && !afterUpdate) _form.ShowTab(Paths.ClientInstalled && !Paths.HostInstalled ? "client" : "host");
    }

    private DesktopNotice? _notice;

    /// <summary>The notice for a recognized desktop. It replaces an earlier notice; it does not take the focus.</summary>
    private void ShowNotice(NoticeInfo info)
    {
        if (!_app.Settings.Host.ShowNotices) return;
        _notice?.Close();
        _notice = new DesktopNotice(info, _app.Settings.Host.NoticeSnoozeMinutes,
            id => _app.Notices.ConnectAsync(id),
            id => _app.Notices.Dismiss(id),
            id => _app.Notices.Snooze(id, _app.Settings.Host.NoticeSnoozeMinutes),
            (id, screen, on) =>
            {
                NoticeService.SetScreenEnabled(id, screen, on);
                return _app.Notices.ScreensOfDesktop(id);
            });
        _notice.Show();
    }

    private async Task AutoStartAsync(bool afterUpdate)
    {
        try
        {
            var resumeHost = File.Exists(Path.Combine(Paths.DataDir, "resume-host"));
            var resumeStreams = File.Exists(Path.Combine(Paths.DataDir, "resume-streams"));
            foreach (var f in new[] { "resume-host", "resume-streams" }) try { File.Delete(Path.Combine(Paths.DataDir, f)); } catch { }
            if (Paths.HostInstalled && (_app.Settings.Host.AutoStart || resumeHost)) await _app.Host.StartAsync();
            var defaultHost = _app.Settings.Client.DefaultHost;
            if (Paths.ClientInstalled && resumeStreams && defaultHost is { Paired: true }) await _app.Client.StartAsync(defaultHost);
            else if (Paths.ClientInstalled && _app.Settings.Client.AutoConnect) await AutoConnectOnce();
            if (afterUpdate) Balloon("LANDock updated", $"Now running version {AppInfo.Version}.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("auto start", ex);
            Balloon("LANDock", ex.Message, ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// Auto-connect: waits a few seconds for the LAN to answer, then connects to the first laptop in the order that is paired and
    /// online. Nothing starts when displays already run or when no paired laptop is online.
    /// </summary>
    private async Task AutoConnectOnce()
    {
        var deadline = DateTime.Now.AddSeconds(8);
        ClientEntry? choice = null;
        while (DateTime.Now < deadline && choice == null)
        {
            await Task.Delay(1000);
            choice = ClientRanking.AutoConnectChoice(_app.ClientEntries());
        }
        if (choice?.Profile == null) { Log.Info("auto-connect: no paired laptop is online"); return; }
        if (_app.Client.AnyRunning()) return;
        try
        {
            Log.Info($"auto-connect: {choice.Name} is first in the order and online");
            await _app.Client.StartAsync(choice.Profile);
        }
        catch (Exception ex)
        {
            Log.Error("auto-connect", ex);
            Balloon("LANDock", $"Could not connect to {choice.Name}: {ex.Message}", ToolTipIcon.Warning);
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
        var h = _app.ClientEntries().FirstOrDefault(e => e.Saved && e.Paired && e.Online)?.Profile ?? _app.Settings.Client.DefaultHost;
        try
        {
            if (h == null) throw new InvalidOperationException("no host yet: open the Client tab and add one");
            if (_app.Client.IsRunning(h)) await _app.Client.StopAsync(h); else await _app.Client.StartAsync(h);
        }
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
        _app.StopSensing();
        foreach (var h in _app.Settings.Client.Hosts) { try { await _app.Client.StopAsync(h); } catch { } }
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
