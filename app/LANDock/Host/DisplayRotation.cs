using System.Runtime.InteropServices;

namespace LANDock.Host;

/// <summary>
/// Turns a virtual display to portrait or landscape, so that it matches the desktop screen it shows. Apollo keeps a display it made
/// earlier at its old orientation when a stream for a portrait screen follows (the display is reused, not made again), so the
/// laptop turns it. Windows accepts the change for a display it already has (verified with the rotation probe).
/// </summary>
public static class DisplayRotation
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY;
        public int DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    private const int DM_DISPLAYORIENTATION = 0x80, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DMDO_DEFAULT = 0, DMDO_90 = 1;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [DllImport("user32.dll", CharSet = CharSet.Ansi)] private static extern bool EnumDisplaySettings(string name, int mode, ref DevMode dm);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] private static extern int ChangeDisplaySettingsEx(string name, ref DevMode dm, IntPtr hwnd, uint flags, IntPtr param);

    /// <summary>
    /// Whether a display of this size must be turned to match a desktop screen of the size wanted: the same pixels, the other way round
    /// (a 1920x1080 display for a 1080x1920 screen). Pure: the sizes are passed in.
    /// </summary>
    public static bool NeedsTurn(int wantW, int wantH, int nowW, int nowH) =>
        wantW != nowW && wantW == nowH && wantH == nowW;

    /// <summary>Turns the display ("\\.\DISPLAYn") to portrait (true) or landscape (false), keeping its place. Returns true when Windows accepted it.</summary>
    public static bool Turn(string gdiName, bool portrait)
    {
        var dm = new DevMode { DeviceName = gdiName, Size = (short)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref dm)) return false;
        var big = Math.Max(dm.PelsWidth, dm.PelsHeight);
        var small = Math.Min(dm.PelsWidth, dm.PelsHeight);
        dm.Fields = DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;
        dm.DisplayOrientation = portrait ? DMDO_90 : DMDO_DEFAULT;
        dm.PelsWidth = portrait ? small : big;
        dm.PelsHeight = portrait ? big : small;
        return ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero, 0, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
    }
}
