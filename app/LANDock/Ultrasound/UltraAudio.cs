using System.Collections.Concurrent;
using LANDock.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LANDock.Ultrasound;

/// <summary>What the laptop has heard recently: desktop sound id -> when, and how loud the band was.</summary>
public static class Heard
{
    private static readonly ConcurrentDictionary<string, (DateTime at, double db)> Table = new(StringComparer.OrdinalIgnoreCase);

    public static void Report(string id, double db) => Table[id] = (DateTime.Now, db);

    public static (DateTime at, double db)? Last(string id) => Table.TryGetValue(id, out var v) ? v : null;

    public static IReadOnlyDictionary<string, (DateTime at, double db)> Snapshot() => new Dictionary<string, (DateTime, double)>(Table);

    public static void Clear() => Table.Clear();
}

/// <summary>
/// Listens on the microphone for desktop ids (the laptop side). Runs only while the setting "listen for nearby desktops" is on.
/// ggwave decodes the ids; the band meter gives the bars the Host tab shows, so you can see the microphone picking up the sound.
/// </summary>
public sealed class UltraListener : IDisposable
{
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private GgWave.Instance? _rx;
    private BandMeter? _meter;
    private double[] _bars = new double[BandMeter.Bars];
    private double _band;
    private double _rmsDb = -120;
    private DateTime _lastAudio = DateTime.MinValue;
    private long _buffers;

    /// <summary>The loudness of the whole microphone signal (all frequencies), in dBFS: a plain check that it hears anything.</summary>
    public double RmsDbfs { get { lock (_gate) return _rmsDb; } }

    public bool Running => _capture != null;
    /// <summary>Why it is not listening (null when it is).</summary>
    public string? Problem { get; private set; }
    public string? Device { get; private set; }
    public string Mode { get; private set; } = SoundMode.Audible;
    public (double low, double high) Band { get; private set; } = SoundMode.Band(SoundMode.Audible);
    public double SampleRate { get; private set; }
    public event Action<byte[]>? IdHeard;

    /// <summary>Latest level of each bar above the noise floor (dB).</summary>
    public double[] BarDb() { lock (_gate) return (double[])_bars.Clone(); }
    /// <summary>The bars with their hold: the peak of the last second, for the display.</summary>
    public double[] HoldDb() { lock (_gate) return (double[])_hold.Clone(); }
    private double[] _hold = new double[BandMeter.Bars];
    public double BandDb { get { lock (_gate) return _band; } }
    /// <summary>When the last audio buffer arrived. No buffers while running means the microphone is blocked.</summary>
    public DateTime LastAudio { get { lock (_gate) return _lastAudio; } }
    public long Buffers { get { lock (_gate) return _buffers; } }

