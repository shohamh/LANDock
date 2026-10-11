using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using LANDock.Client;
using LANDock.Core;
using LANDock.Host;
using Microsoft.Win32;

namespace LANDock.Update;

/// <summary>The result of one check: the release, and what is newer than what is installed.</summary>
public sealed record UpdateCheck(UpdateManifest Manifest, List<PendingUpdate> Pending, string ReleaseUrl)
{
    public bool Any => Pending.Count > 0;
    public string Summary => string.Join(", ", Pending.Select(p => p.Component == "app" ? $"LANDock {p.To}" : $"{p.Component} component"));
}

/// <summary>
/// Looks at the project's GitHub releases every few minutes. Each release carries manifest.json (a version and checksum per
/// part), so only the parts that changed are downloaded. "ask" mode raises UpdateAvailable and waits; "auto" mode installs by
/// itself, but only while nothing is streaming, because host and client updates restart those parts.
/// </summary>
public sealed class Updater
{
    private readonly Settings _settings;
    private readonly HostService _host;
    private readonly ClientService _client;
    private readonly HttpClient _http;
    private string _etag = "", _cachedRelease = "";
    private string _notified = "";
    private JsonArray _assets = new();
    private CancellationTokenSource? _loop;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public UpdateCheck? Latest { get; private set; }
    public DateTime LastChecked { get; private set; } = DateTime.MinValue;
    public string LastError { get; private set; } = "";
    public bool Applying { get; private set; }
    public event Action<UpdateCheck>? UpdateAvailable;
    public event Action? Changed;

    public Updater(Settings settings, HostService host, ClientService client, HttpMessageHandler? handler = null)
    {
        _settings = settings; _host = host; _client = client;
        // redirects are followed by hand: the release asset download redirects to another site that must NOT receive our token
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LANDock/{AppInfo.Version}");
    }

    // ---- periodic loop ------------------------------------------------------------------------------------------------

