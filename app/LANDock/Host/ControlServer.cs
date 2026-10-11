using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LANDock.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace LANDock.Host;

/// <summary>What a desktop PC learns about the host before it pairs: who it is, and which certificate it shows.</summary>
public sealed record ControlIdentity(string HostId, string HostName, string Version, string Fingerprint, bool PairingOpen, int ControlPort);

public sealed record PairRequest(string? Pin, string? Client, string? UltraId = null);
/// <summary>One display asked for: its name, the size and frame rate, and the desktop screen (<see cref="MonitorOffer.Id"/>) it is
/// shown on. ScreenId is null for an older desktop.</summary>
public sealed record StreamOffer(string Name, int Width, int Height, int Fps, string? ScreenId = null);
/// <summary>A screen of the desktop PC as the desktop reports it (plan-monitor-layout.md, step 1): the laptop mirrors the screens
/// the user extends to. Rotation is in degrees (0, 90, 180 or 270), as Windows reports it; X and Y are the desktop's layout.</summary>
public sealed record MonitorOffer(string Id, int X, int Y, int Width, int Height, int Rotation, bool Primary);
/// <summary>Monitors is null when the desktop is older than the screen report: the laptop then keeps the fixed displays.</summary>
public sealed record SessionRequest(List<StreamOffer>? Streams, string? UltraId = null, List<MonitorOffer>? Monitors = null);
public sealed record StreamAgreement(string Name, int Port, int Width, int Height, int Fps);

