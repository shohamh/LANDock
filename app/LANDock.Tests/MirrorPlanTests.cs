using LANDock.Host;

namespace LANDock.Tests;

/// <summary>Steps 2 and 3 of docs/plan-monitor-layout.md: which virtual display mirrors which desktop screen, on which port, and
/// where it goes. Pure logic: no processes, no files.</summary>
public class MirrorPlanTests
{
    private static MonitorOffer Screen(string id, int x, int y, int w, int h, bool primary = false) =>
        new(id, x, y, w, h, 0, primary);

    [Fact]
    public void Three_screens_get_the_three_fixed_instances_and_ports_left_to_right()
    {
        var plan = HostPlanning.PlanDisplays(new[]
        {
            Screen("DISPLAY3", 3000, 0, 1920, 1080),
            Screen("DISPLAY1", 0, 0, 1920, 1080, primary: true),
            Screen("DISPLAY2", 1920, 0, 1920, 1080),
        });
        Assert.Equal(new[] { "DISPLAY1", "DISPLAY2", "DISPLAY3" }, plan.Select(d => d.Screen.Id));
        Assert.Equal(new[] { 49989, 50989, 51989 }, plan.Select(d => d.Port));
        Assert.Equal(new[] { "c", "d", "e" }, plan.Select(d => d.Key));
    }

    [Fact]
    public void Six_screens_is_the_most_and_seven_is_refused()
    {
        var six = Enumerable.Range(0, 6).Select(i => Screen($"D{i}", i * 1920, 0, 1920, 1080)).ToList();
        Assert.Equal(6, HostPlanning.PlanDisplays(six).Count);
        Assert.Equal(54989, HostPlanning.PlanDisplays(six).Last().Port);
        var seven = Enumerable.Range(0, 7).Select(i => Screen($"D{i}", i * 1920, 0, 1920, 1080)).ToList();
        Assert.Throws<ArgumentException>(() => HostPlanning.PlanDisplays(seven));
    }