    public void Start()
    {
        Stop();
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
            while (!ct.IsCancellationRequested)
            {
                if (_settings.Updates.Enabled) await TickAsync(ct);
                try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _settings.Updates.IntervalMinutes)), ct); } catch { return; }
            }
        }, ct);
    }

    public void Stop() { _loop?.Cancel(); _loop = null; }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            var check = await CheckAsync(ct);
            if (check is not { Any: true }) return;
            if (_settings.Updates.Mode == "auto")
            {
                if (IsBusyStreaming()) { Log.Info("update is ready, waiting until no stream is active"); return; }
                await ApplyAsync(check, ct);
            }
            else if (_notified != check.Manifest.Tag && _settings.Updates.SkippedVersion != check.Manifest.Tag)
            {
                _notified = check.Manifest.Tag;
                UpdateAvailable?.Invoke(check);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("update check", ex); }
    }

    private bool IsBusyStreaming() => (_host.Running && !_host.IsIdle()) || _client.AnyRunning();

    // ---- check --------------------------------------------------------------------------------------------------------

    public static string InstalledHost => Paths.ReadVersion(Paths.HostVersionFile);
    public static string InstalledClient => Paths.ReadVersion(Paths.ClientVersionFile);

    private HttpRequestMessage Request(string url, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = _settings.Updates.Token;
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>Sends a request and follows redirects; the token only goes to the first (GitHub) address.</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, string accept, string? etag, CancellationToken ct)
    {
        var req = Request(url, accept);
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        for (var hop = 0; hop < 5 && resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Moved or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther; hop++)
        {
            var next = resp.Headers.Location ?? throw new InvalidOperationException("redirect without a location");
            resp.Dispose();
            resp = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, next), HttpCompletionOption.ResponseHeadersRead, ct);
        }
        return resp;
    }

    /// <summary>Asks GitHub for the newest release and compares it with what is installed. Safe to call at any time.</summary>
    public async Task<UpdateCheck?> CheckAsync(CancellationToken ct = default)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var repo = _settings.Updates.Repository.Trim();
            var url = _settings.Updates.IncludePrereleases
                ? $"https://api.github.com/repos/{repo}/releases?per_page=5"
                : $"https://api.github.com/repos/{repo}/releases/latest";
            using var resp = await SendAsync(url, "application/vnd.github+json", _etag, ct);
            LastChecked = DateTime.Now;
            string body;
            if (resp.StatusCode == HttpStatusCode.NotModified) body = _cachedRelease;
            else if (resp.IsSuccessStatusCode)
            {
                body = await resp.Content.ReadAsStringAsync(ct);
                _etag = resp.Headers.ETag?.ToString() ?? ""; _cachedRelease = body;
            }
            else
            {
                LastError = resp.StatusCode == HttpStatusCode.NotFound
                    ? "no release found (is the repository private? then add an access token in the Updates tab)"
                    : $"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}";
                Log.Info("update check: " + LastError);
                Changed?.Invoke();
                return Latest;
            }
            LastError = "";

            var node = JsonNode.Parse(body);
            JsonNode? release = node is JsonArray arr ? arr.FirstOrDefault(r => r?["draft"]?.GetValue<bool>() != true) : node;
            if (release == null) { Latest = null; Changed?.Invoke(); return null; }
            _assets = release["assets"] as JsonArray ?? new JsonArray();
            var manifestAsset = _assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == "manifest.json");
            if (manifestAsset == null) { LastError = "the newest release has no manifest.json"; Changed?.Invoke(); return Latest; }

            using var mresp = await SendAsync(manifestAsset["url"]!.GetValue<string>(), "application/octet-stream", null, ct);
            mresp.EnsureSuccessStatusCode();
            var manifest = UpdateManifest.Parse(await mresp.Content.ReadAsStringAsync(ct));
            manifest.Tag = release["tag_name"]?.GetValue<string>() ?? manifest.Tag;
            var pending = UpdatePlanner.Plan(manifest, AppInfo.Version, InstalledHost, InstalledClient);
            Latest = new UpdateCheck(manifest, pending, release["html_url"]?.GetValue<string>() ?? "");
            if (pending.Count > 0) Log.Info($"update available ({manifest.Tag}): {Latest.Summary}");
            Changed?.Invoke();
            return Latest;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message; LastChecked = DateTime.Now;
            Log.Error("update check failed", ex);
            Changed?.Invoke();
            return Latest;
        }
        finally { _busy.Release(); }
    }

    // ---- apply --------------------------------------------------------------------------------------------------------

    private async Task<string> DownloadAsync(PendingUpdate u, IProgress<string>? progress, CancellationToken ct)
    {
        var asset = _assets.FirstOrDefault(x => x?["name"]?.GetValue<string>() == u.Info.File) ?? throw new FileNotFoundException($"{u.Info.File} is not part of the release");
        Directory.CreateDirectory(Paths.UpdatesDir);
        var target = Path.Combine(Paths.UpdatesDir, u.Info.File);
        if (File.Exists(target) && Files.Sha256Of(target) == u.Info.Sha256.ToLowerInvariant()) return target;   // downloaded before
        progress?.Report($"downloading {u.Info.File} ({u.Info.Size / 1024 / 1024} MB)");
        using var resp = await SendAsync(asset["url"]!.GetValue<string>(), "application/octet-stream", null, ct);
        resp.EnsureSuccessStatusCode();
        var part = target + ".part";
        await using (var fs = File.Create(part))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await src.CopyToAsync(fs, ct);
        var got = Files.Sha256Of(part);
        if (!string.Equals(got, u.Info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException($"{u.Info.File} failed its checksum (expected {u.Info.Sha256}, got {got}); not installed");
        }
        File.Move(part, target, true);
        return target;
    }

    /// <summary>Downloads and installs the pending parts: host, client, then the program itself (which restarts it).</summary>
    public async Task<bool> ApplyAsync(UpdateCheck check, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        if (Applying) return false;
        Applying = true; Changed?.Invoke();
        void Say(string s) { Log.Info(s); progress?.Report(s); }
        try
        {
            var wasHostRunning = _host.Running;
            foreach (var u in check.Pending.OrderBy(p => p.Component == "app" ? 1 : 0))
            {
                Say($"updating {u.Component}: {u.From} -> {u.To}");
                var zip = await DownloadAsync(u, progress, ct);
                switch (u.Component)
                {
                    case "host":
                        await ReplaceComponentAsync(zip, Paths.HostDir, async () => { if (_host.Running) await _host.StopAsync(); });
                        if (wasHostRunning) { Say("restarting the host"); await _host.StartAsync(); }
                        break;
                    case "client":
                        var streaming = _settings.Client.Hosts.Where(_client.IsRunning).ToList();
                        await ReplaceComponentAsync(zip, Paths.ClientDir, async () => { foreach (var h in streaming) await _client.StopAsync(h); });
                        if (streaming.Count > 0) Say("restarting the streams");
                        foreach (var h in streaming) await _client.StartAsync(h);
                        break;
                    case "app":
                        await ReplaceProgramAsync(zip, u.To, wasHostRunning, Say);
                        return true;   // the process is about to exit
                }
                Say($"{u.Component} is now {u.To}");
            }
            _notified = "";
            Latest = new UpdateCheck(check.Manifest, new(), check.ReleaseUrl);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("update failed", ex);
            LastError = "update failed: " + ex.Message;
            return false;
        }
        finally { Applying = false; Changed?.Invoke(); }
    }

    private static async Task ReplaceComponentAsync(string zip, string folder, Func<Task> stopFirst)
    {
        var staging = folder + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Files.ExtractSafe(zip, staging);               // unpack first: if this fails nothing has changed
        await stopFirst();
        await Task.Delay(500);
        Files.SwapFolder(folder, staging);
    }

    /// <summary>A running program can be renamed on Windows: move it aside, put the new one in its place, start it, exit.</summary>
    private async Task ReplaceProgramAsync(string zip, string version, bool resumeHost, Action<string> say)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot find the running program");
        var staging = Path.Combine(Paths.UpdatesDir, "app-new");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Files.ExtractSafe(zip, staging);
        var fresh = Path.Combine(staging, Path.GetFileName(exe));
        if (!File.Exists(fresh)) throw new FileNotFoundException("the update archive has no " + Path.GetFileName(exe));

        if (_host.Running) await _host.StopAsync();
        if (_client.AnyRunning())
        {
            File.WriteAllText(Path.Combine(Paths.DataDir, "resume-streams"), "");
            foreach (var h in _settings.Client.Hosts.Where(_client.IsRunning)) await _client.StopAsync(h);
        }
        if (resumeHost) File.WriteAllText(Path.Combine(Paths.DataDir, "resume-host"), "");

        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Copy(fresh, exe); }
        catch { File.Move(old, exe); throw; }
        TryWriteUninstallVersion(version);
        say("restarting LANDock");
        Process.Start(new ProcessStartInfo(exe, "--tray --after-update") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        await Task.Delay(300);
        Environment.Exit(0);
    }

    /// <summary>Keeps the version shown in "Apps &amp; features" in step with the program (the installer wrote it first).</summary>
    private static void TryWriteUninstallVersion(string version)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Installer.AppId + "_is1", true);
            key?.SetValue("DisplayVersion", version);
        }
        catch { }
    }

    /// <summary>Removes what an earlier update left behind (the renamed old program, old folders, downloaded files).</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
            foreach (var d in new[] { Paths.HostDir, Paths.ClientDir })
                foreach (var suffix in new[] { ".old", ".new" })
                    if (Directory.Exists(d + suffix)) Directory.Delete(d + suffix, true);
            if (Directory.Exists(Paths.UpdatesDir)) Directory.Delete(Paths.UpdatesDir, true);
        }
        catch { /* try again next start */ }
    }
}

public static class Installer
{
    /// <summary>Must match AppId in installer/LANDock.iss.</summary>
    public const string AppId = "{7C1D5A52-3E0B-4F51-9A63-1A4D0C0C0001}";
}
