using LANDock.Core;

namespace LANDock.Discovery;

/// <summary>One laptop in the desktop's list: a saved host, a laptop found on the LAN, or both.</summary>
public sealed class ClientEntry
{
    /// <summary>The laptop's id (the saved host's id, or the advertised one). Used for the manual order.</summary>
    public string HostId { get; init; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public bool Saved { get; set; }
    public bool Paired { get; set; }
    public bool Online { get; set; }
    public DateTime? LastSeen { get; set; }
    public bool Nearby { get; set; }
    public double NearbyDb { get; set; }
    public DateTime? HeardUsAt { get; set; }
    public bool Favorite { get; set; }
    public int ManualIndex { get; set; } = int.MaxValue;
    public string Version { get; set; } = "";
    public HostProfile? Profile { get; set; }
    public DiscoveredLaptop? Found { get; set; }

    public string Status => Nearby ? "nearby" : Online ? "online" : Saved ? "offline" : "found";
}

/// <summary>
/// Builds the list of laptops the desktop can connect to, in the order it should try them:
/// 1. nearby: the laptop heard this PC's ultrasound recently (the loudest first);
/// 2. recency: seen on the LAN within 15 s, then 10 min, a day, older;
/// 3. favorites first;
/// 4. the manual order the user set with Move up / Move down;
/// 5. the most recently seen, then name.
/// </summary>
public static class ClientRanking
{
    public static readonly TimeSpan OnlineWithin = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan NearbyWithin = TimeSpan.FromSeconds(60);

    public static int RecencyBucket(DateTime? lastSeen, DateTime now)
    {
        if (lastSeen is not { } seen) return 4;
        var age = now - seen;
        return age <= OnlineWithin ? 0 : age <= TimeSpan.FromMinutes(10) ? 1 : age <= TimeSpan.FromDays(1) ? 2 : 3;
    }

    public static List<ClientEntry> Build(IEnumerable<HostProfile> saved, IEnumerable<DiscoveredLaptop> found,
                                          IReadOnlyList<string> manualOrder, DateTime now)
    {
        var foundById = found.ToDictionary(f => f.HostId, StringComparer.OrdinalIgnoreCase);
        var entries = new List<ClientEntry>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var h in saved)
        {
            var id = h.HostId != "" ? h.HostId : h.Id;
            var e = new ClientEntry
            {
                HostId = id, Name = h.Name, Address = h.Address, Port = h.ControlPort, Saved = true, Paired = h.Paired,
                Favorite = h.Favorite, Profile = h,
                LastSeen = DateTime.TryParse(h.LastConnected, out var last) ? last : null,
            };
            if (h.HostId != "" && foundById.TryGetValue(h.HostId, out var f)) { Merge(e, f); matched.Add(h.HostId); }
            entries.Add(e);
        }
        foreach (var f in foundById.Values)
        {
            if (matched.Contains(f.HostId)) continue;
            var e = new ClientEntry { HostId = f.HostId, Name = f.Name, Address = f.Address, Port = f.ControlPort };
            Merge(e, f);
            entries.Add(e);
        }

        foreach (var e in entries)
        {
            e.Online = e.Found != null && now - e.Found.LastSeen <= OnlineWithin;
            e.Nearby = e.HeardUsAt is { } at && now - at <= NearbyWithin;
            var idx = manualOrder.ToList().IndexOf(e.HostId);
            e.ManualIndex = idx < 0 ? int.MaxValue : idx;
        }

        return entries
            .OrderByDescending(e => e.Nearby)
            .ThenByDescending(e => e.Nearby ? e.NearbyDb : double.MinValue)
            .ThenBy(e => RecencyBucket(e.LastSeen, now))
            .ThenByDescending(e => e.Favorite)
            .ThenBy(e => e.ManualIndex)
            .ThenByDescending(e => e.LastSeen ?? DateTime.MinValue)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Merge(ClientEntry e, DiscoveredLaptop f)
    {
        e.Found = f;
        e.Version = f.Version;
        e.Address = f.Address;
        e.Port = f.ControlPort;
        e.LastSeen = f.LastSeen;
        if (f.HeardUsAt is { } at) { e.HeardUsAt = at; e.NearbyDb = f.HeardUsDb; }
        if (e.Name == "") e.Name = f.Name;
    }

    /// <summary>The laptop to connect to automatically: the first one in the order that is paired and online.</summary>
    public static ClientEntry? AutoConnectChoice(IEnumerable<ClientEntry> ordered) =>
        ordered.FirstOrDefault(e => e.Saved && e.Paired && e.Online);
}
