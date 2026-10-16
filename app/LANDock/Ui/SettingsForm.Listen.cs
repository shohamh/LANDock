using LANDock.Core;
using LANDock.Host;
using LANDock.Ultrasound;

namespace LANDock.Ui;

/// <summary>The microphone: the listen setting (Settings tab), and the level bars and what is being heard (Laptop tab).</summary>
public sealed partial class SettingsForm
{
    private CheckBox _listen = null!;
    private LevelMeter _meter = null!;
    private Label _listenStatus = null!;
    private bool _loadingListen;
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 200 };

    /// <summary>The Settings tab's microphone checkbox.</summary>
    private void BuildListenSetting(FlowLayoutPanel col)
    {
        _listen = new CheckBox { Text = "Listen on the microphone for the sound of nearby desktops (off by default; the Laptop tab shows what it hears)", AutoSize = true };
        _listen.CheckedChanged += (_, _) =>
        {
            if (_loadingListen) return;
            S.Host.ListenUltrasound = _listen.Checked;
            S.Save();
            ApplySensingBg();
            RefreshListen();
        };
        col.Controls.Add(_listen);
    }

    /// <summary>The Laptop tab's view of the microphone: the level bars and what is being heard.</summary>
    private void BuildListenBox(FlowLayoutPanel col)
    {
        col.Controls.Add(Heading("Nearby desktops (microphone)"));
        col.Controls.Add(Note("Turn listening on in the Settings tab. The bars show the microphone's level in the band of the sound mode. " +
                              "A desktop that plays its id lights up the bars, and the laptop then knows that desktop is nearby. " +
                              "Nothing is recorded or stored."));
        _meter = new LevelMeter { Width = 700 };
        col.Controls.Add(_meter);
        _listenStatus = new Label { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 4, 0, 4) };
        col.Controls.Add(_listenStatus);
        _meterTimer.Tick += (_, _) => { _meter.SetValues(_app.Listener.HoldDb()); RefreshListen(); };
    }

    private void LoadListen()
    {
        _loadingListen = true;
        _listen.Checked = S.Host.ListenUltrasound;
        _loadingListen = false;
        _meter.SetBand(SoundMode.Band(S.Sound.Mode).low, SoundMode.Band(S.Sound.Mode).high);
        RefreshListen();
    }

    /// <summary>Says what the microphone is doing, and what it means when it is not hearing anything.</summary>
    private void RefreshListen()
    {
        if (_listenStatus == null) return;
        var l = _app.Listener;
        if (!_listen.Checked)
        {
            _listenStatus.ForeColor = SystemColors.GrayText;
            _listenStatus.Text = "Off: the microphone is not used.";
            return;
        }
        if (l.Problem != null)
        {
            _listenStatus.ForeColor = Color.Firebrick;
            _listenStatus.Text = l.Problem;
            return;
        }
        if (!l.Running)
        {
            _listenStatus.ForeColor = SystemColors.GrayText;
            _listenStatus.Text = "Starting the microphone...";
            return;
        }
        var silent = l.LastAudio == DateTime.MinValue || (DateTime.Now - l.LastAudio).TotalSeconds > 3;
        if (!silent && l.RmsDbfs < -100)
        {
            // buffers arrive but carry (almost) no sound: the input level is at zero or the microphone is not picking up the room
            _listenStatus.ForeColor = Color.Firebrick;
            _listenStatus.Text = $"'{l.Device}' delivers almost no sound ({l.RmsDbfs:0} dBFS). In Windows Settings > System > Sound, " +
                                 "check the input level of this microphone, turn off its noise suppression and boost in its properties, " +
                                 "or pick another microphone in the Settings tab.";
            return;
        }
        if (silent)
        {
            _listenStatus.ForeColor = Color.Firebrick;
            _listenStatus.Text = $"No sound from '{l.Device}'. Check that the microphone is not muted, and that Windows Settings > Privacy & security > " +
                                 "Microphone lets desktop apps use it.";
            return;
        }
        _listenStatus.ForeColor = SystemColors.ControlText;
        var heard = Heard.Snapshot()
            .Where(kv => (DateTime.Now - kv.Value.at).TotalSeconds < 60)
            .Select(kv => $"{DesktopName(kv.Key)} ({(DateTime.Now - kv.Value.at).TotalSeconds:0} s ago, {kv.Value.db:0} dB)");
        var heardText = string.Join(", ", heard);
        _listenStatus.Text = $"Listening on '{l.Device}' ({SoundMode.Label(l.Mode)}). Band level {l.BandDb:0} dB above the floor." +
                             (heardText == "" ? " No desktop heard yet." : " Heard: " + heardText + ".");
    }

    private string DesktopName(string ultraId)
    {
        // from the list the last refresh read: this runs every 200 ms and must not read a file
        var d = _desktopsCache.FirstOrDefault(x => x.UltraId.Equals(ultraId, StringComparison.OrdinalIgnoreCase));
        return d != null ? d.Name : ultraId;
    }
}
