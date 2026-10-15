using System.Drawing;
using LANDock.Host;
using LANDock.Ui;
using Xunit;

namespace LANDock.Tests;

public class NoticeLayoutTests
{
    private static readonly Rectangle Work = new(0, 0, 1920, 1040);   // the screen minus a 40 px taskbar

    [Fact]
    public void The_notice_sits_above_the_taskbar_on_the_right_for_left_to_right()
    {
        var p = NoticeLayout.Corner(Work, new Size(300, 200), rightToLeft: false, margin: 10);
        Assert.Equal(1920 - 300 - 10, p.X);
        Assert.Equal(1040 - 200 - 10, p.Y);
    }

    [Fact]
    public void The_notice_sits_on_the_left_for_right_to_left_languages()
    {
        var p = NoticeLayout.Corner(Work, new Size(300, 200), rightToLeft: true, margin: 10);
        Assert.Equal(10, p.X);
        Assert.Equal(1040 - 200 - 10, p.Y);
    }

    [Fact]
    public void Screens_keep_their_relative_positions_and_sizes()
    {
        // landscape on the left, a portrait screen to its right, one above both
        var screens = new[]
        {
            new NoticeScreen("A", 0, 0, 1920, 1080, true),
            new NoticeScreen("B", 1920, 0, 1080, 1920, false),
            new NoticeScreen("C", 0, -1080, 1920, 1080, false),
        };
        var boxes = NoticeLayout.Diagram(screens, new Rectangle(0, 0, 400, 200), gap: 0, minSide: 1);
        var a = boxes[0].Bounds; var b = boxes[1].Bounds; var c = boxes[2].Bounds;
        Assert.True(a.Left < b.Left);                 // B is right of A
        Assert.True(c.Bottom <= a.Top + 1);           // C is above A
        Assert.True(b.Height > b.Width);              // portrait drawn tall
        Assert.True(boxes[1].Portrait);
        Assert.True(a.Width > 0 && c.Width > 0);      // every screen gets a size
        Assert.Equal("1920x1080", boxes[0].Label);
        Assert.Equal("1080x1920", boxes[1].Label);
        Assert.True(boxes[0].Primary);
    }

    [Fact]
    public void Every_box_lies_inside_the_area()
    {
        var screens = new[] { new NoticeScreen("A", 0, 0, 3840, 2160, true), new NoticeScreen("B", 3840, 0, 1280, 1024, false) };
        var area = new Rectangle(8, 8, 320, 180);
        foreach (var box in NoticeLayout.Diagram(screens, area))
        {
            Assert.True(area.Contains(box.Bounds), $"{box.Bounds} is outside {area}");
        }
    }

    [Fact]
    public void A_bigger_resolution_is_drawn_bigger()
    {
        var screens = new[] { new NoticeScreen("A", 0, 0, 3840, 2160, true), new NoticeScreen("B", 3840, 0, 1280, 1024, false) };
        var boxes = NoticeLayout.Diagram(screens, new Rectangle(0, 0, 400, 200), gap: 0, minSide: 1);
        Assert.True(boxes[0].Bounds.Width * boxes[0].Bounds.Height > boxes[1].Bounds.Width * boxes[1].Bounds.Height);
    }

    [Fact]
    public void A_box_keeps_its_screen_id_and_whether_it_is_on()
    {
        var screens = new[] { new NoticeScreen("A", 0, 0, 1920, 1080, true), new NoticeScreen("B", 1920, 0, 1920, 1080, false, Enabled: false) };
        var boxes = NoticeLayout.Diagram(screens, new Rectangle(0, 0, 400, 200));
        Assert.Equal(new[] { "A", "B" }, boxes.Select(b => b.Id));
        Assert.Equal(new[] { true, false }, boxes.Select(b => b.Enabled));
    }

    [Fact]
    public void Bitrate_defaults_to_a_moderate_value_and_spare_displays_are_not_paired()
    {
        var c = new LANDock.Core.ClientSettings();
        Assert.Equal(20000, c.BitrateKbps);
        Assert.False(c.PairSpareDisplays);
    }

    [Fact]
    public void No_screens_draws_nothing() =>
        Assert.Empty(NoticeLayout.Diagram(Array.Empty<NoticeScreen>(), new Rectangle(0, 0, 400, 200)));
}

