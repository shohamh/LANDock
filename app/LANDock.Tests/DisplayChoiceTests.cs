using LANDock.Host;

namespace LANDock.Tests;

/// <summary>Step 4 of docs/plan-monitor-layout.md: a display's choices belong to the desktop's screen. Choices saved by older versions
/// (keyed by display name) still apply when no screen entry exists.</summary>
public class DisplayChoiceTests
{
    [Fact]
    public void A_screen_uses_its_own_choice_first()
    {
        var list = new List<DisplayPolicy> { new("\\\\.\\DISPLAY2", Enabled: false), new("M1", MaxWidth: 1280, MaxHeight: 720) };
        var p = DisplayPolicies.Choose(list, "\\\\.\\DISPLAY2", "M1");
        Assert.False(p.Enabled);
        Assert.Equal("\\\\.\\DISPLAY2", p.Name);
    }

    [Fact]
    public void Without_a_screen_entry_the_choice_saved_for_the_display_name_applies()
    {
        var list = new List<DisplayPolicy> { new("M1", MaxWidth: 1280, MaxHeight: 720) };
        var p = DisplayPolicies.Choose(list, "\\\\.\\DISPLAY7", "M1");
        Assert.Equal(1280, p.MaxWidth);
    }

    [Fact]
    public void Without_any_entry_the_defaults_apply_to_the_screen()
    {
        var p = DisplayPolicies.Choose(null, "\\\\.\\DISPLAY3", "M2");
        Assert.True(p.Enabled);
        Assert.Equal(3840, p.MaxWidth);
        Assert.Equal(60, p.MaxFps);
        Assert.Equal("\\\\.\\DISPLAY3", p.Name);
    }

    [Fact]
    public void An_old_desktop_without_a_screen_id_is_looked_up_by_its_display_name()
    {
        var list = new List<DisplayPolicy> { new("TV", Enabled: false) };
        Assert.False(DisplayPolicies.Choose(list, null, "TV").Enabled);
    }

    [Fact]
    public void The_screens_of_a_desktop_survive_a_restart_of_the_list()
    {
        var screens = new List<MonitorOffer> { new("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, 0, true) };
        var json = System.Text.Json.JsonSerializer.Serialize(new HostIdentity.PairedDesktop("PC", "h", "2026-10-11 09:00", "", null, screens));
        var back = System.Text.Json.JsonSerializer.Deserialize<HostIdentity.PairedDesktop>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(screens, back.Screens);
        // an entry written before screens were kept reads with none
        var old = System.Text.Json.JsonSerializer.Deserialize<HostIdentity.PairedDesktop>("{\"Name\":\"PC\",\"TokenHash\":\"h\",\"Created\":\"x\"}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Null(old.Screens);
    }
}
