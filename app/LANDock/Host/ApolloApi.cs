using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LANDock.Core;

namespace LANDock.Host;

/// <summary>The web API of one Apollo instance (its port is the stream port + 1; it only answers on this computer).</summary>
public sealed class ApolloApi : IDisposable
{
    /// <summary>What a first-paired, fully trusted client has.</summary>
    public const long FullPermission = 119480064;
    /// <summary>All bits; the server masks it down to the valid ones.</summary>
    public const long AllBits = 4294967295;

    private readonly HttpClient _http;
    private bool _loggedIn;
    public int Port { get; }

    public ApolloApi(int streamPort)
    {
        Port = streamPort;
        // the instance uses a self-signed certificate; we only ever talk to localhost
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseProxy = false,
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{streamPort + 1}"), Timeout = TimeSpan.FromSeconds(8) };
    }

    public static (string user, string password)? Credentials()
    {
        try
        {
            var m = Regex.Match(File.ReadAllText(Paths.Credentials), @"user:\s*(\S+)\s+password:\s*(\S+)");
            return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
        }
        catch { return null; }
    }

    private async Task<JsonNode?> PostAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(path, body, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException();
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private async Task<JsonNode?> CallAsync(string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!_loggedIn)
                {
                    var c = Credentials() ?? throw new InvalidOperationException("no web API credentials (host not set up)");
                    await PostAsync("/api/login", new { username = c.user, password = c.password }, ct);
                    _loggedIn = true;
                }
                if (body != null) return await PostAsync(path, body, ct);
                using var resp = await _http.GetAsync(path, ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException();
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch (UnauthorizedAccessException) when (attempt == 0) { _loggedIn = false; }
        }
        return null;
    }

    /// <summary>Hands the PIN to a pairing request that is waiting on this instance (does nothing when none is).</summary>
    public Task SubmitPinAsync(string pin, string name, CancellationToken ct) =>
        CallAsync("/api/pin", new { pin, name }, ct);

    public async Task<List<(string uuid, string name, long perm)>> ListClientsAsync(CancellationToken ct)
    {
        var node = await CallAsync("/api/clients/list", null, ct);
        var list = new List<(string, string, long)>();
        if (node?["named_certs"] is JsonArray arr)
            foreach (var c in arr)
                list.Add((c?["uuid"]?.GetValue<string>() ?? "", c?["name"]?.GetValue<string>() ?? "", c?["perm"]?.GetValue<long>() ?? 0));
        return list;
    }

    public Task SetPermissionsAsync(string uuid, string name, CancellationToken ct) =>
        CallAsync("/api/clients/update", new { uuid, name, perm = AllBits }, ct);

    public Task UnpairAsync(string uuid, CancellationToken ct) => CallAsync("/api/clients/unpair", new { uuid }, ct);

    /// <summary>Ends the running stream (which removes its virtual display).</summary>
    public Task CloseAppAsync(CancellationToken ct) => CallAsync("/api/apps/close", new { }, ct);

    public void Dispose() => _http.Dispose();
}
