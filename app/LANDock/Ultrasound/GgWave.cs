using System.Runtime.InteropServices;
using LANDock.Core;

namespace LANDock.Ultrasound;

/// <summary>
/// The ggwave library (MIT, https://github.com/ggerganov/ggwave), called from C#. ggwave turns a short payload into a sound
/// wave with forward error correction, and decodes such waves from microphone samples. We use its ultrasound protocols
/// with a fixed 8-byte payload: the desktop PC's id.
/// The library is built by scripts/build-ggwave.sh and placed next to LANDock.exe as ggwave.dll.
/// </summary>
public static class GgWave
{
    public const int PayloadBytes = 8;
    /// <summary>Ultrasound protocols (ggwave's ids): Normal, Fast, Fastest.</summary>
    public const int ProtocolUltrasoundNormal = 3, ProtocolUltrasoundFast = 4, ProtocolUltrasoundFastest = 5;
    /// <summary>Audible protocols (a short chirp anyone hears): Normal, Fast, Fastest.</summary>
    public const int ProtocolAudibleNormal = 0, ProtocolAudibleFast = 1, ProtocolAudibleFastest = 2;
    private const int SampleFormatF32 = 5;
    private const int OperatingModeRxAndTx = 1 << 1 | 1 << 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public int PayloadLength;
        public float SampleRateInp;
        public float SampleRateOut;
        public float SampleRate;
        public int SamplesPerFrame;
        public float SoundMarkerThreshold;
        public int SampleFormatInp;
        public int SampleFormatOut;
        public int OperatingMode;
    }

    [DllImport("ggwave", CallingConvention = CallingConvention.Cdecl)]
    private static extern Parameters ggwave_getDefaultParameters();
    [DllImport("ggwave", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ggwave_init(Parameters parameters);
    [DllImport("ggwave", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ggwave_free(int instance);
    [DllImport("ggwave", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ggwave_encode(int instance, byte[] payload, int payloadSize, int protocolId, int volume, byte[]? waveform, int query);
    [DllImport("ggwave", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ggwave_decode(int instance, byte[] waveform, int waveformSize, byte[] payload);

    /// <summary>True when ggwave.dll is next to the program.</summary>
    public static bool Available => File.Exists(Path.Combine(AppContext.BaseDirectory, "ggwave.dll"));

    /// <summary>One ggwave instance: it encodes or decodes at a fixed sample rate, with float samples.</summary>
    public sealed class Instance : IDisposable
    {
        private readonly int _id;
        public double SampleRate { get; }
        public int SamplesPerFrame { get; }

        public Instance(double sampleRate, bool rx, bool tx)
        {
            var p = ggwave_getDefaultParameters();
            p.PayloadLength = PayloadBytes;
            p.SampleRate = p.SampleRateInp = p.SampleRateOut = (float)sampleRate;
            p.SampleFormatInp = p.SampleFormatOut = SampleFormatF32;
            p.OperatingMode = (rx ? 1 << 1 : 0) | (tx ? 1 << 2 : 0);
            _id = ggwave_init(p);
            if (_id < 0) throw new InvalidOperationException("ggwave could not start (too many instances, or an unsupported sample rate)");
            SampleRate = sampleRate;
            SamplesPerFrame = p.SamplesPerFrame;
        }

        /// <summary>The sound for an 8-byte payload, as mono float samples at this instance's sample rate.</summary>
        public float[] Encode(byte[] payload, int protocol, int volume = 40)
        {
            if (payload.Length != PayloadBytes) throw new ArgumentException($"the payload is {PayloadBytes} bytes");
            // first ask how many bytes the wave takes, then make it (the wave is float samples: 4 bytes each)
            var size = ggwave_encode(_id, payload, payload.Length, protocol, volume, null, 1);
            if (size <= 0) throw new InvalidOperationException("ggwave could not encode the payload");
            var bytes = new byte[size];
            var written = ggwave_encode(_id, payload, payload.Length, protocol, volume, bytes, 0);
            if (written <= 0) throw new InvalidOperationException("ggwave could not encode the payload");
            var result = new float[written / 4];
            Buffer.BlockCopy(bytes, 0, result, 0, result.Length * 4);
            return result;
        }

        private readonly List<float> _pending = new();

        /// <summary>
        /// Decodes what the microphone heard. Samples may arrive in any sizes: the ones that do not make a whole frame are kept
        /// for the next call. Returns the payloads completed (each one is 8 bytes).
        /// </summary>
        public List<byte[]> Decode(ReadOnlySpan<float> samples)
        {
            _pending.AddRange(samples.ToArray());
            var found = new List<byte[]>();
            var payload = new byte[PayloadBytes];
            var frame = new byte[SamplesPerFrame * 4];
            var used = 0;
            while (_pending.Count - used >= SamplesPerFrame)
            {
                Buffer.BlockCopy(_pending.GetRange(used, SamplesPerFrame).ToArray(), 0, frame, 0, frame.Length);
                used += SamplesPerFrame;
                var n = ggwave_decode(_id, frame, frame.Length, payload);
                if (n == PayloadBytes) found.Add((byte[])payload.Clone());
            }
            _pending.RemoveRange(0, used);
            return found;
        }

        public void Dispose() => ggwave_free(_id);
    }
}