    /// <param name="mode">"audible" or "ultrasound" (see SoundMode).</param>
    /// <param name="inputId">The microphone's device id; empty for the default one.</param>
    public void Start(string mode, string? inputId)
    {
        Stop();
        mode = SoundMode.Normalize(mode);
        Mode = mode;
        Band = SoundMode.Band(mode);
        if (!GgWave.Available) { Problem = "ggwave.dll is missing next to LANDock.exe (see scripts/build-ggwave.sh)"; return; }
        try
        {
            var device = AudioDevices.Input(inputId);
            Device = device.FriendlyName;
            var capture = new WasapiCapture(device);
            var fmt = capture.WaveFormat;
            SampleRate = fmt.SampleRate;
            if (fmt.SampleRate < SoundMode.MinSampleRate(mode))
            {
                capture.Dispose();
                Problem = $"the microphone records at {fmt.SampleRate} Hz; {SoundMode.Label(mode)} needs at least {SoundMode.MinSampleRate(mode) / 1000} kHz";
                return;
            }
            _rx = new GgWave.Instance(fmt.SampleRate, rx: true, tx: false);
            _meter = new BandMeter(fmt.SampleRate, Band.low, Band.high);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) => { if (e.Exception != null) Problem = "the microphone stopped: " + e.Exception.Message; };
            capture.StartRecording();
            _capture = capture;
            Problem = null;
            Log.Info($"listening for nearby desktops on '{Device}' ({fmt.SampleRate} Hz, {SoundMode.Label(mode)})");
        }
        catch (Exception ex)
        {
            Problem = "no microphone: " + ex.Message;
            Log.Error("sound listening", ex);
            Stop();
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture?.WaveFormat;
        var rx = _rx;
        var meter = _meter;
        if (fmt == null || rx == null || meter == null) return;
        if (_buffers == 10)   // once: the stream format and its first bytes, for diagnosis
        {
            var head = string.Join(" ", e.Buffer.Take(Math.Min(e.BytesRecorded, 24)).Select(b => b.ToString("X2")));
            var nonZero = e.Buffer.Take(e.BytesRecorded).Count(b => b != 0);
            Log.Info($"capture format {fmt.Encoding} {fmt.BitsPerSample} bit, {fmt.Channels} ch, block {fmt.BlockAlign}; first buffer {e.BytesRecorded} bytes, {nonZero} non-zero: {head}");
        }
        var mono = ToMono(e.Buffer, e.BytesRecorded, fmt);
        if (mono.Length == 0) return;
        meter.Process(mono);
        var ids = rx.Decode(mono);
        double sq = 0;
        foreach (var v in mono) sq += v * v;
        var rms = 10 * Math.Log10(Math.Max(sq / mono.Length, 1e-14));
        lock (_gate)
        {
            _rmsDb = rms;
            Array.Copy(meter.BarDb, _bars, _bars.Length);
            Array.Copy(meter.HoldDb, _hold, _hold.Length);
            _band = meter.BandDb;
            _lastAudio = DateTime.Now;
            _buffers++;
        }
        foreach (var id in ids)
        {
            var strongest = _bars.Max();
            Heard.Report(Convert.ToHexString(id), strongest);
            Log.Info($"heard desktop {Convert.ToHexString(id)} ({strongest:0} dB above the floor)");
            IdHeard?.Invoke(id);
        }
    }

    /// <summary>The samples as mono floats (channels are averaged; 16, 24 and 32-bit PCM and 32-bit float are handled).</summary>
    public static float[] ToMono(byte[] buffer, int count, WaveFormat fmt)
    {
        var bytesPer = fmt.BitsPerSample / 8;
        var channels = Math.Max(1, fmt.Channels);
        var frames = count / (bytesPer * channels);
        var mono = new float[frames];
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * bytesPer;
                sum += isFloat && bytesPer == 4 ? BitConverter.ToSingle(buffer, o)
                     : bytesPer == 2 ? BitConverter.ToInt16(buffer, o) / 32768.0
                     : bytesPer == 3 ? ((buffer[o] | buffer[o + 1] << 8 | buffer[o + 2] << 16) << 8 >> 8) / 8388608.0
                     : BitConverter.ToInt32(buffer, o) / 2147483648.0;
            }
            mono[f] = (float)(sum / channels);
        }
        return mono;
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        _rx?.Dispose();
        _rx = null;
        _meter = null;
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        c.Dispose();
        lock (_gate) { Array.Clear(_bars); _band = 0; }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// The desktop side: plays this PC's id as sound every so often, with random gaps so two desktops rarely overlap,
/// through the chosen speakers at a level the app sets. System volume is not changed.
/// </summary>
public sealed class UltraAnnouncer : IDisposable
{
    private WasapiOut? _out;
    private SampleQueue? _queue;
    private GgWave.Instance? _tx;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private double _rate;

    public bool Running => _out != null;
    public string? Problem { get; private set; }
    public string? Device { get; private set; }
    public string Mode { get; private set; } = SoundMode.Audible;
    public DateTime LastSent { get; private set; } = DateTime.MinValue;

    /// <summary>Diagnostics: how often the speakers asked for sound, and how much of our sound they took.</summary>
    public string Diagnostics => _queue is { } q ? $"device asked {q.Reads} times, took {q.Taken} samples, state {_out?.PlaybackState}" : "not playing";

