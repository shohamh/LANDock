namespace LANDock.Host;

/// <summary>
/// Decides when a paired desktop's notice shows on the laptop. A desktop is recognized while it was heard (ultrasound) or seen
/// (LAN) in the last minute. The notice shows when it is recognized, not connected, not snoozed and not dismissed. It shows once
/// per arrival: it does not come back on its own while the desktop stays, and a dismissal lasts until the desktop has been away.
/// The clock is passed in, so the timing can be tested.
/// </summary>
public sealed class NoticeTracker
{
    public static readonly TimeSpan RecognizedFor = TimeSpan.FromSeconds(60);
    /// <summary>A desktop gone this long counts as a new arrival: an earlier dismissal ends.</summary>
    public static readonly TimeSpan AwayFor = TimeSpan.FromMinutes(10);

    private sealed class State
    {
        public DateTime LastSeen = DateTime.MinValue;
        public DateTime SnoozedUntil = DateTime.MinValue;
        public bool Dismissed;
        public bool Showing;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    private State Get(string id)
    {
        if (!_states.TryGetValue(id, out var s)) _states[id] = s = new State();
        return s;
    }

    /// <summary>The desktop was heard or seen now.</summary>
    public void Seen(string id, DateTime now)
    {
        var s = Get(id);
        // back after a gap: a new arrival (the notice may show again, and an earlier dismissal ends after AwayFor)
        if (s.LastSeen == DateTime.MinValue || now - s.LastSeen > RecognizedFor) s.Showing = false;
        if (s.LastSeen != DateTime.MinValue && now - s.LastSeen > AwayFor) s.Dismissed = false;
        s.LastSeen = now;
    }

    public bool Recognized(string id, DateTime now)
    {
        var s = Get(id);
        return s.LastSeen != DateTime.MinValue && now - s.LastSeen <= RecognizedFor;
    }

    /// <summary>True when the notice should be shown for this desktop now.</summary>
    public bool ShouldShow(string id, DateTime now, bool connected)
    {
        var s = Get(id);
        return Recognized(id, now) && !connected && !s.Dismissed && now >= s.SnoozedUntil;
    }

    /// <summary>
    /// Called on each tick. Returns true once when the notice has to appear for this arrival (the moment ShouldShow turns true).
    /// </summary>
    public bool Update(string id, DateTime now, bool connected)
    {
        var s = Get(id);
        var should = ShouldShow(id, now, connected);
        var appear = should && !s.Showing;
        s.Showing = should;
        return appear;
    }

    public void Dismiss(string id) => Get(id).Dismissed = true;

    public void Snooze(string id, DateTime now, TimeSpan length)
    {
        var s = Get(id);
        s.SnoozedUntil = now + length;
        s.Showing = false;
    }
}
