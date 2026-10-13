using LANDock.Core;
using Makaretu.Dns;

namespace LANDock.Discovery;

/// <summary>
/// The desktop's presence on the LAN: a DNS-SD service "_landock-desk._tcp" with its ultrasound id (the laptop knows the desktop by
/// that id) and its name. The laptop browses for it, so a paired desktop that is on the network is recognized without sound.
/// </summary>
public static class PresenceNames
{
    public const string Service = "_landock-desk._tcp";
    public static readonly DomainName ServiceName = new("_landock-desk._tcp");
    /// <summary>A desktop that has not answered for this long is no longer on the network.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(60);
}

/// <summary>The desktop side: keeps the presence advert on the LAN while the client runs with LAN discovery on.</summary>
public sealed class PresenceAdvertiser : IDisposable
{
    private ServiceDiscovery? _sd;
    private ServiceProfile? _profile;

    public bool Running => _profile != null;

    public void Start(string id, string name)
    {
        Stop();
        if (id == "") return;
        try
        {
            _sd = new ServiceDiscovery();
            var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-');
            if (clean.Length == 0) clean = "LANDock-desktop";
            _profile = new ServiceProfile(new DomainName(clean.Length > 60 ? clean[..60] : clean), PresenceNames.ServiceName, 1, LanNames.LocalIPv4(), false);
            _profile.AddProperty("id", id);
            _profile.AddProperty("name", name);
            _sd.Advertise(_profile);
            _sd.Announce(_profile, 3);
            Log.Info("showing this desktop on the LAN to paired laptops");
        }
        catch (Exception ex)
        {
            Log.Error("presence advert", ex);
            Stop();
        }
    }

    public void Stop()
    {
        try { if (_sd != null && _profile != null) _sd.Unadvertise(_profile); } catch { }
        _sd?.Dispose();
        _sd = null;
        _profile = null;
    }

    public void Dispose() => Stop();
}

/// <summary>The laptop side: browses the LAN for desktops that show their presence, and keeps the ones seen recently.</summary>
public sealed class PresenceBrowser : IDisposable
{
    public sealed record Seen(string Id, string Name, string Address, DateTime At);

    private readonly object _gate = new();
    private readonly Dictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);
    private ServiceDiscovery? _sd;
    private CancellationTokenSource? _cts;

    public bool Running => _sd != null;

    public void Start()
    {
        Stop();
        try { _sd = new ServiceDiscovery(); }
        catch (Exception ex) { Log.Error("desktop presence browse", ex); return; }
        _sd.Mdns.AnswerReceived += (_, e) => { try { Read(e.Message); } catch { } };
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { _sd?.QueryServiceInstances(PresenceNames.Service); } catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }
            }
        }, ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _sd?.Dispose();
        _sd = null;
    }

    /// <summary>True when a desktop with this id was seen within 'within'.</summary>
    public bool SeenWithin(string id, TimeSpan within)
    {
        lock (_gate)
            return _seen.TryGetValue(id, out var s) && DateTime.Now - s.At <= within;
    }

    /// <summary>The desktops seen within the expiry time.</summary>
    public List<Seen> Snapshot()
    {
        lock (_gate)
        {
            var cutoff = DateTime.Now - PresenceNames.Expiry;
            foreach (var k in _seen.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList()) _seen.Remove(k);
            return _seen.Values.ToList();
        }
    }

    private void Read(Makaretu.Dns.Message msg)
    {
        var records = msg.Answers.Concat(msg.AdditionalRecords).ToList();
        var srv = records.OfType<SRVRecord>().FirstOrDefault(r => r.Name.ToString().Contains("_landock-desk._tcp", StringComparison.OrdinalIgnoreCase));
        if (srv == null) return;
        var txt = records.OfType<TXTRecord>().FirstOrDefault(r => r.Name.Equals(srv.Name))?.Strings ?? new List<string>();
        string id = "", name = "";
        foreach (var s in txt)
        {
            var eq = s.IndexOf('=');
            if (eq <= 0) continue;
            var key = s[..eq];
            if (key.Equals("id", StringComparison.OrdinalIgnoreCase)) id = s[(eq + 1)..];
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = s[(eq + 1)..];
        }
        if (id == "") return;
        var address = records.OfType<ARecord>().FirstOrDefault(a => a.Name.Equals(srv.Target))?.Address.ToString() ?? "";
        bool first;
        lock (_gate)
        {
            first = !_seen.ContainsKey(id);
            _seen[id] = new Seen(id, name, address, DateTime.Now);
        }
        if (first) Log.Info($"a desktop shows its presence on the LAN: {name} ({id}) at {address}");
    }

    public void Dispose() => Stop();
}
