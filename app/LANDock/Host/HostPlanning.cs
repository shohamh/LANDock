using System.Text.RegularExpressions;

namespace LANDock.Host;

/// <summary>One Apollo instance: each serves one stream and creates one virtual display while a client is connected.</summary>
public sealed record Instance(string Key, int Port, string Label);

public sealed record ScreenInfo(string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>The logic that is easy to get wrong and easy to test: no processes, no files, no network.</summary>
public static class HostPlanning
{
    /// <summary>Left to right: the stream order is also the screen order on the laptop.</summary>
    public static readonly IReadOnlyList<Instance> Instances = new[]
    {
        new Instance("c", 49989, "M1"),
        new Instance("d", 50989, "M2"),
        new Instance("e", 51989, "TV"),
    };

    private static readonly Regex ScreenLine = new(@"^\\\\\.\\(DISPLAY\d+) pos=(-?\d+),(-?\d+) size=(\d+)x(\d+)", RegexOptions.Compiled);
    private static readonly Regex CreatedLine = new(@"Virtual Display created at \\\\\.\\(DISPLAY\d+)", RegexOptions.Compiled);

    /// <summary>Parses the output of "vddtool screens".</summary>
    public static Dictionary<string, ScreenInfo> ParseScreens(string output)
    {
        var res = new Dictionary<string, ScreenInfo>();
        foreach (var line in output.Split('\n'))
        {
            var m = ScreenLine.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            res[m.Groups[1].Value] = new ScreenInfo(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), line.Contains(" primary"));
        }
        return res;
    }

    /// <summary>The display an instance created for its current stream, or null once that stream has ended (read from its log lines).</summary>
    public static string? ActiveDisplay(IEnumerable<string> logLines)
    {
        int i = 0, created = -1, removed = -1;
        string? name = null;
        foreach (var l in logLines)
        {
            var m = CreatedLine.Match(l);
            if (m.Success) { created = i; name = m.Groups[1].Value; }
            if (l.Contains("Virtual Display removed")) removed = i;
            i++;
        }
        return created > removed ? name : null;
    }

    /// <summary>
    /// Where each active virtual display must be: laptop screen first, then the streams in order, side by side with tops aligned
    /// to the primary screen. Returns only the displays that are not there yet, as "DISPLAYn=x,y" arguments for vddtool.
    /// </summary>
    public static List<string> PlanLayout(IReadOnlyDictionary<string, ScreenInfo> screens, IEnumerable<string?> activeInOrder)
    {
        var todo = new List<string>();
        var primary = screens.Values.FirstOrDefault(s => s.Primary);
        if (primary == null) return todo;
        int x = primary.X + primary.Width;
        foreach (var d in activeInOrder)
        {
            if (d == null || !screens.TryGetValue(d, out var s)) continue;
            if (s.X != x || s.Y != primary.Y) todo.Add($"{d}={x},{primary.Y}");
            x += s.Width;
        }
        return todo;
    }

    /// <summary>A random 4-digit pairing PIN.</summary>
    public static string NewPin() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
}