    /// <summary>
    /// Opens the speakers. With an id it also plays the id now and then; with null it only plays what EnqueueTone adds.
    /// </summary>
    /// <param name="idHex">The desktop's id (16 hex characters), or null.</param>
    /// <param name="mode">"audible" or "ultrasound" (see SoundMode).</param>
    /// <param name="outputId">The speakers' device id; empty for a physical output chosen automatically.</param>
    public void Start(string? idHex, string mode = SoundMode.Audible, string? outputId = null)
    {
        Stop();
        mode = SoundMode.Normalize(mode);
        Mode = mode;
        if (idHex != null && !GgWave.Available) { Problem = "ggwave.dll is missing next to LANDock.exe (see scripts/build-ggwave.sh)"; return; }
        try
        {
            var id = idHex == null ? Array.Empty<byte>() : Convert.FromHexString(idHex);
            if (idHex != null && id.Length != GgWave.PayloadBytes) throw new ArgumentException("the desktop's sound id is not 8 bytes");
            var device = AudioDevices.Output(outputId);
            Device = device.FriendlyName;
            var mix = device.AudioClient.MixFormat;
            _rate = mix.SampleRate;
            if (mix.SampleRate < SoundMode.MinSampleRate(mode))
            {
                Problem = $"the speakers play at {mix.SampleRate} Hz; {SoundMode.Label(mode)} needs at least {SoundMode.MinSampleRate(mode) / 1000} kHz";
                return;
            }
            _tx = idHex == null ? null : new GgWave.Instance(mix.SampleRate, rx: false, tx: true);
            _queue = new SampleQueue(mix.SampleRate, mix.Channels);
            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            _out.Init(_queue);
            _out.Play();
            Problem = null;
            if (idHex == null) return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var tx = _tx ?? throw new InvalidOperationException("the ultrasound encoder is not loaded");
            var queue = _queue;
            var protocol = SoundMode.Protocol(mode);
            var volume = SoundMode.Volume(mode);
            var (minGap, maxGap) = SoundMode.AnnounceSeconds(mode);
            _loop = Task.Run(async () =>
            {
                var rnd = new Random();
                while (!ct.IsCancellationRequested)
                {
                    queue.Enqueue(tx.Encode(id, protocol, volume));
                    LastSent = DateTime.Now;
                    try { await Task.Delay(TimeSpan.FromSeconds(minGap + rnd.NextDouble() * (maxGap - minGap)), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }, ct);
            Log.Info($"announcing this PC by sound ({SoundMode.Label(mode)}, id {idHex}) on '{Device}'");
        }
        catch (Exception ex)
        {
            Problem = "cannot play sound: " + ex.Message;
            Log.Error("sound announcing", ex);
            Stop();
        }
    }

    /// <summary>Plays a plain tone for the given time (for testing the speakers and microphone at one frequency).</summary>
    public void EnqueueTone(double hz, double seconds, double amplitude)
    {
        var n = (int)(seconds * _rate);
        var samples = new float[n];
        for (var i = 0; i < n; i++) samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / _rate));
        _queue?.Enqueue(samples);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _loop = null;
        var o = _out;
        _out = null;
        try { o?.Stop(); } catch { }
        o?.Dispose();
        _queue = null;
        _tx?.Dispose();
        _tx = null;
    }

    public void Dispose() => Stop();

    /// <summary>Feeds the speakers: queued sound, then silence. Every channel gets the same sample.</summary>
    private sealed class SampleQueue : IWaveProvider
    {
        private readonly Queue<float> _samples = new();
        private readonly object _gate = new();
        private readonly int _channels;

        public SampleQueue(int sampleRate, int channels)
        {
            _channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>How many times the device asked for sound, and how many samples it took (for diagnosis).</summary>
        public long Reads { get; private set; }
        public long Taken { get; private set; }

        public void Enqueue(float[] mono)
        {
            lock (_gate)
            {
                foreach (var s in mono) _samples.Enqueue(s);
                for (var i = 0; i < (int)(0.3 * WaveFormat.SampleRate); i++) _samples.Enqueue(0);   // a short gap after each frame
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / (4 * _channels);
            lock (_gate)
            {
                Reads++;
                for (var f = 0; f < frames; f++)
                {
                    var s = _samples.Count > 0 ? _samples.Dequeue() : 0f;
                    if (_samples.Count > 0) Taken++;
                    for (var c = 0; c < _channels; c++)
                        BitConverter.TryWriteBytes(buffer.AsSpan(offset + (f * _channels + c) * 4, 4), s);
                }
            }
            return frames * 4 * _channels;
        }
    }
}
