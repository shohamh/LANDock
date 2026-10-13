namespace LANDock.Host;

/// <summary>Where the laptop's screen sits in relation to the desktop's screens.</summary>
public enum LaptopPlacement
{
    /// <summary>The laptop is to the left of the desktop's screens (the desktop's screens are to its right).</summary>
    Left,
    /// <summary>The laptop is to the right of the desktop's screens.</summary>
    Right,
    /// <summary>The laptop is below the desktop's screens, centred under them.</summary>
    BelowCentered,
    /// <summary>The laptop is below the rightmost desktop screen (its right edge is the desktop's right edge).</summary>
    BelowRight,
    /// <summary>The laptop is below the leftmost desktop screen (its left edge is the desktop's left edge).</summary>
    BelowLeft,
}

/// <summary>Which desktop screen shows a copy of the laptop's screen (the rest show their own displays).</summary>
public enum DuplicateTarget
{
    None,
    /// <summary>The desktop's primary screen.</summary>
    Primary,
    /// <summary>The desktop's screen with the largest resolution.</summary>
    Largest,
}

/// <summary>
/// Places the laptop's virtual displays (one per desktop screen) and the laptop's own screen in Windows' display layout. The laptop's
/// primary screen stays at (0,0) (Windows fixes the primary screen's origin), so the desktop's screens move around it: the desktop's
/// arrangement is kept, and the laptop is placed as the setting says. Pure logic: no displays are touched here.
/// </summary>
public static class LaptopLayout
{
    public static LaptopPlacement ParsePlacement(string? s) => s?.ToLowerInvariant() switch
    {
        "left" => LaptopPlacement.Left,
        "below-centered" => LaptopPlacement.BelowCentered,
        "below-right" => LaptopPlacement.BelowRight,
        "below-left" => LaptopPlacement.BelowLeft,
        _ => LaptopPlacement.Right,
    };

    public static string PlacementName(LaptopPlacement p) => p switch
    {
        LaptopPlacement.Left => "left",
        LaptopPlacement.BelowCentered => "below-centered",
        LaptopPlacement.BelowRight => "below-right",
        LaptopPlacement.BelowLeft => "below-left",
        _ => "right",
    };

    public static DuplicateTarget ParseDuplicate(string? s) => s?.ToLowerInvariant() switch
    {
        "primary" => DuplicateTarget.Primary,
        "largest" => DuplicateTarget.Largest,
        _ => DuplicateTarget.None,
    };

    public static string DuplicateName(DuplicateTarget d) => d switch
    {
        DuplicateTarget.Primary => "primary",
        DuplicateTarget.Largest => "largest",
        _ => "none",
    };

    /// <summary>
    /// Where the desktop's arrangement starts, with the laptop's screen at (0,0), for the placement: the origin of the desktop's
    /// top-left screen corner. For the placements below the desktop, the desktop is moved so that its lowest screen over the laptop's
    /// width touches the laptop's top edge; the other screens keep their place in the arrangement. Windows keeps a display that touches
    /// no other display where it was, so a screen floating above the laptop would never move there.
    /// </summary>
    public static (int x, int y) GroupOrigin(ScreenInfo laptop, IReadOnlyList<MonitorOffer> desktop, LaptopPlacement placement)
    {
        var minX = desktop.Min(s => s.X);
        var minY = desktop.Min(s => s.Y);
        var groupW = desktop.Max(s => s.X + s.Width) - minX;
        var groupH = desktop.Max(s => s.Y + s.Height) - minY;
        var (originX, originY) = Origin(laptop.Width, laptop.Height, groupW, groupH, placement);
        if (placement is LaptopPlacement.BelowCentered or LaptopPlacement.BelowRight or LaptopPlacement.BelowLeft)
        {
            var over = desktop.Where(s => originX + (s.X - minX) < laptop.Width && originX + (s.X - minX) + s.Width > 0).ToList();
            originY = -(over.Count > 0 ? over.Max(s => s.Y + s.Height - minY) : groupH);
        }
        return (originX, originY);
    }

