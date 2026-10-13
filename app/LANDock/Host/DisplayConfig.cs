using System.Runtime.InteropServices;

namespace LANDock.Host;

/// <summary>
/// Windows' display topology (what the Display settings show): saved before the laptop's virtual displays appear, and put back when
/// they are gone. Calls the Windows display configuration API directly.
/// </summary>
/// <remarks>
/// Windows cannot make one screen show another across two graphics adapters (the laptop's screen and the virtual displays are on
/// different ones), so a copy of the laptop's screen is made by the stream instead (HostSetup.WriteCopyConf), not here.
/// </remarks>
public static class DisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint SDC_APPLY = 0x80, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_ALLOW_CHANGES = 0x400;
    private const int ERROR_SUCCESS = 0;
    private static readonly byte[] Magic = "LDC1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSource { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology; public uint Rotation;
        public uint Scaling; public uint RefreshNumerator; public uint RefreshDenominator; public uint ScanLineOrdering;
        public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Path { public PathSource Source; public PathTarget Target; public uint Flags; }

    /// <summary>A mode is opaque here (its 48-byte payload is kept as it is).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Mode { public uint InfoType; public uint Id; public Luid AdapterId; public long D0, D1, D2, D3, D4, D5; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] Path[] paths, ref uint numModes, [Out] Mode[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPaths, Path[] paths, uint numModes, Mode[] modes, uint flags);

    /// <summary>The display configuration as it is now (the active screens and where they are), as bytes for <see cref="Restore"/>.</summary>
    public static byte[] Save()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != ERROR_SUCCESS) throw new InvalidOperationException("the display configuration cannot be read");
        var paths = new Path[np];
        var modes = new Mode[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != ERROR_SUCCESS) throw new InvalidOperationException("the display configuration cannot be read");
        Array.Resize(ref paths, (int)np);
        Array.Resize(ref modes, (int)nm);
        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.Write(BitConverter.GetBytes(paths.Length));
        ms.Write(BitConverter.GetBytes(modes.Length));
        ms.Write(MemoryMarshal.AsBytes(paths.AsSpan()));
        ms.Write(MemoryMarshal.AsBytes(modes.AsSpan()));
        return ms.ToArray();
    }

    /// <summary>Puts a saved configuration back (the screens it lists become active again, at their saved places).</summary>
    public static bool Restore(byte[] saved)
    {
        if (saved.Length < 12 || !saved.AsSpan(0, 4).SequenceEqual(Magic)) return false;
        var np = BitConverter.ToInt32(saved, 4);
        var nm = BitConverter.ToInt32(saved, 8);
        var pathBytes = np * Marshal.SizeOf<Path>();
        var modeBytes = nm * Marshal.SizeOf<Mode>();
        if (saved.Length != 12 + pathBytes + modeBytes) return false;
        var paths = MemoryMarshal.Cast<byte, Path>(saved.AsSpan(12, pathBytes)).ToArray();
        var modes = MemoryMarshal.Cast<byte, Mode>(saved.AsSpan(12 + pathBytes, modeBytes)).ToArray();
        return SetDisplayConfig((uint)np, paths, (uint)nm, modes, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES) == ERROR_SUCCESS;
    }
}
