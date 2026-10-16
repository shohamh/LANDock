using LANDock.Ultrasound;

namespace LANDock.Ui;

/// <summary>The Settings tab's sound section: the sound mode, and the microphone and speakers the app uses.</summary>
public sealed partial class SettingsForm
{
    private ComboBox _soundMode = null!, _inputDevice = null!, _outputDevice = null!;
    private Label _soundNote = null!;
    private bool _loadingSound;

    /// <summary>A device choice in a combo box: an empty id is "automatic".</summary>
    private sealed record DeviceChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private void BuildSoundBox(FlowLayoutPanel col)
    {
        col.Controls.Add(Heading("Sound (how laptops and desktops find each other)"));
        _soundMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
        _soundMode.Items.Add(new DeviceChoice(SoundMode.Audible, "Audible: a short chirp, heard by everyone nearby (default)"));
        _soundMode.Items.Add(new DeviceChoice(SoundMode.Ultrasound, "Ultrasound: inaudible, 15 to 19.5 kHz (many speakers and microphones do not carry it)"));
        _soundMode.DisplayMember = nameof(DeviceChoice.Name);
        _soundMode.SelectedIndexChanged += (_, _) => { if (!_loadingSound) SoundChanged(); };
        col.Controls.Add(Row(new Label { Text = "Mode:", AutoSize = true, Margin = new Padding(0, 6, 8, 0), Width = 110 }, _soundMode));

        _inputDevice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, DisplayMember = nameof(DeviceChoice.Name) };
        _inputDevice.SelectedIndexChanged += (_, _) => { if (!_loadingSound) SoundChanged(); };
        col.Controls.Add(Row(new Label { Text = "Microphone (laptop):", AutoSize = true, Margin = new Padding(0, 6, 8, 0), Width = 110 }, _inputDevice));

        _outputDevice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, DisplayMember = nameof(DeviceChoice.Name) };
        _outputDevice.SelectedIndexChanged += (_, _) => { if (!_loadingSound) SoundChanged(); };
        col.Controls.Add(Row(new Label { Text = "Speakers (desktop):", AutoSize = true, Margin = new Padding(0, 6, 8, 0), Width = 110 }, _outputDevice));

        var refresh = new Button { Text = "Refresh devices", AutoSize = true, MinimumSize = new Size(120, 28) };
        refresh.Click += (_, _) => FillDevicesAsync();
        col.Controls.Add(Row(refresh));
        _soundNote = new Label { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 4, 0, 4) };
        col.Controls.Add(_soundNote);
        col.Controls.Add(Note("Warning: the audible chirp is loud, and ultrasound (15 to 19.5 kHz) is heard by dogs and cats. Keep \"let laptops hear this PC\" off and " +
                              "do not listen near pets. Both are off by default. " +
                              "The laptop listens on its microphone and the desktop plays through its speakers, so both must be real devices in the room. " +
                              "Virtual devices (Steam Streaming, virtual cables) do not reach the air; \"automatic\" avoids them."));
    }

    private List<AudioDevices.Device> _inputs = new(), _outputs = new();

    private void LoadSound()
    {
        _loadingSound = true;
        _soundMode.SelectedIndex = S.Sound.Mode == SoundMode.Ultrasound ? 1 : 0;
        _loadingSound = false;
        FillDevicesAsync();
    }

    /// <summary>Lists the microphones and speakers of this PC (asking Windows can take a while, so it is done in the background),
    /// keeping the saved choice selected (or noting that it is not connected).</summary>
    private void FillDevicesAsync()
    {
        Bg(() => (AudioDevices.Inputs(), AudioDevices.Outputs()), d =>
        {
            (_inputs, _outputs) = d;
            _loadingSound = true;
            FillCombo(_inputDevice, _inputs, S.Sound.InputDeviceId);
            FillCombo(_outputDevice, _outputs, S.Sound.OutputDeviceId);
            _loadingSound = false;
            RefreshSoundNote();
        });
    }

    private static void FillCombo(ComboBox box, List<AudioDevices.Device> devices, string savedId)
    {
        box.Items.Clear();
        box.Items.Add(new DeviceChoice("", "Automatic"));
        foreach (var d in devices) box.Items.Add(new DeviceChoice(d.Id, d.Name));
        if (savedId != "" && !devices.Any(d => d.Id == savedId)) box.Items.Add(new DeviceChoice(savedId, "(not connected right now)"));
        var idx = 0;
        for (var i = 0; i < box.Items.Count; i++) if (((DeviceChoice)box.Items[i]!).Id == savedId) idx = i;
        box.SelectedIndex = idx;
    }

    /// <summary>A change is saved at once and applied: the microphone or speakers restart with the new choice.</summary>
    private void SoundChanged()
    {
        S.Sound.Mode = ((DeviceChoice)_soundMode.SelectedItem!).Id;
        S.Sound.InputDeviceId = (_inputDevice.SelectedItem as DeviceChoice)?.Id ?? "";
        S.Sound.OutputDeviceId = (_outputDevice.SelectedItem as DeviceChoice)?.Id ?? "";
        S.Save();
        ApplySensingBg();
        _meter.SetBand(SoundMode.Band(S.Sound.Mode).low, SoundMode.Band(S.Sound.Mode).high);
        RefreshSoundNote();
    }

    private void RefreshSoundNote()
    {
        if (_soundNote == null) return;
        var inputs = _inputs.Count;
        var outputs = _outputs.Count;
        _soundNote.ForeColor = SystemColors.GrayText;
        _soundNote.Text = $"Mode: {SoundMode.Label(S.Sound.Mode)}. {inputs} microphone(s) and {outputs} speaker output(s) found on this PC.";
    }
}
