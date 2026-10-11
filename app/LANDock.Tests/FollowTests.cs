using System.Drawing;
using LANDock.Client;
using LANDock.Core;

namespace LANDock.Tests;

/// <summary>Step 6 of docs/plan-monitor-layout.md: the desktop follows its screens. Pure rules, no screens needed.</summary>
public class FollowTests
{
    private static MonitorInfo Mon(int index, string name, int x, int w, int h, bool primary = false) =>
        new(index, name, new Rectangle(x, 0, w, h), primary);

    private static StreamSettings Stream(string name, string screenId, int monitor = 1, bool enabled = true) =>
        new() { Name = name, ScreenId = screenId, Monitor = monitor, Enabled = enabled };

    [Fact]
    public void A_display_stays_on_its_screen_by_id_even_when_the_numbers_change()
    {
        // a screen was plugged in on the left: the display that was number 1 is now number 2
        var now = new List<MonitorInfo> { Mon(1, "\\\\.\\DISPLAY6", 0, 1920, 1080), Mon(2, "\\\\.\\DISPLAY1", 1920, 1920, 1080, primary: true) };
        var s = Stream("M1", "\\\\.\\DISPLAY1", monitor: 1);
        Assert.Equal("\\\\.\\DISPLAY1", ClientService.ScreenOf(s, now)!.Name);
    }

    [Fact]
    public void A_display_whose_screen_is_gone_is_not_moved_to_another_screen()
    {
        var now = new List<MonitorInfo> { Mon(1, "\\\\.\\DISPLAY1", 0, 1920, 1080, primary: true) };
        var s = Stream("M2", "\\\\.\\DISPLAY9", monitor: 1);   // number 1 exists, but the display's own screen does not
        Assert.Null(ClientService.ScreenOf(s, now));
    }

    [Fact]
    public void A_display_from_older_settings_without_an_id_uses_its_number()
    {
        var now = new List<MonitorInfo> { Mon(1, "\\\\.\\DISPLAY1", 0, 1920, 1080, primary: true), Mon(2, "\\\\.\\DISPLAY2", 1920, 1920, 1080) };
        Assert.Equal("\\\\.\\DISPLAY2", ClientService.ScreenOf(Stream("TV", "", monitor: 2), now)!.Name);
    }

    [Fact]
    public void The_changes_are_the_screens_added_removed_and_moved_or_resized()
    {
        var before = new List<MonitorInfo> { Mon(1, "A", 0, 1920, 1080, true), Mon(2, "B", 1920, 1920, 1080), Mon(3, "C", 3840, 1920, 1080) };
        // B is resized; C is only moved (its place changed, its size did not): a move is not a change of display
        var after = new List<MonitorInfo> { Mon(1, "A", 0, 1920, 1080, true), Mon(2, "B", 1920, 1280, 720), Mon(3, "D", 3840, 1920, 1080) };
        var (added, removed, changed) = ClientService.ScreenChanges(before, after);
        Assert.Equal(new[] { "D" }, added.Select(m => m.Name));
        Assert.Equal(new[] { "C" }, removed.Select(m => m.Name));
        Assert.Equal(new[] { "B" }, changed.Select(m => m.Name));
        var moved = new List<MonitorInfo> { Mon(1, "A", 0, 1920, 1080, true), Mon(2, "B", 0, 1920, 1080), Mon(3, "C", 3840, 1920, 1080) };
        Assert.Empty(ClientService.ScreenChanges(before, moved).changed);
    }

    [Fact]
    public void No_change_is_no_change()
    {
        var mons = new List<MonitorInfo> { Mon(1, "A", 0, 1920, 1080, true) };
        var (added, removed, changed) = ClientService.ScreenChanges(mons, mons);
        Assert.Empty(added);
        Assert.Empty(removed);
        Assert.Empty(changed);
    }

    [Fact]
    public void A_new_screen_takes_a_display_that_never_had_a_screen_before_one_that_had_one()
    {
        var h = new HostProfile { Streams = new List<StreamSettings> { Stream("M1", "\\\\.\\DISPLAY1"), Stream("M2", "", enabled: false), Stream("TV", "", enabled: false) } };
        var now = new List<MonitorInfo> { Mon(1, "\\\\.\\DISPLAY1", 0, 1920, 1080, true), Mon(2, "\\\\.\\DISPLAY6", 1920, 1920, 1080) };
        var free = ClientService.FreeStream(h, now, new Dictionary<string, DateTime>());
        Assert.Equal("M2", free!.Name);
    }

    [Fact]
    public void A_display_waiting_for_its_screen_to_come_back_is_not_given_to_a_new_screen()
    {
        var h = new HostProfile { Streams = new List<StreamSettings> { Stream("M1", "\\\\.\\DISPLAY6", enabled: true), Stream("M2", "", enabled: false) } };
        var now = new List<MonitorInfo> { Mon(1, "\\\\.\\DISPLAY1", 0, 1920, 1080, true) };
        var waiting = new Dictionary<string, DateTime> { ["M1"] = DateTime.Now };
        Assert.Equal("M2", ClientService.FreeStream(h, now, waiting)!.Name);
        Assert.Null(ClientService.FreeStream(h, now, new Dictionary<string, DateTime> { ["M1"] = DateTime.Now, ["M2"] = DateTime.Now }));
    }
}
