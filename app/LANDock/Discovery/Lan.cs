using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LANDock.Core;
using Makaretu.Dns;

namespace LANDock.Discovery;

/// <summary>What a laptop tells the LAN about itself (mDNS / DNS-SD service "_landock._tcp").</summary>
public sealed record Advert(string HostId, string Name, string Version, int ControlPort, string Pairing, IReadOnlyList<(string id, double db, double ageSeconds)> Heard, IReadOnlyList<string>? Starts = null);

/// <summary>A laptop found on the LAN, as the desktop PC knows it.</summary>
public sealed class DiscoveredLaptop
{
    public string HostId { get; init; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Address { get; set; } = "";
    public int ControlPort { get; set; }
    public string Pairing { get; set; } = "";
    public DateTime LastSeen { get; set; }
    /// <summary>When the laptop last said it heard this PC's ultrasound id, and how loud it was (null: not heard).</summary>
    public DateTime? HeardUsAt { get; set; }
    public double HeardUsDb { get; set; }
    /// <summary>Desktops (their ultrasound ids) that this laptop asks to start their windows now (HostService.RequestStart).</summary>
    public List<string> StartFor { get; set; } = new();
}

/// <summary>
/// DNS-SD over mDNS (the Makaretu.Dns library, MIT): the laptop advertises "&lt;name&gt;._landock._tcp.local" with the control
/// port as the SRV port and its id, version, pairing state and what it heard as TXT entries.
/// </summary>
public static class LanNames
{
    public const string Service = "_landock._tcp";
    public static readonly DomainName ServiceName = new("_landock._tcp");   // the library adds ".local"

    /// <summary>The IPv4 addresses of this PC's network adapters (what the advert points at).</summary>
    public static List<IPAddress> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            // real network adapters only: virtual switches (Hyper-V, WSL), VPN tunnels and loopback are not reachable from a desktop
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                        && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        && !n.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address).Distinct().ToList();
}

/// <summary>The laptop side: keeps the advert on the LAN while the host runs, and refreshes what it heard.</summary>
public sealed class LanAdvertiser : IDisposable
{
    private ServiceDiscovery? _sd;
    private ServiceProfile? _profile;
    private string _lastTxt = "";

    public bool Running => _profile != null;

    public void Start(Advert advert)
    {
        Stop();
        _sd = new ServiceDiscovery();
        Publish(advert);
        Log.Info($"advertising this laptop on the LAN as '{advert.Name}' (control port {advert.ControlPort})");
    }

    /// <summary>Publishes a new advert when something changed (the heard list, the pairing state).</summary>
    public void Update(Advert advert)
    {
        if (_sd == null) return;
        var txt = Describe(advert);
        if (txt == _lastTxt) return;
        if (_profile != null) _sd.Unadvertise(_profile);
        Publish(advert);
    }

    private void Publish(Advert advert)
    {
        var instance = new DomainName(SafeInstance(advert.Name));
        _profile = new ServiceProfile(instance, LanNames.ServiceName, (ushort)advert.ControlPort, LanNames.LocalIPv4(), false);
        _profile.AddProperty("id", advert.HostId);
        _profile.AddProperty("name", advert.Name);
        _profile.AddProperty("ver", advert.Version);
        _profile.AddProperty("pairing", advert.Pairing);
        foreach (var h in advert.Heard.Take(8))
            _profile.AddProperty("heard", $"{h.id}:{h.db:0}:{h.ageSeconds:0}");
        foreach (var id in (advert.Starts ?? Array.Empty<string>()).Take(8))
            _profile.AddProperty("start", id);
        _sd!.Advertise(_profile);
        _sd.Announce(_profile, 3);
        _lastTxt = Describe(advert);
    }

    private static string Describe(Advert a) =>
        $"{a.HostId}|{a.Name}|{a.Version}|{a.ControlPort}|{a.Pairing}|" + string.Join(";", a.Heard.Take(8).Select(h => $"{h.id}:{h.db:0}:{h.ageSeconds:0}"))
        + "|" + string.Join(";", (a.Starts ?? Array.Empty<string>()).Take(8));

    /// <summary>A DNS label: letters, digits and dashes, at most 63 characters.</summary>
    private static string SafeInstance(string name)
    {
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-');
        if (clean.Length == 0) clean = "LANDock";
        return clean.Length > 60 ? clean[..60] : clean;
    }

    public void Stop()
    {
        try { if (_sd != null && _profile != null) _sd.Unadvertise(_profile); } catch { }
        _sd?.Dispose();
        _sd = null;
        _profile = null;
        _lastTxt = "";
    }

    public void Dispose() => Stop();
}

