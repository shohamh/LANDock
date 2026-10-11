using LANDock.Core;
using LANDock.Ultrasound;

namespace LANDock.Tests;

/// <summary>The sound modes and the settings that choose the microphone and the speakers.</summary>
public class SoundTests
{
    [Fact]
    public void Audible_is_the_default_and_unknown_modes_fall_back_to_it()
    {
        Assert.Equal("audible", new Settings().Sound.Mode);
        Assert.Equal(SoundMode.Audible, SoundMode.Normalize("whatever"));
        Assert.Equal(SoundMode.Ultrasound, SoundMode.Normalize("ultrasound"));
    }

    [Fact]
    public void Each_mode_has_its_band_protocol_and_rate_rule()
    {
        var audible = SoundMode.Band(SoundMode.Audible);
        var ultra = SoundMode.Band(SoundMode.Ultrasound);
        Assert.True(audible.high < 8000 && audible.low > 1000, "the audible chirp sits in the speech band");
        Assert.True(ultra.low >= 15000 && ultra.high <= 20000, "the ultrasound band is above 15 kHz");
        Assert.Equal(GgWave.ProtocolAudibleFast, SoundMode.Protocol(SoundMode.Audible));
        Assert.Equal(GgWave.ProtocolUltrasoundFast, SoundMode.Protocol(SoundMode.Ultrasound));
        Assert.True(SoundMode.MinSampleRate(SoundMode.Ultrasound) > SoundMode.MinSampleRate(SoundMode.Audible));
    }

    [Fact]
    public void Audible_announcements_are_less_frequent_than_ultrasound_ones()
    {
        Assert.True(SoundMode.AnnounceSeconds(SoundMode.Audible).min > SoundMode.AnnounceSeconds(SoundMode.Ultrasound).max);
    }

    [Fact]
    public void Sound_choices_survive_a_save_and_load()
    {
        var file = Path.Combine(Path.GetTempPath(), "landock-sound-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new Settings();
            s.Sound.Mode = "ultrasound";
            s.Sound.InputDeviceId = "{0.0.1.00000000}.in";
            s.Sound.OutputDeviceId = "{0.0.0.00000000}.out";
            s.Save(file);
            var back = Settings.Load(file).Sound;
            Assert.Equal("ultrasound", back.Mode);
            Assert.Equal("{0.0.1.00000000}.in", back.InputDeviceId);
            Assert.Equal("{0.0.0.00000000}.out", back.OutputDeviceId);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_bad_sound_mode_in_the_file_is_reset_to_audible()
    {
        var file = Path.Combine(Path.GetTempPath(), "landock-badsound-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, "{ \"sound\": { \"mode\": \"banana\" } }");
            Assert.Equal("audible", Settings.Load(file).Sound.Mode);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_device_that_is_not_connected_is_named_as_such()
    {
        Assert.Equal("automatic", AudioDevices.NameOf("", new List<AudioDevices.Device>()));
        Assert.Equal("(not connected)", AudioDevices.NameOf("gone", new List<AudioDevices.Device>()));
    }
}
