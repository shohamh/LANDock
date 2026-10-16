using System.Text.RegularExpressions;
using LANDock.Client;
using LANDock.Core;

namespace LANDock.Ui;

/// <summary>
/// Adds a laptop or pairs again with one. The address, the control port and a "Check" button come first: Check asks the
/// laptop who it is and shows its certificate fingerprint, which the user compares with the laptop's Laptop tab. Only then
/// can the PIN be entered and paired. For a host that is already known, its saved fingerprint is checked too.
/// </summary>
public sealed class HostDialog : Form
{
    private readonly HostProfile _profile;
    private readonly bool _isNew;
    private readonly TextBox _name = new() { Width = 200 };
    private readonly TextBox _address = new() { Width = 260 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 49900, Width = 80 };
    private readonly TextBox _pin = new() { Width = 80, MaxLength = 4 };
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 8, 0, 8) };
    private readonly Button _check = new() { Text = "Check host", AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly Button _pair = new() { Text = "Pair", AutoSize = true, MinimumSize = new Size(110, 28), Enabled = false };
    private string _fingerprint = "";

    /// <summary>The profile to save: with the address, port and fingerprint the user confirmed.</summary>
    public HostProfile Profile => _profile;
    public string Pin => _pin.Text.Trim();
    public bool IsNew => _isNew;

    /// <param name="existing">A saved laptop to pair again (its address and fingerprint are kept).</param>
    /// <param name="prefill">For a new laptop: where it was found (its name, address and control port).</param>
    public HostDialog(HostProfile? existing, HostProfile? prefill = null)
    {
        _isNew = existing == null;
        _profile = existing ?? new HostProfile();
        if (existing == null && prefill != null)
        {
            _profile.Name = prefill.Name;
            _profile.Address = prefill.Address;
            _profile.ControlPort = prefill.ControlPort;
        }
        Text = _isNew ? "Add a host" : $"Pair again: {_profile.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        _name.Text = _profile.Name;
        _address.Text = _profile.Address;
        _port.Value = Math.Clamp(_profile.ControlPort, 1, 65535);
        _address.ReadOnly = !_isNew;   // a saved host keeps its address; remove and add it again to change it
        _port.Enabled = _isNew;
        _fingerprint = _profile.Fingerprint;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 6 };
        layout.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(new Label { Text = "Address (IP or name):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
        layout.Controls.Add(_address, 1, 1);
        layout.Controls.Add(new Label { Text = "Control port:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 2);
        var portRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        portRow.Controls.Add(_port);
        portRow.Controls.Add(new Label { Text = "the laptop shows its port on the Laptop tab (49900 unless taken)", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 6, 0, 0) });
        layout.Controls.Add(portRow, 1, 2);
        var checkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        checkRow.Controls.Add(_check);
        layout.Controls.Add(checkRow, 1, 3);
        layout.Controls.Add(_info, 1, 4);
        layout.Controls.Add(new Label { Text = "PIN from the laptop:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 5);
        var pinRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        pinRow.Controls.Add(_pin);
        pinRow.Controls.Add(_pair);
        layout.Controls.Add(pinRow, 1, 5);
        Controls.Add(layout);

        var close = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28), Dock = DockStyle.None };
        layout.Controls.Add(close, 1, 6);
        CancelButton = close;

        _check.Click += async (_, _) => await CheckAsync();
        _pair.Click += (_, _) => Accept();
        _pin.TextChanged += (_, _) => _pair.Enabled = _fingerprint != "" && Regex.IsMatch(_pin.Text.Trim(), @"^\d{4}$");

        if (!_isNew)
        {
            _info.Text = $"Saved fingerprint: {_profile.Fingerprint}. Check the host to confirm it still matches.";
        }
    }

    private async Task CheckAsync()
    {
        _check.Enabled = false;
        _pair.Enabled = false;
        _info.ForeColor = SystemColors.ControlText;
        try
        {
            var address = _address.Text.Trim();
            var port = (int)_port.Value;
            if (address == "") { _info.Text = "Enter the laptop's address first."; return; }
            var pinned = _isNew ? null : _profile.Fingerprint;
            var (id, fp) = await ClientService.CheckHostAsync(address, port, pinned);
            _fingerprint = fp;
            _info.Text = $"Found: {id.HostName} (LANDock {id.Version})\n" +
                         $"Certificate fingerprint: {fp}\n" +
                         "Compare it with the fingerprint shown on the laptop's Laptop tab. " +
                         (id.PairingOpen ? "Pairing is open: enter the PIN." : "Pairing is closed on the laptop: press New pairing PIN there first.");
            _pair.Enabled = id.PairingOpen && Regex.IsMatch(_pin.Text.Trim(), @"^\d{4}$");
            if (!_isNew && string.IsNullOrEmpty(_name.Text)) _name.Text = id.HostName;
        }
        catch (ControlException ex)
        {
            _info.ForeColor = Color.Firebrick;
            _info.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _info.ForeColor = Color.Firebrick;
            _info.Text = "Could not check the host: " + ex.Message;
        }
        finally { _check.Enabled = true; }
    }

    private void Accept()
    {
        if (!Regex.IsMatch(_pin.Text.Trim(), @"^\d{4}$")) { _info.Text = "The PIN is the 4 digits shown on the laptop."; return; }
        _profile.Name = _name.Text.Trim();
        if (_isNew)
        {
            _profile.Address = _address.Text.Trim();
            _profile.ControlPort = (int)_port.Value;
        }
        _profile.Fingerprint = _fingerprint;
        DialogResult = DialogResult.OK;
        Close();
    }
}
