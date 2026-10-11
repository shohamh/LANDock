namespace LANDock.Ultrasound;

/// <summary>
/// The sound the desktop ids travel as. Both use ggwave; they differ in the frequencies and therefore in who can hear them:
/// <list type="bullet">
/// <item><b>audible</b>: a short chirp in 1.9-6.4 kHz (ggwave's audible protocol). Everyone hears it. Most speakers and
/// microphones carry it, so it is the default. It is announced less often, to keep it from being annoying.</item>
/// <item><b>ultrasound</b>: 15-19.5 kHz, inaudible to most people. Many laptop speakers and microphones do not carry this band well.</item>
/// </list>
/// The laptop and the desktop must use the same mode.
/// </summary>
public static class SoundMode
{
    public const string Audible = "audible", Ultrasound = "ultrasound";

    public static string Normalize(string? mode) => mode == Ultrasound ? Ultrasound : Audible;

    /// <summary>The frequency range (Hz) the mode uses: the meter shows exactly this range.</summary>
    public static (double low, double high) Band(string mode) =>
        Normalize(mode) == Ultrasound ? (15000, 19500) : (1875, 6375);

    public static int Protocol(string mode) =>
        Normalize(mode) == Ultrasound ? GgWave.ProtocolUltrasoundFast : GgWave.ProtocolAudibleFast;

    public static int Volume(string mode) => Normalize(mode) == Ultrasound ? 40 : 25;

    /// <summary>Random gap between two announcements, in seconds.</summary>
    public static (int min, int max) AnnounceSeconds(string mode) =>
        Normalize(mode) == Ultrasound ? (12, 18) : (45, 60);

    /// <summary>The lowest sample rate the mode can be used at (the band must lie under half of it).</summary>
    public static int MinSampleRate(string mode) => Normalize(mode) == Ultrasound ? 40000 : 16000;

    /// <summary>A short name for the status lines.</summary>
    public static string Label(string mode) => Normalize(mode) == Ultrasound ? "ultrasound (inaudible)" : "audible chirp";
}
