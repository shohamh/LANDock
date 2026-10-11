using LANDock.Core;
using LANDock.Host;

namespace LANDock.Tests;

/// <summary>What a laptop offers each desktop: which displays it gets, and the largest size and frame rate, remembered per desktop.</summary>
[Collection("paths")]
public class DisplayPolicyTests : IDisposable
{
    private readonly string _name = "policy-test-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose() => HostIdentity.Forget(_name);

    [Fact]
    public void Without_choices_every_display_is_on_at_the_defaults()
    {
        var p = DisplayPolicies.For(null, "M1");
        Assert.True(p.Enabled);
        Assert.Equal((3840, 2160, 60), (p.MaxWidth, p.MaxHeight, p.MaxFps));
    }

    [Fact]
    public void A_display_that_is_off_for_a_desktop_is_not_offered_to_it()
    {
        var token = HostIdentity.Issue(_name);
        HostIdentity.SetDisplays(_name, new List<DisplayPolicy> { new("TV", Enabled: false) });
        var host = new HostService(new Settings());
        var (ok, agreed, _) = host.Agree(new List<StreamOffer> { new("M1", 1920, 1080, 60), new("TV", 3840, 2160, 60) }, token);
        Assert.True(ok);
        Assert.Single(agreed);
        Assert.Equal("M1", agreed[0].Name);
    }

    [Fact]
    public void A_size_is_kept_within_the_largest_size_chosen_for_the_desktop()
    {
        var token = HostIdentity.Issue(_name);
        HostIdentity.SetDisplays(_name, new List<DisplayPolicy> { new("M1", MaxWidth: 1920, MaxHeight: 1080, MaxFps: 30) });
        var host = new HostService(new Settings());
        var a = host.Agree(new List<StreamOffer> { new("M1", 3840, 2160, 60) }, token).agreed.Single();
        Assert.Equal((1920, 1080, 30), (a.Width, a.Height, a.Fps));
    }

    [Fact]
    public void The_choices_are_remembered_per_desktop_and_survive_a_reload()
    {
        HostIdentity.Issue(_name);
        var choices = new List<DisplayPolicy> { new("M2", Enabled: false), new("M1", MaxWidth: 2560, MaxHeight: 1440) };
        HostIdentity.SetDisplays(_name, choices);
        var back = HostIdentity.DisplaysOf(_name)!;
        Assert.False(DisplayPolicies.For(back, "M2").Enabled);
        Assert.Equal(2560, DisplayPolicies.For(back, "M1").MaxWidth);
        // another desktop has its own choices, not these
        var other = "policy-other-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            HostIdentity.Issue(other);
            Assert.Null(HostIdentity.DisplaysOf(other));
        }
        finally { HostIdentity.Forget(other); }
    }

    [Fact]
    public void The_size_labels_match_the_offered_sizes()
    {
        Assert.Equal("4K (3840x2160)", DisplayPolicies.SizeLabel(3840, 2160));
        Assert.Equal("1234x567", DisplayPolicies.SizeLabel(1234, 567));
    }
}
