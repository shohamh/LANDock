using LANDock.Host;

namespace LANDock.Ui;

/// <summary>
/// The Host tab's paired desktops: the list, Forget, and for the selected desktop how each virtual display behaves for it
/// (on or off, the largest size, the largest frame rate). The choices are saved with the desktop and apply to its next connection.
/// </summary>
public sealed partial class SettingsForm
{
    private ListView _desktops = null!;
    private Button _forget = null!;
    private Label _desktopsNote = null!;
    private string _desktopsSignature = "";
    private DataGridView _policy = null!;
    private Button _savePolicy = null!;
    private Label _policyNote = null!;

    private void BuildDesktopsBox(FlowLayoutPanel col)
    {
        col.Controls.Add(Heading("Desktop PCs paired with this laptop"));
        _desktops = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = true, Width = 700, Height = 110 };
        _desktops.Columns.Add("Desktop", 260);
        _desktops.Columns.Add("Paired", 200);
        _desktops.SelectedIndexChanged += (_, _) =>
        {
            _forget.Enabled = _desktops.SelectedItems.Count > 0;
            LoadPolicy();
        };
        col.Controls.Add(_desktops);
        _forget = new Button { Text = "Forget selected desktop", AutoSize = true, MinimumSize = new Size(190, 28), Enabled = false };
        _forget.Click += async (_, _) => await ForgetSelectedDesktop();
        _desktopsNote = new Label { AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText };
        col.Controls.Add(Row(_forget));
        col.Controls.Add(_desktopsNote);