/// <summary>The desktop side: browses the LAN for laptops, and keeps the ones seen recently.</summary>
public sealed class LanBrowser : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredLaptop> _found = new(StringComparer.OrdinalIgnoreCase);
    private ServiceDiscovery? _sd;
    private CancellationTokenSource? _cts;
    private readonly string _ourHostId;

    /// <summary>How long a laptop stays in the list after it was last seen.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(60);

    /// <param name="ourUltrasoundId">This PC's ultrasound id: a laptop that hears it is a nearby laptop.</param>
    public LanBrowser(string ourUltrasoundId) => _ourHostId = ourUltrasoundId;

    public bool Running => _sd != null;
    /// <summary>Why the browser is not working (null when it is).</summary>
    public string? Problem { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        Stop();
        try { _sd = new ServiceDiscovery(); }
        catch (Exception ex) { Problem = "mDNS is not available: " + ex.Message; Log.Error("LAN browse", ex); return; }
        Problem = null;
        _sd.Mdns.AnswerReceived += (_, e) => { try { Read(e.Message); } catch { } };
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { _sd?.QueryServiceInstances(LanNames.ServiceName); } catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }
            }
        }, ct);
        Log.Info("browsing the LAN for laptops (mDNS _landock._tcp)");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _sd?.Dispose();
        _sd = null;
    }

    /// <summary>The laptops seen within Expiry, most recent first.</summary>
    public List<DiscoveredLaptop> Snapshot()
    {
        lock (_gate)
        {
            var cutoff = DateTime.Now - Expiry;
            foreach (var k in _found.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList()) _found.Remove(k);
            // "heard us" only counts while it is recent (a laptop reports each 3 s)
            foreach (var d in _found.Values)
                if (d.HeardUsAt is { } at && at < DateTime.Now - TimeSpan.FromSeconds(60)) d.HeardUsAt = null;
            return _found.Values.Select(Copy).OrderByDescending(d => d.LastSeen).ToList();
        }
    }

    private static DiscoveredLaptop Copy(DiscoveredLaptop d) => new()
    {
        HostId = d.HostId, Name = d.Name, Version = d.Version, Address = d.Address, ControlPort = d.ControlPort,
        Pairing = d.Pairing, LastSeen = d.LastSeen, HeardUsAt = d.HeardUsAt, HeardUsDb = d.HeardUsDb, StartFor = new List<string>(d.StartFor),
    };

    private void Read(Makaretu.Dns.Message msg)
    {
        var records = msg.Answers.Concat(msg.AdditionalRecords).ToList();
        var srv = records.OfType<SRVRecord>().FirstOrDefault(r => r.Name.ToString().Contains("_landock._tcp.local", StringComparison.OrdinalIgnoreCase));
        if (srv == null) return;
        var txt = records.OfType<TXTRecord>().FirstOrDefault(r => r.Name.Equals(srv.Name))?.Strings ?? new List<string>();
        var props = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in txt)
        {
            var eq = s.IndexOf('=');
            if (eq <= 0) continue;
            var key = s[..eq];
            if (!props.TryGetValue(key, out var list)) props[key] = list = new List<string>();
            list.Add(s[(eq + 1)..]);
        }
        var id = props.TryGetValue("id", out var ids) ? ids.FirstOrDefault() ?? "" : "";
        if (id == "") return;
        var address = records.OfType<ARecord>().FirstOrDefault(a => a.Name.Equals(srv.Target))?.Address.ToString() ?? "";
        if (address == "") return;
        var instance = srv.Name.Labels.FirstOrDefault() ?? "";

        lock (_gate)
        {
            if (!_found.TryGetValue(id, out var d)) _found[id] = d = new DiscoveredLaptop { HostId = id };
            d.Name = props.TryGetValue("name", out var n) ? n.FirstOrDefault() ?? instance : instance;
            d.Version = props.TryGetValue("ver", out var v) ? v.FirstOrDefault() ?? "" : "";
            d.Pairing = props.TryGetValue("pairing", out var p) ? p.FirstOrDefault() ?? "" : "";
            d.Address = address;
            d.ControlPort = srv.Port;
            d.LastSeen = DateTime.Now;
            d.StartFor = props.TryGetValue("start", out var starts) ? starts.Where(x => x != "").ToList() : new List<string>();
            // what the laptop heard, as id:db:age entries; our own id tells us how close this laptop hears us
            d.HeardUsAt = null;
            if (props.TryGetValue("heard", out var heard))
                foreach (var h in heard)
                {
                    var parts = h.Split(':');
                    if (parts.Length != 3 || !parts[0].Equals(_ourHostId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db)) continue;
                    if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var age)) continue;
                    d.HeardUsAt = DateTime.Now.AddSeconds(-age);
                    d.HeardUsDb = db;
                }
        }
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
