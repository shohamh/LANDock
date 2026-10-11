using System.Text.Json;
using LANDock.Client;
using LANDock.Host;

namespace LANDock.Tests;

/// <summary>Step 1 of docs/plan-monitor-layout.md: a desktop reports the screens it uses. A desktop that does not report them
/// (an older version) is still read, with no screens, and keeps the fixed displays.</summary>
public class MonitorOfferTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_session_request_without_a_screen_report_is_read_with_no_screens()
    {
        var r = JsonSerializer.Deserialize<SessionRequest>("{\"streams\":[{\"name\":\"M1\",\"width\":1920,\"height\":1080,\"fps\":60}]}", Web)!;
        Assert.Null(r.Monitors);
        Assert.Equal("M1", r.Streams![0].Name);
    }

    [Fact]
    public void The_screens_of_a_desktop_are_read_with_their_layout_and_orientation()
    {
        var json = "{\"streams\":[],\"monitors\":[{\"id\":\"DISPLAY2\",\"x\":-1080,\"y\":0,\"width\":1080,\"height\":1920,\"rotation\":90,\"primary\":false}]}";
        var r = JsonSerializer.Deserialize<SessionRequest>(json, Web)!;
        Assert.Equal(new MonitorOffer("DISPLAY2", -1080, 0, 1080, 1920, 90, false), Assert.Single(r.Monitors!));
    }

    [Fact]
    public void The_screen_report_survives_the_trip_the_desktop_sends_it_on()
    {
        var monitors = new List<MonitorOffer> { new("DISPLAY1", 0, 0, 1920, 1080, 0, true) };
        var sent = JsonSerializer.Serialize(new { streams = Array.Empty<int>(), monitors }, Web);
        var back = JsonSerializer.Deserialize<SessionRequest>(sent, Web)!;
        Assert.Equal(new MonitorOffer("DISPLAY1", 0, 0, 1920, 1080, 0, true), Assert.Single(back.Monitors!));
    }

    [Fact]
    public void A_stream_names_the_desktop_screen_it_is_shown_on_and_an_older_stream_names_none()
    {
        var newer = JsonSerializer.Deserialize<SessionRequest>(
            "{\"streams\":[{\"name\":\"M1\",\"width\":1920,\"height\":1080,\"fps\":60,\"screenId\":\"\\\\\\\\.\\\\DISPLAY2\"}]}", Web)!;
        Assert.Equal("\\\\.\\DISPLAY2", newer.Streams![0].ScreenId);
        var older = JsonSerializer.Deserialize<SessionRequest>("{\"streams\":[{\"name\":\"M1\",\"width\":1920,\"height\":1080,\"fps\":60}]}", Web)!;
        Assert.Null(older.Streams![0].ScreenId);
    }

    [Fact]
    public void The_rotation_of_each_screen_of_this_pc_is_one_of_the_four_right_angles()
    {
        foreach (var m in ClientService.Monitors())
            Assert.Contains(ClientService.ScreenRotation(m.Name), new[] { 0, 90, 180, 270 });
    }
}
