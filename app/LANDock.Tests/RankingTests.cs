using LANDock.Core;
using LANDock.Discovery;

namespace LANDock.Tests;

/// <summary>The order the desktop tries laptops in: nearby, recency, favorites, the manual order.</summary>
public class RankingTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0);

    private static HostProfile Saved(string id, string name, bool favorite = false) =>
        new() { HostId = id, Name = name, Address = "10.0.0." + id, ControlPort = 49900, Favorite = favorite, Fingerprint = "FP", TokenProtected = "x" };

    private static DiscoveredLaptop Found(string id, string name, TimeSpan seenAgo, double? heardDb = null, TimeSpan? heardAgo = null) => new()
    {
        HostId = id, Name = name, Address = "10.0.0." + id, ControlPort = 49900, LastSeen = Now - seenAgo,
        HeardUsAt = heardAgo is { } a ? Now - a : null, HeardUsDb = heardDb ?? 0,
    };

    [Fact]
    public void A_laptop_that_heard_this_pc_comes_first_and_the_loudest_leads()
    {
        var list = ClientRanking.Build(
            new[] { Saved("1", "a"), Saved("2", "b"), Saved("3", "c") },
            new[] { Found("1", "a", TimeSpan.FromSeconds(2)), Found("2", "b", TimeSpan.FromSeconds(2), 9, TimeSpan.FromSeconds(3)), Found("3", "c", TimeSpan.FromSeconds(2), 15, TimeSpan.FromSeconds(3)) },
            new List<string>(), Now);
        Assert.Equal(new[] { "3", "2", "1" }, list.Select(e => e.HostId).ToArray());
        Assert.True(list[0].Nearby);
    }

    [Fact]
    public void A_heard_mark_older_than_a_minute_does_not_count_as_nearby()
    {
        var list = ClientRanking.Build(new[] { Saved("1", "a") }, new[] { Found("1", "a", TimeSpan.FromSeconds(2), 12, TimeSpan.FromMinutes(5)) }, new List<string>(), Now);
        Assert.False(list[0].Nearby);
    }

    [Fact]
    public void Recency_comes_before_favorites_and_favorites_before_the_manual_order()
    {
        // all three are online-recent (seen 2 s ago): favorites decide, then the manual order
        var list = ClientRanking.Build(
            new[] { Saved("1", "a"), Saved("2", "b", favorite: true), Saved("3", "c") },
            new[] { Found("1", "a", TimeSpan.FromSeconds(2)), Found("2", "b", TimeSpan.FromSeconds(2)), Found("3", "c", TimeSpan.FromSeconds(2)) },
            new List<string> { "3", "1" }, Now);
        Assert.Equal(new[] { "2", "3", "1" }, list.Select(e => e.HostId).ToArray());
    }

    [Fact]
    public void A_laptop_seen_recently_beats_a_favorite_that_was_not_seen_for_hours()
    {
        var list = ClientRanking.Build(
            new[] { Saved("1", "a", favorite: true), Saved("2", "b") },
            new[] { Found("2", "b", TimeSpan.FromSeconds(3)) },
            new List<string>(), Now);
        Assert.Equal("2", list[0].HostId);
        Assert.True(list[0].Online);
        Assert.False(list[1].Online);
    }

    [Fact]
    public void Found_laptops_that_are_not_saved_are_listed_and_marked_as_not_paired()
    {
        var list = ClientRanking.Build(new[] { Saved("1", "a") }, new[] { Found("9", "new-laptop", TimeSpan.FromSeconds(1)) }, new List<string>(), Now);
        var nw = list.Single(e => e.HostId == "9");
        Assert.False(nw.Saved);
        Assert.False(nw.Paired);
        Assert.Equal("online", nw.Status);   // seen just now, so online (not saved yet)
    }

    [Fact]
    public void Auto_connect_takes_the_first_paired_online_laptop_in_the_order()
    {
        var a = Saved("1", "a");
        a.TokenProtected = "";                       // not paired: never connected to automatically
        var list = ClientRanking.Build(new[] { a, Saved("2", "b") },
            new[] { Found("1", "a", TimeSpan.FromSeconds(1)), Found("2", "b", TimeSpan.FromSeconds(1)) }, new List<string>(), Now);
        Assert.Equal("2", ClientRanking.AutoConnectChoice(list)!.HostId);
    }

    [Fact]
    public void Without_a_discovery_match_a_saved_laptop_is_offline()
    {
        var list = ClientRanking.Build(new[] { Saved("1", "a") }, Array.Empty<DiscoveredLaptop>(), new List<string>(), Now);
        Assert.False(list[0].Online);
        Assert.Null(ClientRanking.AutoConnectChoice(list));
    }

    [Fact]
    public void Recency_buckets_are_ordered()
    {
        Assert.True(ClientRanking.RecencyBucket(Now - TimeSpan.FromSeconds(5), Now) < ClientRanking.RecencyBucket(Now - TimeSpan.FromMinutes(5), Now));
        Assert.True(ClientRanking.RecencyBucket(Now - TimeSpan.FromMinutes(5), Now) < ClientRanking.RecencyBucket(Now - TimeSpan.FromHours(5), Now));
        Assert.True(ClientRanking.RecencyBucket(Now - TimeSpan.FromHours(5), Now) < ClientRanking.RecencyBucket(null, Now));
    }
}