    [Fact]
    public void No_screens_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HostPlanning.PlanDisplays(new List<MonitorOffer>()));
    }

    [Fact]
    public void A_screen_that_stays_keeps_its_instance_and_a_new_one_takes_the_lowest_free_slot()
    {
        var running = HostPlanning.PlanDisplays(new[] { Screen("A", 0, 0, 1920, 1080), Screen("B", 1920, 0, 1920, 1080) });
        var wanted = HostPlanning.PlanDisplays(new[] { Screen("B", 1920, 0, 1920, 1080), Screen("C", 3840, 0, 1920, 1080) }, running);
        var b = wanted.Single(d => d.Screen.Id == "B");
        var c = wanted.Single(d => d.Screen.Id == "C");
        Assert.Equal(running.Single(d => d.Screen.Id == "B").Key, b.Key);
        Assert.Equal(50989, b.Port);
        Assert.Equal("c", c.Key);   // slot 0 was A's, now free
        Assert.Equal(49989, c.Port);
    }

    [Fact]
    public void The_diff_names_the_added_removed_and_resized_displays()
    {
        var running = HostPlanning.PlanDisplays(new[] { Screen("A", 0, 0, 1920, 1080), Screen("B", 1920, 0, 1920, 1080) });
        var wanted = HostPlanning.PlanDisplays(new[] { Screen("A", 0, 0, 1280, 720), Screen("C", 3200, 0, 1920, 1080) }, running);
        var d = HostPlanning.Diff(running, wanted);
        Assert.Equal(new[] { "C" }, d.Added.Select(x => x.Screen.Id));
        Assert.Equal(new[] { "B" }, d.Removed.Select(x => x.Screen.Id));
        Assert.Equal(new[] { "A" }, d.Resized.Select(x => x.Screen.Id));
    }

    [Fact]
    public void An_unchanged_plan_has_no_changes()
    {
        var running = HostPlanning.PlanDisplays(new[] { Screen("A", 0, 0, 1920, 1080) });
        var d = HostPlanning.Diff(running, running);
        Assert.Empty(d.Added);
        Assert.Empty(d.Removed);
        Assert.Empty(d.Resized);
    }

    private static ScreenInfo Laptop(int w, int h) => new("DISPLAY0", 0, 0, w, h, true);

    [Fact]
    public void The_desktop_screens_follow_the_laptop_screen_with_their_own_arrangement()
    {
        // desktop: a portrait screen to the left of the primary one, so the portrait one has a negative x
        var plan = HostPlanning.PlanDisplays(new[]
        {
            Screen("PORTRAIT", -1080, 0, 1080, 1920),
            Screen("PRIMARY", 0, 0, 1920, 1080, primary: true),
        });
        var pos = HostPlanning.MirrorPositions(Laptop(1920, 1080), plan);
        Assert.Equal((1920, 0), pos["PORTRAIT"]);      // starts right of the laptop screen
        Assert.Equal((3000, 0), pos["PRIMARY"]);       // 1080 further right, the desktop's own gap
    }

    [Fact]
    public void A_screen_above_the_others_is_placed_above_them_and_the_top_is_level_with_the_laptop()
    {
        var plan = HostPlanning.PlanDisplays(new[]
        {
            Screen("LOW", 0, 0, 1920, 1080),
            Screen("HIGH", 0, -1080, 1920, 1080),
        });
        var pos = HostPlanning.MirrorPositions(Laptop(1920, 1080), plan);
        Assert.Equal(0, pos["HIGH"].y);
        Assert.Equal(1080, pos["LOW"].y);
        Assert.Equal(1920, pos["HIGH"].x);
    }

    private static Dictionary<string, ScreenInfo> LaptopScreens(params ScreenInfo[] s) => s.ToDictionary(x => x.Name);

    [Fact]
    public void A_display_showing_a_desktop_screen_is_moved_to_the_place_of_that_screen()
    {
        var laptop = LaptopScreens(new ScreenInfo("DISPLAY0", 0, 0, 1920, 1080, true), new ScreenInfo("DISPLAY6", 0, 0, 1920, 1080, false));
        var report = new List<MonitorOffer> { Screen("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, primary: true) };
        var moves = HostPlanning.PlanMirrorLayout(laptop, new[] { ((string?)"DISPLAY6", (string?)"\\\\.\\DISPLAY1") }, report);
        Assert.Equal(new[] { "DISPLAY6=1920,0" }, moves);
    }

    [Fact]
    public void A_display_already_in_place_is_not_moved()
    {
        var laptop = LaptopScreens(new ScreenInfo("DISPLAY0", 0, 0, 1920, 1080, true), new ScreenInfo("DISPLAY6", 1920, 0, 1920, 1080, false));
        var report = new List<MonitorOffer> { Screen("A", 0, 0, 1920, 1080, primary: true) };
        Assert.Empty(HostPlanning.PlanMirrorLayout(laptop, new[] { ((string?)"DISPLAY6", (string?)"A") }, report));
    }

    [Fact]
    public void Two_displays_on_the_same_desktop_screen_go_side_by_side_not_on_top_of_each_other()
    {
        var laptop = LaptopScreens(new ScreenInfo("DISPLAY0", 0, 0, 1920, 1080, true),
            new ScreenInfo("DISPLAY7", 0, 0, 1920, 1080, false), new ScreenInfo("DISPLAY8", 0, 0, 1920, 1080, false));
        var report = new List<MonitorOffer> { Screen("A", 0, 0, 1920, 1080, primary: true) };
        var moves = HostPlanning.PlanMirrorLayout(laptop,
            new[] { ((string?)"DISPLAY7", (string?)"A"), ((string?)"DISPLAY8", (string?)"A") }, report);
        Assert.Equal(new[] { "DISPLAY7=1920,0", "DISPLAY8=3840,0" }, moves);
    }

    [Fact]
    public void A_display_without_a_known_screen_is_left_alone()
    {
        var laptop = LaptopScreens(new ScreenInfo("DISPLAY0", 0, 0, 1920, 1080, true), new ScreenInfo("DISPLAY6", 0, 0, 1920, 1080, false));
        var report = new List<MonitorOffer> { Screen("A", 0, 0, 1920, 1080, primary: true) };
        Assert.Empty(HostPlanning.PlanMirrorLayout(laptop, new[] { ((string?)"DISPLAY6", (string?)null), ((string?)null, (string?)"A") }, report));
        Assert.Empty(HostPlanning.PlanMirrorLayout(laptop, new[] { ((string?)"DISPLAY6", (string?)"NOT-IN-REPORT") }, report));
    }
}