    /// <summary>
    /// The extended screens, moved together so that the laptop's displays touch each other: where a screen that is not extended lies
    /// between two extended ones, the space it takes is closed. The desktop's own arrangement (the diagram) keeps that space. Windows
    /// keeps a display that touches no other display where it was, so a gap would leave a display unplaced.
    /// </summary>
    public static List<MonitorOffer> Pack(IReadOnlyList<MonitorOffer> all, IEnumerable<string> extendedIds)
    {
        var ext = new HashSet<string>(extendedIds, StringComparer.OrdinalIgnoreCase);
        var cur = all.Select(s => s with { }).ToList();   // copies: the positions change below
        foreach (var off in all.Where(s => !ext.Contains(s.Id)).OrderBy(s => s.X).ThenBy(s => s.Y).ToList())
        {
            var i = cur.FindIndex(s => s.Id.Equals(off.Id, StringComparison.OrdinalIgnoreCase));
            var o = cur[i];
            // the screens to its right, in its rows, move left by its width; the screens below it, in its columns, move up by its height
            for (var k = 0; k < cur.Count; k++)
            {
                if (k == i) continue;
                var s = cur[k];
                if (s.X >= o.X + o.Width && s.Y < o.Y + o.Height && s.Y + s.Height > o.Y) cur[k] = s with { X = s.X - o.Width };
            }
            for (var k = 0; k < cur.Count; k++)
            {
                if (k == i) continue;
                var s = cur[k];
                if (s.Y >= o.Y + o.Height && s.X < o.X + o.Width && s.X + s.Width > o.X) cur[k] = s with { Y = s.Y - o.Height };
            }
        }
        return cur.Where(s => ext.Contains(s.Id)).ToList();
    }

    /// <summary>True when every screen touches another (they form one arrangement that Windows keeps).</summary>
    public static bool Connected(IReadOnlyList<MonitorOffer> screens)
    {
        if (screens.Count <= 1) return true;
        bool Touch(MonitorOffer a, MonitorOffer b) =>
            (a.X + a.Width == b.X || b.X + b.Width == a.X) && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height ||
            (a.Y + a.Height == b.Y || b.Y + b.Height == a.Y) && a.X < b.X + b.Width && b.X < a.X + a.Width;
        var seen = new HashSet<int> { 0 };
        var queue = new Queue<int>(new[] { 0 });
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            for (var m = 0; m < screens.Count; m++)
                if (!seen.Contains(m) && Touch(screens[n], screens[m])) { seen.Add(m); queue.Enqueue(m); }
        }
        return seen.Count == screens.Count;
    }

    /// <summary>
    /// The moves ("DISPLAYn=x,y") that put the laptop's virtual displays where the desktop's screens are, for the placement. 'desktop' is
    /// the desktop's screens as it reported them. 'active' is each virtual display the laptop runs and the desktop screen it shows; 'current'
    /// is where each display is now, so that only the moves that are needed are returned. A screen that shows a copy of the laptop's
    /// screen has no virtual display (its stream captures the laptop's screen), so it is not in 'active'.
    /// </summary>
    public static List<string> Compute(ScreenInfo laptop, IReadOnlyList<MonitorOffer> desktop,
        IEnumerable<(string Display, string? ScreenId)> active, IReadOnlyDictionary<string, ScreenInfo> current, LaptopPlacement placement)
    {
        var moves = new List<string>();
        if (desktop.Count == 0) return moves;

        var minX = desktop.Min(s => s.X);
        var minY = desktop.Min(s => s.Y);
        var (originX, originY) = GroupOrigin(laptop, desktop, placement);

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (display, screenId) in active)
        {
            if (screenId == null || !current.ContainsKey(display)) continue;
            var screen = desktop.FirstOrDefault(s => s.Id.Equals(screenId, StringComparison.OrdinalIgnoreCase));
            if (screen == null) continue;
            // a second display for the same screen is left where it is: two displays must not sit on top of each other
            if (!placed.Add(screen.Id)) continue;
            var target = (x: originX + (screen.X - minX), y: originY + (screen.Y - minY));
            var now = current[display];
            if (now.X != target.x || now.Y != target.y) moves.Add($"{display}={target.x},{target.y}");
        }
        return moves;
    }

    /// <summary>Where the desktop's arrangement (its box of groupW x groupH) starts, with the laptop's screen at (0,0).</summary>
    public static (int x, int y) Origin(int laptopW, int laptopH, int groupW, int groupH, LaptopPlacement placement) => placement switch
    {
        LaptopPlacement.Left => (laptopW, 0),                                   // desktop to the right, tops level
        LaptopPlacement.Right => (-groupW, 0),                                  // desktop to the left, tops level
        LaptopPlacement.BelowCentered => ((laptopW - groupW) / 2, -groupH),     // desktop above, centred on the laptop
        LaptopPlacement.BelowRight => (laptopW - groupW, -groupH),              // desktop above, its right edge on the laptop's
        LaptopPlacement.BelowLeft => (0, -groupH),                              // desktop above, its left edge on the laptop's
        _ => (-groupW, 0),
    };

    /// <summary>The desktop screen that gets the copy of the laptop's screen (null for none, or when the choice has no screen).</summary>
    public static MonitorOffer? DuplicateScreen(IReadOnlyList<MonitorOffer> desktop, DuplicateTarget duplicate) => duplicate switch
    {
        DuplicateTarget.Primary => desktop.FirstOrDefault(s => s.Primary) ?? desktop.FirstOrDefault(),
        DuplicateTarget.Largest => desktop.OrderByDescending(s => (long)s.Width * s.Height).FirstOrDefault(),
        _ => null,
    };
}
