using System.Diagnostics;
using LANDock.Client;
using LANDock.Core;
using LANDock.Host;
using LANDock.Update;

namespace LANDock.Ui;

/// <summary>The settings window: Host, Client, Updates and General/About. Changes are applied with "Save".</summary>
public sealed class SettingsForm : Form
{
    private readonly AppServices _app;
    private Settings S => _app.Settings;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1500 };

    // host tab
    private Label _driverNote = null!, _hostState = null!, _pin = null!, _pinInfo = null!, _addresses = null!, _firewall = null!;
    private Button _hostToggle = null!, _newPin = null!, _fwButton = null!;
    private CheckBox _hostAuto = null!, _keepAwake = null!;
    private NumericUpDown _pairMinutes = null!;
    private ListView _instances = null!;

    // client tab
    private TextBox _hostAddress = null!, _clientPin = null!;
    private ComboBox _displayMode = null!;
    private DataGridView _streams = null!;
    private Label _monitors = null!, _clientStatus = null!;
    private Button _pair = null!, _startStreams = null!, _stopStreams = null!;
    private CheckBox _autoStreams = null!;

    // updates tab
    private CheckBox _updEnabled = null!, _updPre = null!;
    private NumericUpDown _updInterval = null!;
    private RadioButton _updAsk = null!, _updAuto = null!;
    private TextBox _updRepo = null!, _updToken = null!, _updNotes = null!;
    private Label _updStatus = null!, _versions = null!;
    private Button _updCheck = null!, _updInstall = null!, _updSkip = null!;

    // general tab
    private CheckBox _startWithWindows = null!;
    private TextBox _logBox = null!;

    public SettingsForm(AppServices app)
    {
        _app = app;
        Text = "LANDock";
        Icon = TrayApp.LoadIcon(32);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(700, 600);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10, 8, 10, 8) };
        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 28) };
        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 28) };
        close.Click += (_, _) => Hide();
        save.Click += (_, _) => SaveSettings(true);
        buttons.Controls.AddRange(new Control[] { close, save });
        AcceptButton = save;

        _tabs.TabPages.Add(BuildHostTab());
        _tabs.TabPages.Add(BuildClientTab());
        _tabs.TabPages.Add(BuildUpdatesTab());
        _tabs.TabPages.Add(BuildGeneralTab());
        Controls.Add(_tabs);
        Controls.Add(buttons);

        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        VisibleChanged += (_, _) => { if (Visible) { LoadSettings(); RefreshAll(); _tick.Start(); } else _tick.Stop(); };
        _tick.Tick += (_, _) => RefreshAll();
        _app.Host.Changed += () => BeginInvokeSafe(RefreshAll);
        _app.Updater.Changed += () => BeginInvokeSafe(RefreshUpdates);
        Log.Line += OnLogLine;
    }

    public void ShowTab(string name)
    {
        Show(); Activate();
        foreach (TabPage t in _tabs.TabPages) if (t.Name == name) _tabs.SelectedTab = t;
    }

    private void BeginInvokeSafe(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { } }

    // =================================================================================================================
    // Host tab
    // =================================================================================================================
    private TabPage BuildHostTab()
    {
        var page = new TabPage("Host (laptop)") { Name = "host", Padding = new Padding(12), AutoScroll = true };
        var col = Column();

        col.Controls.Add(Heading("This computer shares its virtual displays"));
        col.Controls.Add(Note("Run the host on the laptop. The desktop PC then shows the laptop's extra displays on its own monitors."));

        _hostState = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 6, 12, 6) };
        _hostToggle = new Button { AutoSize = true, MinimumSize = new Size(110, 28), Margin = new Padding(0, 2, 0, 2) };
        _hostToggle.Click += async (_, _) => await ToggleHost();
        col.Controls.Add(Row(_hostState, _hostToggle));
        _driverNote = new Label { AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Color.Firebrick, Visible = false };
        col.Controls.Add(_driverNote);

        var pairBox = new GroupBox { Text = "Pairing a desktop PC", AutoSize = true, Padding = new Padding(10), Width = 700 };
        _pin = new Label { AutoSize = true, Font = new Font("Consolas", 22, FontStyle.Bold), Margin = new Padding(0, 0, 16, 0) };
        _pinInfo = new Label { AutoSize = true, Margin = new Padding(0, 14, 16, 0) };
        _newPin = new Button { Text = "New pairing PIN", AutoSize = true, MinimumSize = new Size(120, 28), Margin = new Padding(0, 10, 0, 0) };
        _newPin.Click += (_, _) => { _app.Host.NewPairingWindow(); RefreshAll(); };
        pairBox.Controls.Add(Row(_pin, _pinInfo, _newPin));
        col.Controls.Add(pairBox);
        col.Controls.Add(Note("On the desktop PC enter this PIN under Client > Pair. New desktop PCs can only pair while the window is open."));

        _addresses = new Label { AutoSize = true, MaximumSize = new Size(700, 0) };
        col.Controls.Add(Heading("Addresses of this laptop"));
        col.Controls.Add(_addresses);

        _firewall = new Label { AutoSize = true, Margin = new Padding(0, 8, 12, 0) };
        _fwButton = new Button { Text = "Allow through Windows Firewall…", AutoSize = true, MinimumSize = new Size(200, 28) };
        _fwButton.Click += (_, _) =>
        {
            _fwButton.Enabled = false;
            var ok = Firewall.AddRuleElevated();
            MessageBox.Show(this, ok ? "The firewall rule was added (Private networks)." : "The rule was not added. Windows asks for administrator permission once; it was cancelled or failed.",
                "LANDock", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshAll();
        };
        col.Controls.Add(Row(_firewall, _fwButton));

        _hostAuto = new CheckBox { Text = "Start the host automatically when LANDock starts", AutoSize = true };
        _keepAwake = new CheckBox { Text = "Keep this laptop awake while the host runs (a locked laptop cannot be controlled)", AutoSize = true };
        _pairMinutes = new NumericUpDown { Minimum = 1, Maximum = 120, Width = 60 };
        col.Controls.Add(_hostAuto);
        col.Controls.Add(_keepAwake);
        col.Controls.Add(Row(new Label { Text = "Pairing window:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _pairMinutes, new Label { Text = "minutes after the host starts", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));

        _instances = new ListView { View = View.Details, Width = 700, Height = 100, FullRowSelect = true, GridLines = true };
        _instances.Columns.Add("Display", 120); _instances.Columns.Add("Port", 80); _instances.Columns.Add("State", 160); _instances.Columns.Add("Virtual display", 160);
        col.Controls.Add(Heading("Streams"));
        col.Controls.Add(_instances);

        page.Controls.Add(col);
        return page;
    }

    private async Task ToggleHost()
    {
        _hostToggle.Enabled = false;
        try
        {
            if (_app.Host.Running) await _app.Host.StopAsync();
            else
            {
                SaveSettings(false);
                await _app.Host.StartAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("host", ex);
            MessageBox.Show(this, ex.Message, "LANDock host", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshAll();
    }

    // =================================================================================================================
    // Client tab
    // =================================================================================================================
    private TabPage BuildClientTab()
    {
        var page = new TabPage("Client (desktop PC)") { Name = "client", Padding = new Padding(12), AutoScroll = true };
        var col = Column();
        col.Controls.Add(Heading("This computer shows the laptop's displays"));
        col.Controls.Add(Note("Enter the laptop's address, pair once with the PIN shown on the laptop, then start the displays."));

        _hostAddress = new TextBox { Width = 200 };
        _displayMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        _displayMode.Items.AddRange(new object[] { "fullscreen", "windowed" });
        col.Controls.Add(Row(new Label { Text = "Laptop address:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _hostAddress,
                             new Label { Text = "Display mode:", AutoSize = true, Margin = new Padding(16, 6, 4, 0) }, _displayMode));

        _clientPin = new TextBox { Width = 70, MaxLength = 4 };
        _pair = new Button { Text = "Pair", AutoSize = true, MinimumSize = new Size(90, 28) };
        _pair.Click += async (_, _) => await RunClient(async () =>
        {
            SaveSettings(false);
            var ok = await _app.Client.PairAsync(_clientPin.Text.Trim());
            MessageBox.Show(this, ok ? "All streams are paired." : "Pairing did not work. Is the host running on the laptop, is the PIN current, and is the laptop's firewall open? See the log on the General tab.",
                "LANDock pairing", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        });
        col.Controls.Add(Row(new Label { Text = "Pairing PIN:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _clientPin, _pair));

        _startStreams = new Button { Text = "Start displays", AutoSize = true, MinimumSize = new Size(120, 28) };
        _stopStreams = new Button { Text = "Stop displays", AutoSize = true, MinimumSize = new Size(120, 28) };
        _startStreams.Click += async (_, _) => await RunClient(async () => { SaveSettings(false); await _app.Client.StartAsync(); });
        _stopStreams.Click += async (_, _) => await RunClient(() => _app.Client.StopAsync());
        _autoStreams = new CheckBox { Text = "Start the displays automatically when LANDock starts", AutoSize = true, Margin = new Padding(16, 6, 0, 0) };
        col.Controls.Add(Row(_startStreams, _stopStreams, _autoStreams));
        _clientStatus = new Label { AutoSize = true, MaximumSize = new Size(700, 0) };
        col.Controls.Add(_clientStatus);

        col.Controls.Add(Heading("Streams (one per virtual display of the laptop, left to right)"));
        _streams = new DataGridView
        {
            Width = 700, Height = 150, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Display", ReadOnly = true, FillWeight = 60 });
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Port", HeaderText = "Port", ReadOnly = true, FillWeight = 60 });
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Monitor", HeaderText = "On monitor #", FillWeight = 70 });
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Resolution", HeaderText = "Resolution", FillWeight = 90 });
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Fps", HeaderText = "FPS", FillWeight = 50 });
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bitrate", HeaderText = "Bitrate kbit/s (0 = auto)", FillWeight = 100 });
        var audio = new DataGridViewComboBoxColumn { Name = "Audio", HeaderText = "Sound", FillWeight = 80 };
        audio.Items.AddRange("client", "host");
        _streams.Columns.Add(audio);
        col.Controls.Add(_streams);
        col.Controls.Add(Note("Sound: \"client\" plays on this PC, \"host\" stays on the laptop. Monitor numbers count this PC's monitors from the left."));

        _monitors = new Label { AutoSize = true, MaximumSize = new Size(700, 0) };
        col.Controls.Add(Heading("Monitors of this PC"));
        col.Controls.Add(_monitors);

        page.Controls.Add(col);
        return page;
    }

    private async Task RunClient(Func<Task> work)
    {
        foreach (var b in new[] { _pair, _startStreams, _stopStreams }) b.Enabled = false;
        try { await work(); }
        catch (Exception ex)
        {
            Log.Error("client", ex);
            MessageBox.Show(this, ex.Message, "LANDock client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        foreach (var b in new[] { _pair, _startStreams, _stopStreams }) b.Enabled = true;
        RefreshAll();
    }

    // =================================================================================================================
    // Updates tab
    // =================================================================================================================
    private TabPage BuildUpdatesTab()
    {
        var page = new TabPage("Updates") { Name = "updates", Padding = new Padding(12), AutoScroll = true };
        var col = Column();
        col.Controls.Add(Heading("Keeping LANDock up to date"));
        _versions = new Label { AutoSize = true, MaximumSize = new Size(700, 0) };
        col.Controls.Add(_versions);

        _updEnabled = new CheckBox { Text = "Check for updates automatically", AutoSize = true };
        _updInterval = new NumericUpDown { Minimum = 1, Maximum = 1440, Width = 60 };
        col.Controls.Add(Row(_updEnabled, new Label { Text = "every", AutoSize = true, Margin = new Padding(12, 6, 4, 0) }, _updInterval, new Label { Text = "minutes", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));
        _updAsk = new RadioButton { Text = "Ask me before installing an update", AutoSize = true };
        _updAuto = new RadioButton { Text = "Install updates automatically (host and client parts wait until nothing is streaming)", AutoSize = true };
        col.Controls.Add(_updAsk);
        col.Controls.Add(_updAuto);
        _updPre = new CheckBox { Text = "Include pre-releases", AutoSize = true };
        col.Controls.Add(_updPre);

        _updRepo = new TextBox { Width = 240 };
        col.Controls.Add(Row(new Label { Text = "GitHub repository:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _updRepo));
        _updToken = new TextBox { Width = 300, UseSystemPasswordChar = true };
        col.Controls.Add(Row(new Label { Text = "Access token (optional; only for a private copy of the repository):", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _updToken));

        _updCheck = new Button { Text = "Check now", AutoSize = true, MinimumSize = new Size(110, 28) };
        _updInstall = new Button { Text = "Install update", AutoSize = true, MinimumSize = new Size(120, 28), Visible = false };
        _updSkip = new Button { Text = "Skip this version", AutoSize = true, MinimumSize = new Size(120, 28), Visible = false };
        _updCheck.Click += async (_, _) =>
        {
            _updCheck.Enabled = false; SaveSettings(false);
            try { var c = await _app.Updater.CheckAsync(); if (c is { Any: false }) _updStatus.Text = "LANDock is up to date."; }
            catch (Exception ex) { _updStatus.Text = ex.Message; }
            _updCheck.Enabled = true; RefreshUpdates();
        };
        _updInstall.Click += async (_, _) => await InstallUpdate();
        _updSkip.Click += (_, _) =>
        {
            if (_app.Updater.Latest != null) { S.Updates.SkippedVersion = _app.Updater.Latest.Manifest.Tag; S.Save(); RefreshUpdates(); }
        };
        col.Controls.Add(Row(_updCheck, _updInstall, _updSkip));

        _updStatus = new Label { AutoSize = true, MaximumSize = new Size(700, 0) };
        col.Controls.Add(_updStatus);
        _updNotes = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 700, Height = 130 };
        col.Controls.Add(_updNotes);
        page.Controls.Add(col);
        return page;
    }

    public async Task InstallUpdate()
    {
        var check = _app.Updater.Latest;
        if (check is not { Any: true }) return;
        var busy = (_app.Host.Running && !_app.Host.IsIdle()) || _app.Client.AnyRunning();
        if (busy && MessageBox.Show(this, "A stream is active. Installing the update will interrupt it. Continue?", "LANDock update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _updInstall.Enabled = false;
        var progress = new Progress<string>(s => _updStatus.Text = s);
        var ok = await _app.Updater.ApplyAsync(check, default, progress);
        _updStatus.Text = ok ? "Update installed." : _app.Updater.LastError;
        _updInstall.Enabled = true;
        RefreshAll();
    }

    // =================================================================================================================
    // General tab
    // =================================================================================================================
    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("General / About") { Name = "general", Padding = new Padding(12) };
        var col = Column();
        _startWithWindows = new CheckBox { Text = "Start LANDock when I sign in to Windows (it lives in the notification area)", AutoSize = true };
        col.Controls.Add(_startWithWindows);
        col.Controls.Add(Heading($"LANDock {AppInfo.Version}"));
        col.Controls.Add(Note("Free software (GPL-3.0). Built on Apollo (host), Moonlight (client) and the SudoVDA virtual display driver. See NOTICE.md in the project for credits."));
        var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        links.Controls.Add(Link("Project page", () => OpenUrl($"https://github.com/{S.Updates.Repository}")));
        links.Controls.Add(Link("Open the data folder", () => OpenUrl(Paths.DataDir)));
        col.Controls.Add(links);
        col.Controls.Add(Heading("Log"));
        _logBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Width = 700, Height = 300, Font = new Font("Consolas", 9) };
        col.Controls.Add(_logBox);
        page.Controls.Add(col);
        return page;
    }

    private void OnLogLine(string line)
    {
        BeginInvokeSafe(() =>
        {
            if (_logBox == null || !Visible) return;
            _logBox.AppendText(line + Environment.NewLine);
        });
    }

    // =================================================================================================================
    // load / save / refresh
    // =================================================================================================================
    private void LoadSettings()
    {
        _hostAuto.Checked = S.Host.AutoStart; _keepAwake.Checked = S.Host.KeepAwake; _pairMinutes.Value = S.Host.PairingWindowMinutes;
        _hostAddress.Text = S.Client.HostAddress; _displayMode.SelectedItem = S.Client.DisplayMode; _autoStreams.Checked = S.Client.AutoStartStreams;
        _streams.Rows.Clear();
        foreach (var s in S.Client.Streams)
            _streams.Rows.Add(s.Name, s.Port, s.Monitor, s.Resolution, s.Fps, s.Bitrate, s.Audio == "host" ? "host" : "client");
        _updEnabled.Checked = S.Updates.Enabled; _updInterval.Value = S.Updates.IntervalMinutes;
        _updAsk.Checked = S.Updates.Mode != "auto"; _updAuto.Checked = S.Updates.Mode == "auto";
        _updPre.Checked = S.Updates.IncludePrereleases; _updRepo.Text = S.Updates.Repository; _updToken.Text = S.Updates.Token;
        _startWithWindows.Checked = Startup.IsEnabled();
        _driverNote.Text = VddTool.DriverHelp;
        _driverNote.Visible = Paths.HostInstalled && !VddTool.DriverPresent();   // checked when the window opens, not on every refresh
        _logBox.Text = Log.Tail(300);
        _logBox.SelectionStart = _logBox.TextLength; _logBox.ScrollToCaret();
    }

    public void SaveSettings(bool announce)
    {
        S.Host.AutoStart = _hostAuto.Checked; S.Host.KeepAwake = _keepAwake.Checked; S.Host.PairingWindowMinutes = (int)_pairMinutes.Value;
        S.Client.HostAddress = _hostAddress.Text.Trim(); S.Client.DisplayMode = _displayMode.SelectedItem as string ?? "fullscreen"; S.Client.AutoStartStreams = _autoStreams.Checked;
        var streams = new List<StreamSettings>();
        foreach (DataGridViewRow r in _streams.Rows)
        {
            streams.Add(new StreamSettings
            {
                Name = $"{r.Cells["Name"].Value}", Port = ToInt(r.Cells["Port"].Value, 0), Monitor = Math.Max(1, ToInt(r.Cells["Monitor"].Value, 1)),
                Resolution = $"{r.Cells["Resolution"].Value}".Trim(), Fps = ToInt(r.Cells["Fps"].Value, 60), Bitrate = Math.Max(0, ToInt(r.Cells["Bitrate"].Value, 0)),
                Audio = $"{r.Cells["Audio"].Value}" == "host" ? "host" : "client",
            });
        }
        if (streams.Count > 0) S.Client.Streams = streams;
        S.Updates.Enabled = _updEnabled.Checked; S.Updates.IntervalMinutes = (int)_updInterval.Value;
        S.Updates.Mode = _updAuto.Checked ? "auto" : "ask"; S.Updates.IncludePrereleases = _updPre.Checked;
        S.Updates.Repository = _updRepo.Text.Trim(); S.Updates.Token = _updToken.Text.Trim();
        S.StartWithWindows = _startWithWindows.Checked;
        S.Save();
        Startup.Apply(S.StartWithWindows);
        _app.Updater.Start();
        if (announce) MessageBox.Show(this, "Settings saved.", "LANDock", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static int ToInt(object? v, int fallback) => int.TryParse($"{v}", out var i) ? i : fallback;

    private void RefreshAll()
    {
        if (!Visible && !IsHandleCreated) return;
        var host = _app.Host;
        var hostInstalled = Paths.HostInstalled;
        foreach (var c in new Control[] { _hostToggle, _newPin, _fwButton, _hostAuto, _keepAwake, _pairMinutes }) c.Enabled = hostInstalled;
        _hostState.Text = !hostInstalled ? "The host component is not installed on this computer"
            : host.Starting ? "Host: starting…" : host.Running ? "Host: running" : "Host: stopped";
        _hostToggle.Text = host.Running ? "Stop host" : "Start host";
        _hostToggle.Enabled = hostInstalled && !host.Starting;
        _newPin.Enabled = host.Running;
        _pin.Text = host.Running ? host.Pin : "----";
        var left = host.PairingUntil - DateTime.Now;
        _pinInfo.Text = !host.Running ? "Start the host to get a PIN" : left.TotalSeconds > 0 ? $"pairing open for {left:m\\:ss} more" : "pairing window closed (press New pairing PIN)";
        var addrs = HostSetup.LocalAddresses();
        _addresses.Text = addrs.Count == 0 ? "no network connection found" : string.Join("    ", addrs.Select(a => $"{a.address}  ({a.name})"));
        var fw = hostInstalled && Firewall.RuleExists();
        _firewall.Text = fw ? "Windows Firewall: open for LANDock on Private networks" : "Windows Firewall: not open yet (the desktop PC cannot connect until it is)";
        _fwButton.Visible = !fw;

        _instances.BeginUpdate();
        _instances.Items.Clear();
        foreach (var i in host.Snapshot())
            _instances.Items.Add(new ListViewItem(new[] { i.Label, i.Port.ToString(), i.Up ? "ready" : (host.Running || host.Starting ? "starting" : "stopped"), i.Display ?? "—" }));
        _instances.EndUpdate();

        var clientInstalled = Paths.ClientInstalled;
        foreach (var c in new Control[] { _pair, _startStreams, _stopStreams, _hostAddress, _clientPin, _displayMode, _streams, _autoStreams }) c.Enabled = clientInstalled;
        _monitors.Text = string.Join("\n", ClientService.Monitors().Select(m => $"{m.Index}: {m.Bounds.Width}x{m.Bounds.Height} at ({m.Bounds.X},{m.Bounds.Y}){(m.Primary ? "  [primary]" : "")}"));
        var st = _app.Client.Status();
        _clientStatus.Text = !clientInstalled ? "The client component is not installed on this computer"
            : string.Join("    ", st.Select(s => s.Running ? $"{s.Name}: running on monitor {s.Monitor}" : $"{s.Name}: not running"));
        RefreshUpdates();
    }

    private void RefreshUpdates()
    {
        if (_updStatus == null) return;
        var host = Updater.InstalledHost; var client = Updater.InstalledClient;
        _versions.Text = $"Program {AppInfo.Version}    Host {(host == "" ? "not installed" : host)}    Client {(client == "" ? "not installed" : client)}";
        var u = _app.Updater;
        if (u.Applying) return;
        var latest = u.Latest;
        var any = latest is { Any: true };
        var skipped = any && latest!.Manifest.Tag == S.Updates.SkippedVersion;
        _updInstall.Visible = any; _updSkip.Visible = any && !skipped;
        if (any) _updInstall.Text = "Install " + latest!.Manifest.Tag;
        var when = u.LastChecked == DateTime.MinValue ? "never" : u.LastChecked.ToString("HH:mm:ss");
        _updStatus.Text = u.LastError != "" ? $"Last check {when} failed: {u.LastError}"
            : any ? $"Update available: {latest!.Summary} ({latest.Manifest.Tag}){(skipped ? " — skipped" : "")}. Last check {when}."
            : $"Up to date. Last check {when}.";
        _updNotes.Text = any ? latest!.Manifest.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) : "";
    }

    // =================================================================================================================
    // small layout helpers
    // =================================================================================================================
    private static FlowLayoutPanel Column() => new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(controls);
        return p;
    }

    private Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };
    private Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) };

    private static LinkLabel Link(string text, Action click)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        l.LinkClicked += (_, _) => click();
        return l;
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