        col.Controls.Add(Heading("How the virtual displays work for each screen of the selected desktop"));
        _policy = new DataGridView
        {
            Width = 700, Height = 140, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _policy.Columns.Add(new DataGridViewTextBoxColumn { Name = "Display", HeaderText = "Screen of the desktop", ReadOnly = true, FillWeight = 40 });
        _policy.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "On for this desktop", FillWeight = 60 });
        var size = new DataGridViewComboBoxColumn { Name = "Size", HeaderText = "Largest size", FillWeight = 110 };
        foreach (var s in DisplayPolicies.Sizes) size.Items.Add(s.label);
        _policy.Columns.Add(size);
        var fps = new DataGridViewComboBoxColumn { Name = "Fps", HeaderText = "Largest frame rate", FillWeight = 70 };
        foreach (var f in DisplayPolicies.FrameRates) fps.Items.Add(f.ToString());
        _policy.Columns.Add(fps);
        _policy.Columns.Add(new DataGridViewTextBoxColumn { Name = "Agreed", HeaderText = "Last agreed with this desktop", ReadOnly = true, FillWeight = 120 });
        col.Controls.Add(_policy);
        _savePolicy = new Button { Text = "Save display choices", AutoSize = true, MinimumSize = new Size(170, 28), Enabled = false };
        _savePolicy.Click += (_, _) => SavePolicy();
        _policyNote = new Label { AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText };
        col.Controls.Add(Row(_savePolicy));
        col.Controls.Add(_policyNote);
    }

    /// <summary>The desktop selected in the list, or null.</summary>
    private string? SelectedDesktop() => _desktops.SelectedItems.Count > 0 ? (string)_desktops.SelectedItems[0].Tag! : null;

    /// <summary>Fills the grid with the saved choices of the selected desktop (the defaults for a new one).</summary>
    private void LoadPolicy()
    {
        _policy.Rows.Clear();
        var name = SelectedDesktop();
        _savePolicy.Enabled = name != null;
        if (name == null) { _policyNote.Text = ""; return; }
        var saved = HostIdentity.DisplaysOf(name);
        var screens = HostIdentity.ScreensOf(name) ?? new List<MonitorOffer>();
        // one row per screen the desktop reported: its choice is keyed by the screen (plan-monitor-layout.md, step 4)
        foreach (var s in screens.OrderBy(x => x.X).ThenBy(x => x.Y))
        {
            var p = DisplayPolicies.Choose(saved, s.Id, null);
            var sizeLabel = DisplayPolicies.Sizes.FirstOrDefault(z => z.width == p.MaxWidth && z.height == p.MaxHeight).label
                            ?? DisplayPolicies.Sizes[^1].label;
            var row = _policy.Rows[_policy.Rows.Add(ScreenText(s), p.Enabled, sizeLabel, p.MaxFps.ToString(), "")];
            row.Tag = s.Id;
        }
        RefreshAgreed();
        _policyNote.Text = screens.Count == 0
            ? $"'{name}' has not connected yet, so its screens are not known. They appear here after it connects."
            : saved == null
                ? $"'{name}' has the defaults: every screen on, up to 4K at 60 frames per second. Change them and press Save."
                : $"Choices saved for '{name}'. They apply at its next connection.";
    }

    /// <summary>A screen of the desktop as the settings show it: its number in the desktop's arrangement, size and position.</summary>
    private static string ScreenText(MonitorOffer s) =>
        $"{s.Id.Replace(@"\\.\", "")} {s.Width}x{s.Height} at {s.X},{s.Y}" + (s.Primary ? " (primary)" : "") + (s.Rotation != 0 ? $" rotated {s.Rotation}°" : "");

    /// <summary>The "last agreed" column, from the connections this session (it changes while the host runs).</summary>
    private void RefreshAgreed()
    {
        if (_policy == null) return;
        foreach (DataGridViewRow row in _policy.Rows)
        {
            var a = _app.Host.AgreedOn((string)row.Tag!);
            row.Cells["Agreed"].Value = a != null ? $"{a.Width}x{a.Height} at {a.Fps} fps" : "not used yet";
        }
    }

    private void SavePolicy()
    {
        var name = SelectedDesktop();
        if (name == null) return;
        var list = new List<DisplayPolicy>();
        foreach (DataGridViewRow row in _policy.Rows)
        {
            var size = DisplayPolicies.Sizes.FirstOrDefault(s => s.label == $"{row.Cells["Size"].Value}");
            if (size.label == null) size = DisplayPolicies.Sizes[^1];
            var fps = int.TryParse($"{row.Cells["Fps"].Value}", out var f) ? f : 60;
            list.Add(new DisplayPolicy((string)row.Tag!, Enabled: row.Cells["On"].Value is true, MaxWidth: size.width, MaxHeight: size.height, MaxFps: fps));
        }
        HostIdentity.SetDisplays(name, list);
        _policyNote.Text = $"Saved for '{name}'. The new choices apply at its next connection.";
        _desktopsSignature = "";
        RefreshDesktops();
    }

    /// <summary>Rebuilds the list only when the paired desktops change, so the selection survives the regular refresh.</summary>
    private void RefreshDesktops()
    {
        if (_desktops == null) return;
        var list = HostIdentity.Desktops();
        var signature = string.Join("|", list.Select(d => d.Name + "@" + d.Created));
        if (signature != _desktopsSignature)
        {
            _desktopsSignature = signature;
            _desktops.Items.Clear();
            foreach (var d in list)
                _desktops.Items.Add(new ListViewItem(new[] { d.Name, d.Created }) { Tag = d.Name });
            _forget.Enabled = false;
        }
        _desktopsNote.Text = list.Count == 0
            ? "No desktop has paired yet. Forgetting a desktop revokes its token and removes its Moonlight pairings (the host must be running for the Moonlight part)."
            : "Forgetting a desktop revokes its token and removes its Moonlight pairings (the host must be running for the Moonlight part).";
        RefreshAgreed();
    }

    private async Task ForgetSelectedDesktop()
    {
        if (_desktops.SelectedItems.Count == 0) return;
        var name = (string)_desktops.SelectedItems[0].Tag!;
        if (MessageBox.Show(this, $"Forget '{name}'? It will have to pair again with a PIN before it can connect or stream.", "LANDock",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _forget.Enabled = false;
        try
        {
            var removed = await _app.Host.ForgetDesktopAsync(name);
            MessageBox.Show(this, _app.Host.Running
                ? $"'{name}' is forgotten: its token is revoked and {removed} Moonlight pairing(s) were removed."
                : $"'{name}' is forgotten: its token is revoked. Its Moonlight pairings are removed the next time the host runs.",
                "LANDock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "LANDock", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        _desktopsSignature = "";
        RefreshDesktops();
    }
}
