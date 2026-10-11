using LANDock.Ultrasound;

namespace LANDock.Tests;

/// <summary>ggwave through our C# wrapper: a payload sent as sound must come back out of the decoder.</summary>
[Trait("Category", "NeedsGgWave")]
public class GgWaveTests
{
    private static byte[] Id(int seed) => Enumerable.Range(0, GgWave.PayloadBytes).Select(i => (byte)(seed * 37 + i * 91)).ToArray();

    /// <summary>Feeds samples to the decoder in chunks of varying size, the way the microphone delivers them.</summary>
    private static List<byte[]> Decode(GgWave.Instance rx, float[] samples, int seed)
    {
        var rnd = new Random(seed);
        var found = new List<byte[]>();
        var pos = 0;
        while (pos < samples.Length)
        {
            var n = Math.Min(samples.Length - pos, rnd.Next(300, 2000));
            found.AddRange(rx.Decode(samples.AsSpan(pos, n)));
            pos += n;
        }
        return found;
    }

    private static float[] Scene(float[] wave, int lead, int tail, double noise, int seed)
    {
        var rnd = new Random(seed);
        var all = new float[lead + wave.Length + tail];
        for (var i = 0; i < all.Length; i++) all[i] = (float)((rnd.NextDouble() * 2 - 1) * noise);
        for (var i = 0; i < wave.Length; i++) all[lead + i] += wave[i];
        return all;
    }

    [Theory]
    [InlineData(GgWave.ProtocolUltrasoundFast, 48000)]
    [InlineData(GgWave.ProtocolUltrasoundNormal, 48000)]
    [InlineData(GgWave.ProtocolUltrasoundFastest, 48000)]
    [InlineData(GgWave.ProtocolUltrasoundFast, 44100)]
    [InlineData(GgWave.ProtocolAudibleNormal, 48000)]
    [InlineData(GgWave.ProtocolAudibleFast, 48000)]
    [InlineData(GgWave.ProtocolAudibleFastest, 48000)]
    [InlineData(GgWave.ProtocolAudibleFast, 44100)]
    public void A_desktop_id_comes_back_out_of_the_decoder(int protocol, double rate)
    {
        var id = Id(protocol + (int)rate % 13);
        using var tx = new GgWave.Instance(rate, rx: false, tx: true);
        using var rx = new GgWave.Instance(rate, rx: true, tx: false);
        var wave = tx.Encode(id, protocol, 40);
        var scene = Scene(wave, (int)(0.6 * rate), (int)(0.8 * rate), 0.003, protocol);
        var found = Decode(rx, scene, protocol);
        Assert.Contains(found, f => f.SequenceEqual(id));
    }

    [Fact]
    public void Two_ids_one_after_the_other_are_both_heard()
    {
        const double rate = 48000;
        var a = Id(3); var b = Id(5);
        using var tx = new GgWave.Instance(rate, rx: false, tx: true);
        using var rx = new GgWave.Instance(rate, rx: true, tx: false);
        var gap = new float[(int)(0.5 * rate)];
        var stream = tx.Encode(a, GgWave.ProtocolUltrasoundFast, 40).Concat(gap).Concat(tx.Encode(b, GgWave.ProtocolUltrasoundFast, 40)).ToArray();
        var found = Decode(rx, Scene(stream, (int)(0.4 * rate), (int)(0.8 * rate), 0.003, 11), 11);
        Assert.Contains(found, f => f.SequenceEqual(a));
        Assert.Contains(found, f => f.SequenceEqual(b));
    }

    [Fact]
    public void Noise_alone_gives_no_id()
    {
        const double rate = 48000;
        using var rx = new GgWave.Instance(rate, rx: true, tx: false);
        Assert.Empty(Decode(rx, Scene(Array.Empty<float>(), 0, (int)(4 * rate), 0.01, 5), 5));
    }
}

/// <summary>The band meter: a tone at a bar's frequency must light up that bar.</summary>
public class BandMeterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(15)]
    public void A_tone_lights_up_its_own_bar(int bar)
    {
        const double rate = 48000;
        var meter = new BandMeter(rate);
        var quiet = new float[(int)(1.0 * rate)];
        var rnd = new Random(1);
        for (var i = 0; i < quiet.Length; i++) quiet[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.001);
        meter.Process(quiet);
        var tone = new float[(int)(0.5 * rate)];
        var w = 2 * Math.PI * BandMeter.BarHz(bar);
        for (var i = 0; i < tone.Length; i++) tone[i] = (float)(0.2 * Math.Sin(w * i / rate));
        meter.Process(tone);
        var db = meter.BarDb;
        var best = Array.IndexOf(db, db.Max());
        Assert.True(best == bar, "bars: " + string.Join(", ", db.Select(v => v.ToString("0"))));
        Assert.True(db[bar] > 10, $"bar {bar} is only {db[bar]:0.0} dB above the floor");
        Assert.True(db.Where((_, k) => k != bar).All(v => v < 3), "other bars should stay near the floor");
    }
}