/// <summary>
/// The control port: the handshake between the laptop and a desktop PC. It is separate from the Apollo streams.
/// Pair:    POST /api/pair with the PIN shown on the laptop -> a token for this desktop (kept on the desktop, encrypted).
/// Session: POST /api/session with the token and the monitor sizes the desktop wants -> the sizes the host agrees to,
///          with the stream port for each. Sent with an empty list it just returns the stream list.
/// Identity: GET /api/identity, no login: host id, name and certificate fingerprint, so the desktop can show what it is
///          about to trust.
/// HTTPS with a certificate that is made once per laptop; the desktop checks the fingerprint at every connection.
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    public const int FirstPort = 49900, LastPort = 49999;
    public const int MaxWidth = 3840, MaxHeight = 2160;

    private readonly HostService _host;
    private WebApplication? _app;

    public ControlServer(HostService host) => _host = host;

    public int Port { get; private set; }

    /// <summary>The configured port, or the first free one from 49900 (the choice is returned so it can be saved).</summary>
    public static int ChoosePort(int configured)
    {
        if (configured > 0 && IsFree(configured)) return configured;
        if (configured > 0) Log.Info($"control port {configured} is in use; choosing another one");
        for (var p = FirstPort; p <= LastPort; p++) if (IsFree(p)) return p;
        throw new InvalidOperationException($"no free control port between {FirstPort} and {LastPort}");
    }

    public static bool IsFree(int port)
    {
        try
        {
            var busy = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
            if (busy) return false;
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    public async Task StartAsync(int port, CancellationToken ct = default)
    {
        Port = port;
        var cert = HostIdentity.Certificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Paths.HostState, Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port, lo => lo.UseHttps(cert)));
        var app = builder.Build();

        app.MapGet("/api/identity", () => Results.Json(_host.Identity()));

        app.MapPost("/api/pair", (PairRequest r) =>
        {
            var (ok, token, reason) = _host.Pair(r.Pin, r.Client, r.UltraId);
            if (!ok) return Results.Json(new { error = reason }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Json(new { token, hostId = HostIdentity.HostId(), hostName = Environment.MachineName, streams = _host.StreamList() });
        });

        app.MapPost("/api/session", async (HttpContext ctx, SessionRequest r) =>
        {
            var token = BearerToken(ctx);
            if (!_host.IsPaired(token)) return Results.Json(new { error = "this desktop is not paired with the host any more" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!string.IsNullOrEmpty(r.UltraId)) HostIdentity.SetUltraId(token, r.UltraId);
            _host.ReportMonitors(token, r.Monitors);
            // the displays a desktop asks for that the laptop does not run yet are started first (plan-monitor-layout.md, step 2)
            var failed = await _host.EnsureDisplaysAsync(r.Streams ?? new(), ctx.RequestAborted);
            if (failed.Count > 0) return Results.Json(new { error = $"could not start {string.Join(", ", failed)} on the laptop (see its log)" }, statusCode: StatusCodes.Status500InternalServerError);
            var (ok, agreed, reason) = _host.Agree(r.Streams ?? new(), token);
            if (!ok) return Results.Json(new { error = reason }, statusCode: StatusCodes.Status400BadRequest);
            return Results.Json(agreed);
        });

        await app.StartAsync(ct);
        _app = app;
        Log.Info($"control port {port} open (HTTPS, certificate {HostIdentity.Fingerprint()})");
    }

    private static string BearerToken(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : "";
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app == null) return;
        try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await app.StopAsync(stop.Token); } catch { }
        await app.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>Who this laptop is: a host id that never changes, a certificate made once, and the desktops it has paired with.</summary>
public static class HostIdentity
{
    private static X509Certificate2? _cert;
    private static readonly object Gate = new();

    public static string HostId()
    {
        Directory.CreateDirectory(Paths.HostState);
        var file = Path.Combine(Paths.HostState, "host-id.txt");
        if (File.Exists(file)) return File.ReadAllText(file).Trim();
        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(file, id);
        return id;
    }

    /// <summary>The certificate the control port uses. Made once; its fingerprint is what desktops pin.</summary>
    public static X509Certificate2 Certificate()
    {
        lock (Gate)
        {
            if (_cert != null) return _cert;
            Directory.CreateDirectory(Paths.HostState);
            var pfx = Path.Combine(Paths.HostState, "control.pfx");
            var pwFile = Path.Combine(Paths.HostState, "control-pfx.txt");
            if (File.Exists(pfx) && File.Exists(pwFile))
            {
                _cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, File.ReadAllText(pwFile).Trim(), X509KeyStorageFlags.Exportable);
                return _cert;
            }
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN=LANDock {Environment.MachineName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            File.WriteAllBytes(pfx, made.Export(X509ContentType.Pfx, password));
            File.WriteAllText(pwFile, password);
            _cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, password, X509KeyStorageFlags.Exportable);
            Log.Info("made the control certificate for this laptop");
            return _cert;
        }
    }

    /// <summary>SHA-256 of the certificate, upper-case hex: the form desktops store and compare.</summary>
    public static string Fingerprint() => Certificate().GetCertHashString(HashAlgorithmName.SHA256);

    // ---- paired desktops ----------------------------------------------------------------------------------------------

    /// <summary>A paired desktop: its name, the hash of its token, when it paired, its ultrasound id, and the choices for its displays
    /// (null = the defaults: every display on, up to 4K at 60 frames per second).</summary>
    public sealed record PairedDesktop(string Name, string TokenHash, string Created, string UltraId = "", List<DisplayPolicy>? Displays = null);

    private static string ClientsFile => Path.Combine(Paths.HostState, "paired-desktops.json");

    public static List<PairedDesktop> Desktops()
    {
        try { return JsonSerializer.Deserialize<List<PairedDesktop>>(File.ReadAllText(ClientsFile)) ?? new(); }
        catch { return new(); }
    }

    public static string Hash(string token) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>Makes a new token for a desktop. Only its hash is stored.</summary>
    public static string Issue(string name, string ultraId = "")
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var list = Desktops().Where(d => !d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(new PairedDesktop(name, Hash(token), DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ultraId));
        Directory.CreateDirectory(Paths.HostState);
        File.WriteAllText(ClientsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        return token;
    }

    /// <summary>Records the ultrasound id of the desktop that holds this token (a desktop sends it when it pairs and at each session).</summary>
    public static void SetUltraId(string token, string ultraId)
    {
        var h = Hash(token);
        var list = Desktops();
        var i = list.FindIndex(d => d.TokenHash == h);
        if (i < 0 || list[i].UltraId == ultraId) return;
        list[i] = list[i] with { UltraId = ultraId };
        SaveDesktops(list);
    }

    /// <summary>The display choices of a desktop, by its token (null = the defaults).</summary>
    public static List<DisplayPolicy>? DisplaysOfToken(string token)
    {
        var h = Hash(token);
        return Desktops().FirstOrDefault(d => d.TokenHash == h)?.Displays;
    }

    /// <summary>The display choices of a desktop, by its name (null = the defaults).</summary>
    public static List<DisplayPolicy>? DisplaysOf(string name) =>
        Desktops().FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Displays;

    /// <summary>Remembers the display choices of a desktop: they apply to its next connections.</summary>
    public static void SetDisplays(string name, List<DisplayPolicy> displays)
    {
        var list = Desktops();
        var i = list.FindIndex(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        list[i] = list[i] with { Displays = displays };
        SaveDesktops(list);
    }

    private static void SaveDesktops(List<PairedDesktop> list)
    {
        Directory.CreateDirectory(Paths.HostState);
        File.WriteAllText(ClientsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool IsKnown(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var h = Hash(token);
        return Desktops().Any(d => d.TokenHash == h);
    }

    /// <summary>Forgets a desktop: its token stops working at once.</summary>
    public static bool Forget(string name)
    {
        var list = Desktops();
        var kept = list.Where(d => !d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == list.Count) return false;
        File.WriteAllText(ClientsFile, JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }
}
