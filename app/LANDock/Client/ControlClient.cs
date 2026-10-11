using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace LANDock.Client;

public sealed record RemoteIdentity(string HostId, string HostName, string Version, string Fingerprint, bool PairingOpen, int ControlPort);
public sealed record RemotePairing(string Token, string HostId, string HostName, List<RemoteStream> Streams);
public sealed record RemoteStream(string Name, int Port, int Width, int Height, int Fps);
public sealed record DisplayOffer(string Name, int Width, int Height, int Fps, string? ScreenId = null);

/// <summary>A failure the user should read: wrong PIN, pairing closed, certificate changed, host not reachable...</summary>
public sealed class ControlException : Exception
{
    public int Status { get; }
    public ControlException(string message, int status = 0) : base(message) => Status = status;
}

/// <summary>Talks to the host's control port (see host ControlServer). Every connection checks the host's certificate
/// against the fingerprint this PC saved when it paired; with no saved fingerprint (first contact) any certificate is
/// accepted and its fingerprint is reported, so the user can compare it with the laptop before pairing.</summary>
public sealed class ControlClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _pinned;
    private readonly string _where;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The fingerprint of the certificate the host showed in the last connection.</summary>
    public string? SeenFingerprint { get; private set; }

    public ControlClient(string address, int port, string? pinnedFingerprint)
    {
        _pinned = string.IsNullOrWhiteSpace(pinnedFingerprint) ? null : pinnedFingerprint.Trim();
        _where = $"{address}:{port}";
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                SeenFingerprint = cert?.GetCertHashString(HashAlgorithmName.SHA256);
                return _pinned == null || string.Equals(SeenFingerprint, _pinned, StringComparison.OrdinalIgnoreCase);
            },
        };
        var host = address.Contains(':') && !address.StartsWith('[') ? $"[{address}]" : address;
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://{host}:{port}"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public Task<RemoteIdentity> IdentityAsync(CancellationToken ct = default) =>
        SendAsync<RemoteIdentity>(HttpMethod.Get, "/api/identity", null, null, ct);

    public Task<RemotePairing> PairAsync(string pin, string desktopName, string ultraId, CancellationToken ct = default) =>
        SendAsync<RemotePairing>(HttpMethod.Post, "/api/pair", new { pin, client = desktopName, ultraId }, null, ct);

    /// <summary>Asks the host for the displays at the given sizes. An empty list just returns the streams.</summary>
    /// <param name="monitors">The screens this PC uses (plan-monitor-layout.md). Null leaves the report out, as an older PC does.</param>
    public Task<List<RemoteStream>> SessionAsync(string token, IEnumerable<DisplayOffer> offers, string ultraId,
        IEnumerable<LANDock.Host.MonitorOffer>? monitors = null, CancellationToken ct = default) =>
        SendAsync<List<RemoteStream>>(HttpMethod.Post, "/api/session",
            new { streams = offers.ToList(), ultraId, monitors = monitors?.ToList() }, token, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = JsonContent.Create(body);
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (_pinned != null && SeenFingerprint != null && !string.Equals(SeenFingerprint, _pinned, StringComparison.OrdinalIgnoreCase))
                throw new ControlException($"the certificate of {_where} changed (it shows {SeenFingerprint}, this PC paired with {_pinned}). Not connecting. " +
                                           "If the laptop was reinstalled, remove the host here and pair it again.");
            throw new ControlException($"could not reach the host at {_where} (is the host running on the laptop, and is its firewall open for private networks?) [{ex.GetType().Name}: {ex.Message}]");
        }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var msg = "";
                try { msg = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString() ?? ""; } catch { }
                throw new ControlException(string.IsNullOrEmpty(msg) ? $"the host answered {(int)resp.StatusCode}" : msg, (int)resp.StatusCode);
            }
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw new ControlException("the host sent an empty answer");
        }
    }

    public void Dispose() => _http.Dispose();
}
