using LANDock.Host;
using Xunit;

namespace LANDock.Tests;

public class LaptopLayoutTests
{
    // the laptop's screen: 1920x1080 at the origin, primary
    private static readonly ScreenInfo Laptop = new("DISPLAY1", 0, 0, 1920, 1080, true);

    // two desktop screens side by side: 1920x1080 primary at 0,0 and a 1080x1920 portrait screen right of it
    private static readonly List<MonitorOffer> Desktop = new()
    {
        new MonitorOffer("A", 0, 0, 1920, 1080, 0, true),
        new MonitorOffer("B", 1920, 0, 1080, 1920, 90, false),
    };

    private static readonly (string, string?)[] Active = { ("DISPLAY2", "A"), ("DISPLAY3", "B") };

    private static Dictionary<string, ScreenInfo> Current(params (string name, int x, int y, int w, int h)[] displays)
    {
        var res = new Dictionary<string, ScreenInfo>();
        foreach (var d in displays) res[d.name] = new ScreenInfo(d.name, d.x, d.y, d.w, d.h, false);
        return res;
    }

    /// <summary>Where a display is after the moves (its current place when it is not moved).</summary>
    private static (int x, int y) PositionOf(List<string> moves, string display, Dictionary<string, ScreenInfo> now)
    {
        var move = moves.FirstOrDefault(m => m.StartsWith(display + "="));
        if (move == null) return (now[display].X, now[display].Y);
        var parts = move[(display.Length + 1)..].Split(',');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    [Fact]
    public void Right_puts_the_desktop_to_the_left_of_the_laptop()
    {
        var now = Current(("DISPLAY2", 1920, 0, 1920, 1080), ("DISPLAY3", 3840, 0, 1080, 1920));
        var moves = LaptopLayout.Compute(Laptop, Desktop, Active, now, LaptopPlacement.Right);
        // the desktop spans 3000 px: its origin is -3000 (left of the laptop), tops level
        Assert.Equal((-3000, 0), PositionOf(moves, "DISPLAY2", now));
        Assert.Equal((-1080, 0), PositionOf(moves, "DISPLAY3", now));
    }

    [Fact]
    public void Left_puts_the_desktop_to_the_right_of_the_laptop()
    {
        var now = Current(("DISPLAY2", 0, 0, 1920, 1080), ("DISPLAY3", 1920, 0, 1080, 1920));
        var moves = LaptopLayout.Compute(Laptop, Desktop, Active, now, LaptopPlacement.Left);
        Assert.Equal((1920, 0), PositionOf(moves, "DISPLAY2", now));
        Assert.Equal((3840, 0), PositionOf(moves, "DISPLAY3", now));
    }

    [Fact]
    public void Below_centred_puts_the_desktop_above_the_laptop_centred()
    {
        var now = Current(("DISPLAY2", 0, 0, 1920, 1080), ("DISPLAY3", 1920, 0, 1080, 1920));
        var moves = LaptopLayout.Compute(Laptop, Desktop, Active, now, LaptopPlacement.BelowCentered);
        // desktop box 3000 x 1920: x = (1920 - 3000) / 2 = -540, bottom at the laptop's top (y = -1920)
        Assert.Equal((-540, -1920), PositionOf(moves, "DISPLAY2", now));
        Assert.Equal((1380, -1920), PositionOf(moves, "DISPLAY3", now));
    }

    [Fact]
    public void Below_right_aligns_the_desktop_right_edge_with_the_laptop()
    {
        var now = Current(("DISPLAY2", 0, 0, 1920, 1080), ("DISPLAY3", 1920, 0, 1080, 1920));
        var moves = LaptopLayout.Compute(Laptop, Desktop, Active, now, LaptopPlacement.BelowRight);
        // the desktop's right edge (3000) is the laptop's right edge (1920): origin x = 1920 - 3000
        Assert.Equal((-1080, -1920), PositionOf(moves, "DISPLAY2", now));
        Assert.Equal((840, -1920), PositionOf(moves, "DISPLAY3", now));
        // the rightmost desktop screen (B, 1080 wide) ends exactly at the laptop's right edge
        Assert.Equal(1920, PositionOf(moves, "DISPLAY3", now).x + 1080);
    }

    [Fact]
    public void Below_left_aligns_the_desktop_left_edge_with_the_laptop()
    {
        var now = Current(("DISPLAY2", 0, 0, 1920, 1080), ("DISPLAY3", 1920, 0, 1080, 1920));
        var moves = LaptopLayout.Compute(Laptop, Desktop, Active, now, LaptopPlacement.BelowLeft);
        // the leftmost screen (1080 tall, over the laptop) sits on the laptop's top edge; the portrait screen keeps its place beside it
        Assert.Equal((0, -1080), PositionOf(moves, "DISPLAY2", now));
        Assert.Equal((1920, -1080), PositionOf(moves, "DISPLAY3", now));
    }

    [Fact]
    public void Below_placement_puts_a_screen_over_the_laptop_on_its_top_edge()
    {
        // landscape screen over the laptop, portrait screen to the right, nothing over the laptop beside it
        var now = Current(("DISPLAY2", 0, 0, 1920, 1080), ("DISPLAY3", 1920, 0, 1080, 1920));
        foreach (var placement in new[] { LaptopPlacement.BelowLeft, LaptopPlacement.BelowCentered, LaptopPlacement.BelowRight })
        {
            var moves = LaptopLayout.Compute(Laptop, Desktop, Active, now, placement);
            var (x, y) = PositionOf(moves, "DISPLAY2", now);
            var (px, py) = PositionOf(moves, "DISPLAY3", now);
            // some desktop screen over the laptop has its bottom edge on the laptop's top edge (y = -1080 + 1080 = 0)
            Assert.True(y + 1080 == 0 || py + 1920 == 0, $"{placement}: no screen touches the laptop");
        }
    }

    [Fact]
    public void Nothing_moves_when_the_displays_are_already_in_place()
    {
        var now = Current(("DISPLAY2", -3000, 0, 1920, 1080), ("DISPLAY3", -1080, 0, 1080, 1920));
        Assert.Empty(LaptopLayout.Compute(Laptop, Desktop, Active, now, LaptopPlacement.Right));
    }

    [Fact]
    public void A_second_display_for_the_same_screen_is_not_moved_on_top_of_the_first()
    {
        var now = Current(("DISPLAY2", 0, 0, 1920, 1080), ("DISPLAY3", 1920, 0, 1080, 1920), ("DISPLAY4", 1920, 0, 1080, 1920));
        var moves = LaptopLayout.Compute(Laptop, Desktop, new[] { ("DISPLAY2", (string?)"A"), ("DISPLAY3", (string?)"B"), ("DISPLAY4", (string?)"B") },
            now, LaptopPlacement.Right);
        Assert.DoesNotContain(moves, m => m.StartsWith("DISPLAY4="));
    }

    [Fact]
    public void Duplicate_primary_picks_the_desktop_primary_screen()
    {
        Assert.Equal("A", LaptopLayout.DuplicateScreen(Desktop, DuplicateTarget.Primary)!.Id);
    }

    [Fact]
    public void Duplicate_largest_picks_the_largest_resolution_and_a_tie_takes_the_first()
    {
        // 1920x1080 and 1080x1920 are the same size: the first one in the report wins
        Assert.Equal("A", LaptopLayout.DuplicateScreen(Desktop, DuplicateTarget.Largest)!.Id);
        var big = new List<MonitorOffer> { new("S", 0, 0, 1280, 1024, 0, true), new("L", 1280, 0, 3840, 2160, 0, false) };
        Assert.Equal("L", LaptopLayout.DuplicateScreen(big, DuplicateTarget.Largest)!.Id);
    }

    [Fact]
    public void A_display_is_turned_only_when_it_is_the_other_orientation_of_its_screen()
    {
        Assert.True(DisplayRotation.NeedsTurn(1080, 1920, 1920, 1080));    // portrait screen, landscape display
        Assert.True(DisplayRotation.NeedsTurn(1920, 1080, 1080, 1920));    // and the other way round
        Assert.False(DisplayRotation.NeedsTurn(1920, 1080, 1920, 1080));   // already matching
        Assert.False(DisplayRotation.NeedsTurn(1280, 1024, 1920, 1080));   // a different size, not a turn
    }

    [Fact]
    public void No_copy_is_chosen_for_none()
    {
        Assert.Null(LaptopLayout.DuplicateScreen(Desktop, DuplicateTarget.None));
    }

    [Fact]
    public void Names_parse_and_default_to_the_current_behaviour()
    {
        Assert.Equal(LaptopPlacement.BelowRight, LaptopLayout.ParsePlacement("below-right"));
        Assert.Equal(LaptopPlacement.Right, LaptopLayout.ParsePlacement(null));
        Assert.Equal(LaptopPlacement.Right, LaptopLayout.ParsePlacement("nonsense"));
        Assert.Equal(DuplicateTarget.Largest, LaptopLayout.ParseDuplicate("largest"));
        Assert.Equal(DuplicateTarget.None, LaptopLayout.ParseDuplicate(""));
        Assert.Equal("below-centered", LaptopLayout.PlacementName(LaptopPlacement.BelowCentered));
    }
}
