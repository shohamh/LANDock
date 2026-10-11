namespace LANDock.Ultrasound;

/// <summary>
/// Shows how loud the microphone is in the sound band of the mode, split into bars: the level of each bar above the room's
/// noise floor, in dB. It runs beside the decoder and does not decide anything: it is there so you can see that the microphone
/// carries the sound (a played tone lights up its bar).
/// Measured with the Goertzel algorithm on 80 ms Hann windows, every 10 ms, for 16 bars across the band.
/// </summary>
public sealed class BandMeter
{
    public const int Bars = 16;
    private const double WindowSeconds = 0.08, HopSeconds = 0.01;

    private readonly double _low, _high;
    private readonly int _win, _hop;
    private readonly double[] _window, _coef;
    private readonly double[] _floor = new double[Bars];
    private readonly double[] _last = new double[Bars];
    private readonly List<float> _store = new();
    private long _storeStart, _nextStart;

    /// <summary>Latest level of each bar above its floor (dB).</summary>
    public double[] BarDb { get; } = new double[Bars];
    /// <summary>Latest level of the whole band above its floor (dB).</summary>
    public double BandDb { get; private set; }
    /// <summary>
    /// The bars for the display: the highest level of the last second, falling slowly. A short sound (a chirp lasts under a
    /// second) stays visible long enough to be seen, where BarDb would show only the moment of the last measurement.
    /// </summary>
    public double[] HoldDb { get; } = new double[Bars];

    public double Low => _low;
    public double High => _high;

    /// <summary>The frequency of a bar in Hz, for a band from low to high.</summary>
    public static double BarHz(int bar, double low = 15000, double high = 19500) => low + (high - low) * bar / (Bars - 1);

    public BandMeter(double sampleRate, double low = 15000, double high = 19500)
    {
        _low = low;
        _high = high;
        _win = (int)Math.Round(WindowSeconds * sampleRate);
        _hop = (int)Math.Round(HopSeconds * sampleRate);
        _window = new double[_win];
        for (var i = 0; i < _win; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (_win - 1));
        _coef = new double[Bars];
        for (var k = 0; k < Bars; k++) _coef[k] = 2 * Math.Cos(2 * Math.PI * BarHz(k, low, high) / sampleRate);
        Array.Fill(_floor, double.NaN);
    }

    public void Process(ReadOnlySpan<float> mono)
    {
        _store.AddRange(mono.ToArray());
        while (_nextStart + _win <= _storeStart + _store.Count)
        {
            Measure((int)(_nextStart - _storeStart));
            Update();                 // once per 10 ms hop, so the floor follows time, not the number of calls
            _nextStart += _hop;
        }
        var drop = (int)Math.Min(_store.Count, _nextStart - _storeStart);
        if (drop > 0) { _store.RemoveRange(0, drop); _storeStart += drop; }
    }

    private void Measure(int offset)
    {
        for (var k = 0; k < Bars; k++)
        {
            var c = _coef[k];
            double s1 = 0, s2 = 0;
            for (var i = 0; i < _win; i++)
            {
                var s0 = _store[offset + i] * _window[i] + c * s1 - s2;
                s2 = s1; s1 = s0;
            }
            _last[k] = Math.Max(0, s1 * s1 + s2 * s2 - c * s1 * s2);
        }
    }

    private void Update()
    {
        // the floor drops at once to any quieter level and rises very slowly (about 20 s), so a steady tone keeps reading high
        double total = 0, floorTotal = 0;
        for (var k = 0; k < Bars; k++)
        {
            var e = _last[k];
            _floor[k] = double.IsNaN(_floor[k]) ? e : (e < _floor[k] ? e : _floor[k] + (e - _floor[k]) * 0.0005);
            var floor = Math.Max(_floor[k], 1e-12);
            BarDb[k] = 10 * Math.Log10(Math.Max(e, 1e-12) / floor);
            HoldDb[k] = Math.Max(BarDb[k], HoldDb[k] - 0.5);   // about 50 dB per second
            total += e; floorTotal += floor;
        }
        BandDb = 10 * Math.Log10(Math.Max(total, 1e-12) / Math.Max(floorTotal, 1e-12));
    }
}
