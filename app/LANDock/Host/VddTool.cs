using System.Diagnostics;
using LANDock.Core;

namespace LANDock.Host;

/// <summary>Wraps host\bin\vddtool.exe, our small helper that talks to the SudoVDA virtual-display driver and the display layout.</summary>
public static class VddTool
{
    public static bool Available => File.Exists(Paths.VddTool);

    public static (int code, string output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Paths.VddTool)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Paths.HostBin,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20_000)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
        return (p.ExitCode, so.Result + se.Result);
    }

    /// <summary>True when the SudoVDA virtual display driver is installed (installing Apollo once adds it).</summary>
    public static bool DriverPresent()
    {
        if (!Available) return false;
        var (code, output) = Run("watchdog");
        return code != -1 && !output.Contains("cannot open SudoVDA device");
    }

    public const string DriverHelp = "The virtual display driver (SudoVDA) is not installed on this computer. Install Apollo once from "
        + "https://github.com/ClassicOldSong/Apollo/releases (its installer adds the driver), then start the host again. LANDock does not install drivers itself.";

    public static Dictionary<string, ScreenInfo> Screens() =>
        Available ? HostPlanning.ParseScreens(Run("screens").output) : new();

    public static string Layout(IEnumerable<string> assignments) =>
        Run(new[] { "layout" }.Concat(assignments).ToArray()).output.Trim();

    /// <summary>Removes the virtual displays that belong to the given client uuids.</summary>
    public static string Remove(IEnumerable<string> uuids)
    {
        var list = uuids.ToArray();
        if (list.Length == 0 || !Available) return "nothing to do";
        var lines = Run(new[] { "remove" }.Concat(list).ToArray()).output.Trim().Split('\n');
        return lines.Length > 0 ? lines[^1].Trim() : "done";
    }
}
