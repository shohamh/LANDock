using LANDock.Discovery;
using LANDock.Core;
using LANDock.Ui;
using LANDock.Ultrasound;

namespace LANDock.Host;

/// <summary>What the notice shows about a recognized desktop: its id and name, its screens, and how it was recognized.</summary>
public sealed record NoticeInfo(string DesktopId, string Name, IReadOnlyList<NoticeScreen> Screens, string Source);

/// <summary>
/// The laptop's notice about nearby paired desktops. A paired desktop is recognized when the laptop hears its ultrasound id or sees
/// its LAN presence. The notice comes up for it (see NoticeTracker for when), and Connect starts this laptop's virtual monitors and
/// asks the desktop to start its Moonlight windows.
/// </summary>
public sealed class NoticeService : IDisposable
{
    /// <summary>A desktop that asked for its displays this recently counts as connected (while its displays start).</summary>
    public static readonly TimeSpan ConnectedFor = TimeSpan.FromSeconds(60);

    /// <summary>A desktop whose heartbeat is this recent is connected: it sends one every few seconds (ClientService.BeatEvery), so
    /// three missed beats end it.</summary>
    public static readonly TimeSpan BeatFor = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether a desktop counts as connected now, so that no notice is shown for it: it asked for its displays lately, or it sent a heartbeat
    /// lately (a copy stream has no display of its own, so the heartbeat is what shows it is in use), or a display of the laptop runs for it.
    /// </summary>
    public static bool ConnectedNow(DateTime now, DateTime lastSession, DateTime lastBeat, bool displayRunning) =>
        displayRunning || now - lastSession <= ConnectedFor || now - lastBeat <= BeatFor;

    /// <summary>The screens of a desktop that are extended to this laptop: the ones its choice (kept per desktop) keeps on. A screen with no
    /// choice is on. The rest are not extended: they get no display, and the desktop's arrangement still keeps them.</summary>
    public static List<MonitorOffer> ExtendedScreens(IEnumerable<MonitorOffer> screens, IEnumerable<DisplayPolicy>? choices) =>
        screens.Where(s => DisplayPolicies.Choose(choices, s.Id, null).Enabled).ToList();

    /// <summary>The laptop's display for each screen of a desktop: the screens left to right (then top to bottom), one display each, in
    /// the laptop's display order. Connect starts these displays, and the settings draw them, so both use this.</summary>
    public static List<(string Label, MonitorOffer Screen)> DisplaysFor(IEnumerable<MonitorOffer> screens) =>
        screens.OrderBy(s => s.X).ThenBy(s => s.Y).Take(HostPlanning.AllInstances.Count)
            .Select((s, i) => (HostPlanning.AllInstances[i].Label, s)).ToList();

    /// <summary>True while a paired desktop is connected (the rule the notice uses, so the settings and the notice agree).</summary>
    public bool IsConnected(HostIdentity.PairedDesktop d) =>
        ConnectedNow(DateTime.Now, _host.LastSessionOf(d.TokenHash), _host.LastBeatOf(d.TokenHash),
            _host.Launched().Any(i => HostService.ActiveDisplayOf(i) != null));

    private readonly HostService _host;
    private readonly PresenceBrowser _presence;
    private readonly NoticeTracker _tracker = new();
    private readonly Dictionary<string, DateTime> _lastHeldLog = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _timer;

    /// <summary>A desktop was recognized and the notice should come up for it.</summary>
    public event Action<NoticeInfo>? Recognized;

    public NoticeService(HostService host, PresenceBrowser presence)
    {
        _host = host;
        _presence = presence;
    }

    public bool Running => _timer != null;

