using LANDock.Client;
using LANDock.Core;
using LANDock.Discovery;

namespace LANDock.Ui;

/// <summary>
/// The Client tab: how this PC finds laptops (LAN, ultrasound, automatic connection), the list of laptops in the order they are
/// tried, the displays of the selected laptop, and the buttons that act on them.
/// </summary>
public sealed partial class SettingsForm
{
    private CheckBox _discoverLan = null!, _discoverUltra = null!, _autoConnect = null!;
    private Label _finderStatus = null!;
    private ListView _laptops = null!;
    private Button _pairBtn = null!, _favBtn = null!, _upBtn = null!, _downBtn = null!, _renameBtn = null!, _removeBtn = null!,
                   _addHost = null!, _check = null!, _startStreams = null!, _stopStreams = null!;
    private DataGridView _streams = null!;
    private ComboBox _displayMode = null!;
    private Label _hostInfo = null!, _monitors = null!, _clientStatus = null!;
    private HostProfile? _selected;
    private ClientEntry? _selectedEntry;
    private string _selectedKey = "";
    private List<string> _keys = new();
    private bool _loadingClient;

    private TabPage BuildClientTab()
    {
        var page = new TabPage("Client (desktop PC)") { Name = "client", Padding = new Padding(12), AutoScroll = true };
        var col = Column();

        col.Controls.Add(Heading("Finding laptops"));
        _discoverLan = new CheckBox { Text = "Find laptops on this network (LAN discovery)", AutoSize = true };
        _discoverUltra = new CheckBox { Text = "Let laptops hear this PC by sound (plays its id now and then; off by default: keep off near pets)", AutoSize = true };
        _autoConnect = new CheckBox { Text = "Connect automatically to the first paired laptop that is online (the order below)", AutoSize = true };
        foreach (var c in new[] { _discoverLan, _discoverUltra, _autoConnect })
            c.CheckedChanged += (_, _) => FinderChanged();
        col.Controls.Add(_discoverLan);
        col.Controls.Add(_discoverUltra);
        col.Controls.Add(_autoConnect);
        _finderStatus = new Label { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 4, 0, 4) };
        col.Controls.Add(_finderStatus);

