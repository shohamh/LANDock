using System.Text.RegularExpressions;

namespace LANDock.Host;

/// <summary>One Apollo instance: each serves one stream and creates one virtual display while a client is connected.</summary>
public sealed record Instance(string Key, int Port, string Label);

public sealed record ScreenInfo(string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>The logic that is easy to get wrong and easy to test: no processes, no files, no network.</summary>
public static class HostPlanning
{
    /// <summary>
    /// Every display slot the laptop can serve (plan-monitor-layout.md, step 2): slot k is instance k, on port 49989 + 1000 k. The
    /// first three are the fixed displays; the others are started when a desktop asks for them.
    /// </summary>
    public static readonly IReadOnlyList<Instance> AllInstances = new[]
    {
        new Instance("c", 49989, "M1"),
        new Instance("d", 50989, "M2"),
        new Instance("e", 51989, "TV"),
        new Instance("f", 52989, "M4"),
        new Instance("g", 53989, "M5"),
        new Instance("h", 54989, "M6"),
    };

    /// <summary>The fixed displays, started with the host. Left to right: the stream order is also the screen order on the laptop.</summary>
    public static readonly IReadOnlyList<Instance> Instances = AllInstances.Take(3).ToList();

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

    /// <summary>True while a client is connected to the instance: its last "CLIENT CONNECTED" is after its last "CLIENT DISCONNECTED" (read from its log lines).
    /// A display that is kept and reused does not show its end in the log, so the connections are what tell a running stream.</summary>
    public static bool StreamRunning(IEnumerable<string> logLines)
    {
        int i = 0, connected = -1, disconnected = -1;
        foreach (var l in logLines)
        {
            if (l.Contains("CLIENT CONNECTED")) connected = i;
            if (l.Contains("CLIENT DISCONNECTED")) disconnected = i;
            i++;
        }
        return connected > disconnected;
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

    // ---- mirroring the desktop's screens (plan-monitor-layout.md, steps 2 and 3) -------------------------------------------

    /// <summary>The most virtual displays one laptop serves: one Apollo instance each.</summary>
    public const int MaxDisplays = 6;

    /// <summary>Instance k serves on port BasePort + PortStep * k: the three fixed displays are instances 0, 1 and 2.</summary>
    public const int BasePort = 49989, PortStep = 1000;

    /// <summary>A virtual display on the laptop: one Apollo instance (its key and port) that mirrors one desktop screen.</summary>
    public sealed record PlannedDisplay(string Key, int Port, MonitorOffer Screen);

    /// <summary>
    /// The displays for the screens a desktop reported. A screen that was served before keeps its instance (key and port), so
    /// adding or removing another screen does not restart the others. New screens take the lowest free instance. The screens are
    /// taken in the desktop's order: left to right, then top to bottom.
    /// </summary>
    public static List<PlannedDisplay> PlanDisplays(IReadOnlyList<MonitorOffer> screens, IReadOnlyList<PlannedDisplay>? running = null)
    {
        if (screens.Count == 0) throw new ArgumentException("the desktop reported no screens", nameof(screens));
        if (screens.Count > MaxDisplays) throw new ArgumentException($"{screens.Count} screens; the laptop serves up to {MaxDisplays}", nameof(screens));
        var ordered = screens.OrderBy(s => s.X).ThenBy(s => s.Y).ToList();
        var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in running ?? Array.Empty<PlannedDisplay>())
            slots[r.Screen.Id] = SlotOf(r);
        var taken = new HashSet<int>();
        var result = new List<PlannedDisplay?>(new PlannedDisplay?[ordered.Count]);
        // a screen that keeps its instance first, then the new ones take the lowest free slot
        for (var i = 0; i < ordered.Count; i++)
            if (slots.TryGetValue(ordered[i].Id, out var slot) && slot < MaxDisplays && taken.Add(slot))
                result[i] = Make(slot, ordered[i]);
        var free = Enumerable.Range(0, MaxDisplays).Where(k => !taken.Contains(k)).GetEnumerator();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (result[i] != null) continue;
            free.MoveNext();
            result[i] = Make(free.Current, ordered[i]);
        }
        return result.Select(d => d!).ToList();
    }

    /// <summary>The Apollo instance key of each slot: the first three are the fixed instances (c, d, e), so the same folders serve them.</summary>
    private static readonly string[] SlotKeys = { "c", "d", "e", "f", "g", "h" };

    private static PlannedDisplay Make(int slot, MonitorOffer screen) =>
        new(SlotKeys[slot], BasePort + PortStep * slot, screen);

    private static int SlotOf(PlannedDisplay d) => Array.IndexOf(SlotKeys, d.Key);

    /// <summary>What changes between the displays that run and the ones wanted (by screen). Resizing is a new agreement for that
    /// display; a change of place only is applied by moving the displays (<see cref="MirrorPositions"/>).</summary>
    public sealed record PlanChanges(List<PlannedDisplay> Added, List<PlannedDisplay> Removed, List<PlannedDisplay> Resized);

    public static PlanChanges Diff(IReadOnlyList<PlannedDisplay> running, IReadOnlyList<PlannedDisplay> wanted)
    {
        var have = running.ToDictionary(d => d.Screen.Id, StringComparer.OrdinalIgnoreCase);
        var want = wanted.ToDictionary(d => d.Screen.Id, StringComparer.OrdinalIgnoreCase);
        var added = wanted.Where(d => !have.ContainsKey(d.Screen.Id)).ToList();
        var removed = running.Where(d => !want.ContainsKey(d.Screen.Id)).ToList();
        var resized = wanted.Where(d => have.TryGetValue(d.Screen.Id, out var r)
                                        && (r.Screen.Width != d.Screen.Width || r.Screen.Height != d.Screen.Height)).ToList();
        return new PlanChanges(added, removed, resized);
    }

    /// <summary>
    /// Where each mirrored display goes on the laptop's virtual desktop. The laptop's primary screen keeps its place; the desktop's
    /// screens follow to its right, with their own relative arrangement: the leftmost and topmost desktop screen start right of
    /// the laptop screen and level with its top.
    /// </summary>
    public static Dictionary<string, (int x, int y)> MirrorPositions(ScreenInfo laptopPrimary, IReadOnlyList<PlannedDisplay> displays)
    {
        var res = new Dictionary<string, (int x, int y)>(StringComparer.OrdinalIgnoreCase);
        if (displays.Count == 0) return res;
        var minX = displays.Min(d => d.Screen.X);
        var minY = displays.Min(d => d.Screen.Y);
        var x0 = laptopPrimary.X + laptopPrimary.Width;
        var y0 = laptopPrimary.Y;
        foreach (var d in displays)
            res[d.Screen.Id] = (x0 + (d.Screen.X - minX), y0 + (d.Screen.Y - minY));
        return res;
    }

    /// <summary>
    /// The moves that put the active displays where the desktop's screens are. Each active display is its virtual display's name
    /// and the desktop screen it shows (null when unknown). A display whose screen is not in the report, or that is unknown to the
    /// laptop, stays where it is. Returns "DISPLAYn=x,y" arguments for vddtool, for the displays that are not there yet.
    /// </summary>
    public static List<string> PlanMirrorLayout(IReadOnlyDictionary<string, ScreenInfo> screens,
        IEnumerable<(string? Display, string? ScreenId)> active, IReadOnlyList<MonitorOffer> report)
    {
        var todo = new List<string>();
        var laptop = screens.Values.FirstOrDefault(s => s.Primary);
        if (laptop == null) return todo;
        var placed = new List<(string Display, MonitorOffer Screen)>();
        foreach (var (display, screenId) in active)
        {
            if (display == null || screenId == null || !screens.ContainsKey(display)) continue;
            var screen = report.FirstOrDefault(r => r.Id.Equals(screenId, StringComparison.OrdinalIgnoreCase));
            if (screen != null) placed.Add((display, screen));
        }
        if (placed.Count == 0) return todo;
        // one place per desktop screen; a second display that shows the same screen (two streams on one monitor) goes after the
        // others, level with the laptop screen, so that two displays never sit on top of each other
        var firsts = new List<MonitorOffer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in placed) if (seen.Add(p.Screen.Id)) firsts.Add(p.Screen);
        var positions = MirrorPositions(laptop, firsts.Select(s => new PlannedDisplay("", 0, s)).ToList());
        var right = firsts.Max(s => positions[s.Id].x + s.Width);
        seen.Clear();
        foreach (var (display, screen) in placed)
        {
            (int x, int y) target;
            if (seen.Add(screen.Id)) target = positions[screen.Id];
            else { target = (right, laptop.Y); right += screen.Width; }
            var s = screens[display];
            if (s.X != target.x || s.Y != target.y) todo.Add($"{display}={target.x},{target.y}");
        }
        return todo;
    }

    /// <summary>A random 4-digit pairing PIN.</summary>
    public static string NewPin() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
}