    public void Start()
    {
        if (_timer != null) return;
        _timer = new System.Threading.Timer(_ => { try { Tick(DateTime.Now); } catch (Exception ex) { Log.Error("notice", ex); } }, null, 2000, 2000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Looks at every paired desktop once. Public so that it can be tested with a fixed time.</summary>
    public void Tick(DateTime now)
    {
        if (!_host.Running) return;
        foreach (var d in HostIdentity.Desktops())
        {
            if (string.IsNullOrEmpty(d.UltraId)) continue;   // a desktop paired before ultrasound ids existed cannot be recognized
            var sources = SourcesOf(d.UltraId, DateTime.Now, Heard.Last, id => _presence.SeenWithin(id, TimeSpan.FromSeconds(60)));
            var connected = ConnectedNow(now, _host.LastSessionOf(d.TokenHash), _host.LastBeatOf(d.TokenHash),
                _host.Launched().Any(i => HostService.ActiveDisplayOf(i) != null));
            bool appear;
            lock (_tracker)
            {
                if (sources.Count > 0) _tracker.Seen(d.UltraId, now);
                appear = _tracker.Update(d.UltraId, now, connected);
            }
            if (appear)
            {
                Log.Info($"desktop '{d.Name}' is here ({string.Join(", ", sources)}); showing the notice");
                Recognized?.Invoke(new NoticeInfo(d.UltraId, d.Name, ScreensFor(d.Name), string.Join(" and ", sources)));
            }
            else if (sources.Count > 0 && connected && DateTime.Now - _lastHeldLog.GetValueOrDefault(d.UltraId) > TimeSpan.FromMinutes(1))
            {
                // seen, but no notice: say why (once a minute), so that a missing notice can be traced
                _lastHeldLog[d.UltraId] = DateTime.Now;
                Log.Info($"desktop '{d.Name}' is here ({string.Join(", ", sources)}) but counts as connected: no notice");
            }
        }
    }

    /// <summary>
    /// How a desktop is recognized right now: "ultrasound" when the laptop heard its id within a minute, "LAN" when it was seen on the
    /// LAN within a minute. The lookups are passed in, so this can be tested without sound or a network.
    /// </summary>
    public static List<string> SourcesOf(string desktopId, DateTime now, Func<string, (DateTime at, double db)?> heard, Func<string, bool> seenOnLan)
    {
        var sources = new List<string>();
        if (heard(desktopId) is { } h && (now - h.at).TotalSeconds <= 60) sources.Add("ultrasound");
        if (seenOnLan(desktopId)) sources.Add("LAN");
        return sources;
    }

    public void Dismiss(string desktopId)
    {
        lock (_tracker) _tracker.Dismiss(desktopId);
    }

    public void Snooze(string desktopId, int minutes)
    {
        lock (_tracker) _tracker.Snooze(desktopId, DateTime.Now, TimeSpan.FromMinutes(minutes));
    }

    /// <summary>
    /// Connect: starts this laptop's virtual monitors for the desktop's displays, then asks the desktop to start its windows.
    /// Returns null when both worked, or the reason it did not.
    /// </summary>
    public async Task<string?> ConnectAsync(string desktopId, CancellationToken ct = default)
    {
        var d = HostIdentity.Desktops().FirstOrDefault(x => string.Equals(x.UltraId, desktopId, StringComparison.OrdinalIgnoreCase));
        if (d == null) return "this desktop is no longer paired with the laptop";
        if (!_host.Running) return "the host is not running";
        // one virtual display for each connected screen of the desktop, in the desktop's order (left to right)
        // only the screens this desktop's choice extends (the choice is kept with the desktop): the others get no display
        var reported = HostIdentity.ScreensOf(d.Name) ?? new List<MonitorOffer>();
        if (reported.Count == 0) return "the desktop has not reported its screens yet: connect it from the desktop first";
        var screens = ExtendedScreens(reported, d.Displays).OrderBy(s => s.X).ThenBy(s => s.Y).ToList();
        if (screens.Count == 0) return "no screen of this desktop is extended to the laptop: turn one on in the Desktop PCs section";
        if (screens.Count > HostPlanning.MaxDisplays) return $"the desktop has {screens.Count} screens; the laptop serves up to {HostPlanning.MaxDisplays}";
        var offers = DisplaysFor(screens).Select(x => new StreamOffer(x.Label, x.Screen.Width, x.Screen.Height, 60, x.Screen.Id)).ToList();
        var failed = await _host.EnsureDisplaysAsync(offers, ct, _host.CopyScreenFor(d, screens));
        if (failed.Count > 0) return $"could not start {string.Join(", ", failed)} on this laptop (see its log)";
        _host.RequestStart(desktopId);
        Log.Info($"connect: started this laptop's displays for '{d.Name}' and asked it to start its windows");
        return null;
    }

    private static IReadOnlyList<NoticeScreen> ScreensFor(string desktopName) =>
        (HostIdentity.ScreensOf(desktopName) ?? new List<MonitorOffer>())
            .Select(m => new NoticeScreen(m.Id, m.X, m.Y, m.Width, m.Height, m.Primary)).ToList();

    public void Dispose() => Stop();
}