        col.Controls.Add(Heading("Laptops, in the order they are tried"));
        col.Controls.Add(Note("Nearby laptops (heard this PC's sound) come first, then the ones seen most recently, then favorites, then the order you set with Move up and Move down. " +
                              "A laptop is only connected to after the certificate and the PIN check, as before."));
        _laptops = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = true, Width = 700, Height = 170 };
        _laptops.Columns.Add("#", 30);
        _laptops.Columns.Add("Laptop", 170);
        _laptops.Columns.Add("Address", 150);
        _laptops.Columns.Add("Status", 90);
        _laptops.Columns.Add("Last seen", 90);
        _laptops.Columns.Add("Heard you", 120);
        _laptops.Columns.Add("Paired", 60);
        _laptops.SelectedIndexChanged += (_, _) => { if (!_loadingClient) SelectLaptop(); };
        col.Controls.Add(_laptops);

        _pairBtn = Btn("Pair…", PairSelected);
        _favBtn = Btn("★ Favorite", ToggleFavorite);
        _upBtn = Btn("Move up", () => MoveSelected(-1));
        _downBtn = Btn("Move down", () => MoveSelected(1));
        _renameBtn = Btn("Rename…", RenameSelected);
        _removeBtn = Btn("Remove", RemoveSelected);
        _addHost = Btn("Add by address…", AddHost);
        col.Controls.Add(Row(_pairBtn, _favBtn, _upBtn, _downBtn));
        col.Controls.Add(Row(_renameBtn, _removeBtn, _addHost));
        _hostInfo = new Label { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 4, 0, 4) };
        col.Controls.Add(_hostInfo);

        col.Controls.Add(Heading("Displays of the selected laptop"));
        _streams = new DataGridView
        {
            Width = 700, Height = 150, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Display", ReadOnly = true, FillWeight = 60 });
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Port", HeaderText = "Port", ReadOnly = true, FillWeight = 60 });
        var monitor = new DataGridViewComboBoxColumn { Name = "Monitor", HeaderText = "On monitor #", FillWeight = 70 };
        foreach (var m in ClientService.Monitors()) monitor.Items.Add(m.Index);
        _streams.Columns.Add(monitor);
        var audio = new DataGridViewComboBoxColumn { Name = "Audio", HeaderText = "Sound", FillWeight = 70 };
        audio.Items.AddRange("client", "host");
        _streams.Columns.Add(audio);
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Fps", HeaderText = "FPS", FillWeight = 50 });
        _streams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bitrate", HeaderText = "Bitrate kbit/s (0 = auto)", FillWeight = 100 });
        var mode = new DataGridViewComboBoxColumn { Name = "Mode", HeaderText = "Mode", FillWeight = 80 };
        mode.Items.AddRange("", "fullscreen", "windowed");
        _streams.Columns.Add(mode);
        col.Controls.Add(_streams);
        col.Controls.Add(Note("Each display's size is agreed with the laptop at every start: the size of the monitor it goes to (at most 4K). " +
                              "Sound \"client\" plays here, \"host\" stays on the laptop. Monitors are numbered from the left; an empty mode uses the default below."));

        _displayMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        _displayMode.Items.AddRange(new object[] { "fullscreen", "windowed" });
        col.Controls.Add(Row(new Label { Text = "Default mode:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _displayMode));

        _check = Btn("Check connection", CheckConnection);
        _startStreams = Btn("Start displays", StartSelected);
        _stopStreams = Btn("Stop displays", StopSelected);
        col.Controls.Add(Row(_check, _startStreams, _stopStreams));
        _clientStatus = new Label { AutoSize = true, MaximumSize = new Size(700, 0) };
        col.Controls.Add(_clientStatus);

        _monitors = new Label { AutoSize = true, MaximumSize = new Size(700, 0) };
        col.Controls.Add(Heading("Monitors of this PC"));
        col.Controls.Add(_monitors);

        page.Controls.Add(col);
        return page;
    }

    private static Button Btn(string text, Func<Task> act)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 28) };
        b.Click += async (_, _) => { try { await act(); } catch (Exception ex) { MessageBox.Show(ex.Message, "LANDock", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        return b;
    }

    private static Button Btn(string text, Action act) => Btn(text, () => { act(); return Task.CompletedTask; });

    // ---- settings that the finder uses (applied at once) ---------------------------------------------------------------

    private void LoadClient()
    {
        _loadingClient = true;
        _discoverLan.Checked = S.Client.DiscoverLan;
        _discoverUltra.Checked = S.Client.DiscoverUltrasound;
        _autoConnect.Checked = S.Client.AutoConnect;
        _displayMode.SelectedItem = S.Client.DisplayMode;
        _loadingClient = false;
        RefreshClient();
    }

    private void FinderChanged()
    {
        if (_loadingClient) return;
        S.Client.DiscoverLan = _discoverLan.Checked;
        S.Client.DiscoverUltrasound = _discoverUltra.Checked;
        S.Client.AutoConnect = _autoConnect.Checked;
        S.Save();
        _app.ApplySensing();
        RefreshClient();
    }

    /// <summary>Copies the client settings from the form (used when the settings are saved).</summary>
    private void SaveClientFromForm()
    {
        S.Client.DisplayMode = _displayMode.SelectedItem as string ?? "fullscreen";
        CommitStreams();
    }

    // ---- the list ----------------------------------------------------------------------------------------------------

    /// <summary>Rebuilds the list when its order changes; otherwise only the texts are refreshed (so selection and typing survive).</summary>
    private void RefreshLaptops()
    {
        var entries = _app.ClientEntries();
        var keys = entries.Select(e => e.HostId).ToList();
        if (!keys.SequenceEqual(_keys))
        {
            _loadingClient = true;
            _laptops.BeginUpdate();
            _laptops.Items.Clear();
            for (var i = 0; i < entries.Count; i++)
            {
                var item = new ListViewItem((i + 1).ToString()) { Tag = entries[i].HostId };
                for (var c = 0; c < 6; c++) item.SubItems.Add("");
                _laptops.Items.Add(item);
            }
            _keys = keys;
            _loadingClient = false;
            _laptops.EndUpdate();
            var sel = _laptops.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == _selectedKey);
            if (sel != null) { _loadingClient = true; sel.Selected = true; _loadingClient = false; }
        }
        for (var i = 0; i < entries.Count && i < _laptops.Items.Count; i++)
        {
            var e = entries[i];
            var item = _laptops.Items[i];
            item.Text = (i + 1).ToString();
            item.SubItems[1].Text = (e.Favorite ? "★ " : "") + e.Name;
            item.SubItems[2].Text = e.Address == "" ? "" : $"{e.Address}:{e.Port}";
            item.SubItems[3].Text = e.Status;
            item.SubItems[4].Text = e.LastSeen is { } seen ? Ago(seen) : "-";
            item.SubItems[5].Text = e.HeardUsAt is { } at ? $"{e.NearbyDb:0} dB, {Ago(at)}" : "-";
            item.SubItems[6].Text = e.Saved ? (e.Paired ? "yes" : "no") : "no";
        }
    }

    private static string Ago(DateTime t)
    {
        var s = (DateTime.Now - t).TotalSeconds;
        return s < 60 ? $"{s:0} s ago" : s < 3600 ? $"{s / 60:0} min ago" : s < 86400 ? $"{s / 3600:0} h ago" : $"{s / 86400:0} d ago";
    }

    private ClientEntry? Entry(string key) => _app.ClientEntries().FirstOrDefault(e => e.HostId == key);

    private void SelectLaptop()
    {
        CommitStreams();
        _selectedKey = _laptops.SelectedItems.Count > 0 ? (string)_laptops.SelectedItems[0].Tag! : "";
        _selectedEntry = _selectedKey == "" ? null : Entry(_selectedKey);
        _selected = _selectedEntry?.Profile;
        FillStreams();
        RefreshClient();
    }

    private void FillStreams()
    {
        _streams.Rows.Clear();
        if (_selected == null) return;
        foreach (var s in _selected.Streams)
            _streams.Rows.Add(s.Name, s.Port, s.Monitor, s.Audio == "host" ? "host" : "client", s.Fps, s.Bitrate, s.DisplayMode);
    }

    /// <summary>Copies the grid's values into the selected laptop (monitor, sound, frame rate, bitrate and mode of each display).</summary>
    private void CommitStreams()
    {
        if (_selected == null) return;
        for (var i = 0; i < _streams.Rows.Count && i < _selected.Streams.Count; i++)
        {
            var r = _streams.Rows[i];
            var s = _selected.Streams[i];
            LANDock.Client.ClientService.AssignMonitor(s, Math.Max(1, ToInt(r.Cells["Monitor"].Value, s.Monitor)));
            s.Audio = $"{r.Cells["Audio"].Value}" == "host" ? "host" : "client";
            s.Fps = Math.Clamp(ToInt(r.Cells["Fps"].Value, 60), 30, 60);
            s.Bitrate = Math.Max(0, ToInt(r.Cells["Bitrate"].Value, 0));
            s.DisplayMode = $"{r.Cells["Mode"].Value}";
        }
    }

    // ---- actions -----------------------------------------------------------------------------------------------------

    private async Task AddHost()
    {
        using var dlg = new HostDialog(null);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await PairNew(dlg);
    }

    /// <summary>Pair with a laptop that was found on the LAN (or added by address): the dialog starts with its address.</summary>
    private async Task PairSelected()
    {
        if (_selectedEntry == null) return;
        if (_selected != null)
        {
            var h = _selected;
            using var dlg = new HostDialog(h);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var pin = dlg.Pin;
            await RunClient(() => PairAndReport(h, pin));
            return;
        }
        var found = _selectedEntry.Found;
        var prefill = new HostProfile { Name = _selectedEntry.Name, Address = _selectedEntry.Address, ControlPort = _selectedEntry.Port };
        using var newDlg = new HostDialog(null, prefill);
        if (newDlg.ShowDialog(this) != DialogResult.OK) return;
        await PairNew(newDlg);
        _ = found;
    }

    /// <summary>Pairs and tells the user how it went: the result is a message, not only a line in the window.</summary>
    private async Task PairAndReport(HostProfile h, string pin)
    {
        var ok = await _app.Client.PairAsync(h, pin);
        MessageBox.Show(this, ok ? "All streams are paired." : "Pairing did not work. Is the host running on the laptop, is the PIN current, and is the laptop's firewall open? See the log in the General tab.",
            "LANDock pairing", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task PairNew(HostDialog dlg)
    {
        var h = dlg.Profile;
        S.Client.Hosts.Add(h);
        if (S.Client.DefaultHostId == "") S.Client.DefaultHostId = h.Id;
        S.Save();
        _selected = h;
        _selectedKey = h.HostId != "" ? h.HostId : h.Id;
        var pin = dlg.Pin;
        await RunClient(() => PairAndReport(h, pin));
    }

    private void ToggleFavorite()
    {
        if (_selected == null) { MessageBox.Show(this, "Pair with the laptop first: favorites are kept for paired laptops."); return; }
        _selected.Favorite = !_selected.Favorite;
        S.Save();
        RefreshClient();
    }

    /// <summary>Moves the selected laptop up or down in the manual order, relative to the list as it is shown now.</summary>
    private void MoveSelected(int delta)
    {
        var ids = _app.ClientEntries().Select(e => e.HostId).ToList();
        var i = ids.IndexOf(_selectedKey);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        S.Client.ManualOrder = ids;
        S.Save();
        _keys = new List<string>();   // rebuild the list in the new order
        RefreshClient();
    }

    private void RenameSelected()
    {
        if (_selected == null) { MessageBox.Show(this, "Pair with the laptop first: a nickname is kept with the paired laptop."); return; }
        var name = NicknameDialog.Ask(this, _selected.Name);
        if (name == null || name.Trim() == "") return;
        _selected.Name = name.Trim();
        S.Save();
        RefreshClient();
    }

    private async Task RemoveSelected()
    {
        if (_selected == null) return;
        if (MessageBox.Show(this, $"Remove {_selected.Name} from this PC? Its displays stop and the saved pairing is deleted here.", "LANDock",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var h = _selected;
        _selected = null;
        _selectedKey = "";
        await RunClient(() => _app.Client.RemoveAsync(h));
    }

    private async Task CheckConnection()
    {
        if (_selected == null) return;
        var h = _selected;
        var (id, _) = await ClientService.CheckHostAsync(h.Address, h.ControlPort, h.Fingerprint);
        _hostInfo.ForeColor = SystemColors.ControlText;
        _hostInfo.Text = $"{h.Name} is reachable: {id.HostName} (LANDock {id.Version}). Its certificate matches the saved one.";
    }

    private async Task StartSelected()
    {
        if (_selected == null)
        {
            MessageBox.Show(this, "Pair with this laptop first (press Pair…).", "LANDock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        CommitStreams();
        S.Save();
        var h = _selected;
        await RunClient(() => _app.Client.StartAsync(h));
    }

    private async Task StopSelected()
    {
        if (_selected == null) return;
        var h = _selected;
        await RunClient(() => _app.Client.StopAsync(h));
    }

    /// <summary>Runs one client action: buttons are off while it runs, errors are shown under the laptop's details.</summary>
    private async Task RunClient(Func<Task> work)
    {
        var buttons = new[] { _pairBtn, _favBtn, _upBtn, _downBtn, _renameBtn, _removeBtn, _addHost, _check, _startStreams, _stopStreams };
        foreach (var b in buttons) b.Enabled = false;
        string? failure = null;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Error("client", ex);
            failure = ex.Message;
        }
        foreach (var b in buttons) b.Enabled = true;
        _keys = new List<string>();
        _hostInfo.ForeColor = SystemColors.ControlText;
        RefreshClient();
        FillStreams();
        // shown as a box, because the status line is rewritten by the refresh above and the failure would be lost at once
        if (failure != null)
            MessageBox.Show(this, failure, "LANDock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ---- refresh -----------------------------------------------------------------------------------------------------

    private void RefreshClient()
    {
        var installed = Paths.ClientInstalled;
        var hasSelection = installed && _selectedEntry != null;
        foreach (var c in new Control[] { _discoverLan, _discoverUltra, _autoConnect, _laptops, _addHost, _displayMode }) c.Enabled = installed;
        _pairBtn.Enabled = hasSelection;
        _favBtn.Enabled = hasSelection && _selected != null;
        _renameBtn.Enabled = hasSelection && _selected != null;
        _removeBtn.Enabled = hasSelection && _selected != null;
        _upBtn.Enabled = hasSelection;
        _downBtn.Enabled = hasSelection;
        _check.Enabled = hasSelection && _selected != null;
        _startStreams.Enabled = hasSelection && _selected != null;
        _stopStreams.Enabled = hasSelection && _selected != null;
        _streams.Enabled = hasSelection && _selected != null;
        _monitors.Text = string.Join("\n", ClientService.Monitors().Select(m => $"{m.Index}: {m.Bounds.Width}x{m.Bounds.Height} at ({m.Bounds.X},{m.Bounds.Y}){(m.Primary ? "  [primary]" : "")}"));

        RefreshLaptops();
        var found = _app.Browser.Snapshot().Count;
        var sound = !_discoverUltra.Checked ? "sound: off" : _app.Announcer.Problem ?? (_app.Announcer.Running ? $"sound: this PC is announced (last {(_app.Announcer.LastSent == DateTime.MinValue ? "never" : Ago(_app.Announcer.LastSent))})" : "sound: starting");
        _finderStatus.ForeColor = _app.Announcer.Problem != null && _discoverUltra.Checked ? Color.Firebrick : SystemColors.ControlText;
        _finderStatus.Text = (_discoverLan.Checked
            ? $"LAN: {found} laptop(s) found on this network" + (_app.Browser.Problem is { } lp ? " (" + lp + ")" : "")
            : "LAN discovery is off") + "   ·   " + sound;

        if (_selected != null)
        {
            var h = _selected;
            _hostInfo.Text = $"{h.Address}:{h.ControlPort}   host id {(h.HostId == "" ? "-" : h.HostId)}   certificate {(h.Fingerprint == "" ? "not confirmed yet" : h.Fingerprint)}" +
                             (h.LastConnected == "" ? "" : $"   last connected {h.LastConnected}");
            _clientStatus.Text = !installed ? "The client component is not installed on this computer"
                : string.Join("    ", _app.Client.Status(h).Select(s => s.Running ? $"{s.Name}: running on monitor {s.Monitor}" : $"{s.Name}: not running"));
        }
        else if (_selectedEntry != null)
        {
            _hostInfo.Text = $"{_selectedEntry.Name} ({_selectedEntry.Address}:{_selectedEntry.Port}) was found on the LAN and is not paired yet: press Pair… to check its certificate and enter the PIN.";
            _clientStatus.Text = "";
        }
        else
        {
            _hostInfo.Text = _laptops.Items.Count == 0 ? "No laptop found or added yet. Turn on Find laptops on this network, or press Add by address…." : "Select a laptop.";
            _clientStatus.Text = "";
        }
    }

    private void FinderTick() => RefreshClient();
}

/// <summary>A small dialog that asks for a nickname.</summary>
public static class NicknameDialog
{
    public static string? Ask(IWin32Window owner, string current)
    {
        using var f = new Form
        {
            Text = "Nickname", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Font, ClientSize = new Size(360, 110),
        };
        var box = new TextBox { Text = current, Location = new Point(12, 14), Width = 330 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(186, 60), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(267, 60), Width = 75 };
        f.Controls.AddRange(new Control[] { box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