public class ConnectedAndRestoreTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0);

    [Fact]
    public void A_heartbeat_keeps_a_copy_stream_connected_with_no_virtual_display() =>
        Assert.True(NoticeService.ConnectedNow(Now, Now.AddMinutes(-30), Now.AddSeconds(-5), displayRunning: false));

    [Fact]
    public void Without_a_heartbeat_for_fifteen_seconds_and_no_display_the_desktop_is_not_connected() =>
        Assert.False(NoticeService.ConnectedNow(Now, Now.AddMinutes(-30), Now.AddSeconds(-16), displayRunning: false));

    [Fact]
    public void A_recent_session_counts_while_the_displays_start() =>
        Assert.True(NoticeService.ConnectedNow(Now, Now.AddSeconds(-30), DateTime.MinValue, displayRunning: false));

    [Fact]
    public void A_running_virtual_display_counts_on_its_own() =>
        Assert.True(NoticeService.ConnectedNow(Now, DateTime.MinValue, DateTime.MinValue, displayRunning: true));

    [Fact]
    public void Nothing_at_all_is_not_connected() =>
        Assert.False(NoticeService.ConnectedNow(Now, DateTime.MinValue, DateTime.MinValue, displayRunning: false));

    [Fact]
    public void The_setup_is_not_put_back_while_heartbeats_arrive()
    {
        // a copy stream: the desktop beats every 5 s, no display ever ran. Even after two minutes the setup stays
        var lastBeat = Now.AddSeconds(-5);
        Assert.False(HostService.RestoreDue(Now, Now.AddMinutes(-2), Now.AddMinutes(-2), lastBeat, saved: true));
    }

    [Fact]
    public void The_setup_comes_back_a_minute_after_the_last_sign_of_use()
    {
        var lastActive = Now.AddSeconds(-61);
        Assert.True(HostService.RestoreDue(Now, Now.AddMinutes(-1), Now.AddMinutes(-5), lastActive, saved: true));
        Assert.False(HostService.RestoreDue(Now, Now.AddMinutes(-1), Now.AddMinutes(-5), Now.AddSeconds(-30), saved: true));
    }

    [Fact]
    public void A_session_with_no_stream_gets_three_minutes_to_start()
    {
        // the desktop asked two minutes ago and nothing ran yet: still waiting for Moonlight
        Assert.False(HostService.RestoreDue(Now, Now.AddMinutes(-2), Now.AddMinutes(-2), DateTime.MinValue, saved: true));
        Assert.True(HostService.RestoreDue(Now, Now.AddMinutes(-4), Now.AddMinutes(-4), DateTime.MinValue, saved: true));
    }

    [Fact]
    public void Nothing_is_put_back_when_nothing_was_saved() =>
        Assert.False(HostService.RestoreDue(Now, Now.AddMinutes(-10), Now.AddMinutes(-10), Now.AddMinutes(-10), saved: false));
}

public class NoticeSourceTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0);

    [Fact]
    public void A_recently_heard_desktop_is_recognized_by_ultrasound() =>
        Assert.Equal(new[] { "ultrasound" }, NoticeService.SourcesOf("d", Now,
            id => (Now.AddSeconds(-20), -40.0), id => false));

    [Fact]
    public void An_old_ultrasound_sighting_does_not_count() =>
        Assert.Empty(NoticeService.SourcesOf("d", Now, id => (Now.AddSeconds(-61), -40.0), id => false));

    [Fact]
    public void A_desktop_seen_on_the_lan_is_recognized_by_lan() =>
        Assert.Equal(new[] { "LAN" }, NoticeService.SourcesOf("d", Now, id => null, id => id == "d"));

    [Fact]
    public void Both_routes_are_listed_when_both_see_the_desktop() =>
        Assert.Equal(new[] { "ultrasound", "LAN" }, NoticeService.SourcesOf("d", Now, id => (Now, -30.0), id => true));

    [Fact]
    public void Another_desktops_sighting_is_ignored() =>
        Assert.Empty(NoticeService.SourcesOf("d", Now, id => id == "other" ? (Now, -30.0) : null, id => id == "other"));
}

public class NoticeTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0);

    [Fact]
    public void A_recognized_desktop_that_is_not_connected_shows_its_notice_once()
    {
        var t = new NoticeTracker();
        t.Seen("d", T0);
        Assert.True(t.Update("d", T0, connected: false));
        Assert.False(t.Update("d", T0.AddSeconds(2), connected: false));   // still there: no second appearance
        Assert.False(t.Update("d", T0.AddSeconds(4), connected: false));
    }

    [Fact]
    public void A_connected_desktop_gets_no_notice()
    {
        var t = new NoticeTracker();
        t.Seen("d", T0);
        Assert.False(t.Update("d", T0, connected: true));
    }

    [Fact]
    public void A_desktop_that_is_no_longer_seen_is_no_longer_recognized()
    {
        var t = new NoticeTracker();
        t.Seen("d", T0);
        Assert.True(t.Recognized("d", T0.AddSeconds(59)));
        Assert.False(t.Recognized("d", T0.AddSeconds(61)));
    }

    [Fact]
    public void Dismiss_hides_the_notice_while_the_desktop_stays()
    {
        var t = new NoticeTracker();
        t.Seen("d", T0);
        Assert.True(t.Update("d", T0, false));
        t.Dismiss("d");
        t.Seen("d", T0.AddSeconds(5));
        Assert.False(t.ShouldShow("d", T0.AddSeconds(5), false));
        Assert.False(t.Update("d", T0.AddSeconds(5), false));
    }

    [Fact]
    public void A_dismissed_desktop_that_was_away_for_ten_minutes_shows_again()
    {
        var t = new NoticeTracker();
        t.Seen("d", T0);
        t.Update("d", T0, false);
        t.Dismiss("d");
        var back = T0.AddMinutes(11);
        t.Seen("d", back);
        Assert.True(t.Update("d", back, false));
    }

    [Fact]
    public void Snooze_hides_the_notice_for_its_length_and_then_shows_it_again()
    {
        var t = new NoticeTracker();
        t.Seen("d", T0);
        Assert.True(t.Update("d", T0, false));
        t.Snooze("d", T0, TimeSpan.FromMinutes(10));
        t.Seen("d", T0.AddMinutes(5));
        Assert.False(t.Update("d", T0.AddMinutes(5), false));
        t.Seen("d", T0.AddMinutes(10));
        Assert.True(t.Update("d", T0.AddMinutes(10), false));
    }

    [Fact]
    public void Each_desktop_is_tracked_on_its_own()
    {
        var t = new NoticeTracker();
        t.Seen("a", T0);
        t.Seen("b", T0);
        t.Dismiss("a");
        Assert.False(t.ShouldShow("a", T0, false));
        Assert.True(t.ShouldShow("b", T0, false));
    }
}
