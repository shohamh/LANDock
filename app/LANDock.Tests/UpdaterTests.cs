using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using LANDock.Client;
using LANDock.Core;
using LANDock.Host;
using LANDock.Update;

namespace LANDock.Tests;

/// <summary>The updater end to end against a fake GitHub: release lookup, ETag, checksum, and swapping a component in place.
/// These tests change the global install/data folders, so they must not run in parallel with each other.</summary>
[Collection("paths")]
public class UpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "landock-upd-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldInstall = Paths.InstallDir, _oldData = Paths.DataDir;

    public UpdaterTests()
    {
        Directory.CreateDirectory(_root);
        Paths.InstallDir = Path.Combine(_root, "install"); Paths.DataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(Paths.InstallDir); Directory.CreateDirectory(Paths.DataDir);
    }

    public void Dispose()
    {
        Paths.InstallDir = _oldInstall; Paths.DataDir = _oldData;
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Assets = new();
        public string ReleaseJson = "";
        public int ReleaseCalls, NotModified;
        public bool RequireToken;
        public string? SeenAuthorization;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            SeenAuthorization = req.Headers.Authorization?.ToString();
            if (RequireToken && req.Headers.Authorization == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (url.EndsWith("/releases/latest"))
            {
                ReleaseCalls++;
                if (req.Headers.TryGetValues("If-None-Match", out var inm) && inm.Contains("\"v1\"")) { NotModified++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson, Encoding.UTF8, "application/json") };
                r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(r);
            }
            if (url.StartsWith("https://api.github.com/assets/") && Assets.TryGetValue(url[(url.LastIndexOf('/') + 1)..], out var data))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Zip(params (string entry, string text)[] files)
    {
        using var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (e, t) in files) { using var w = new StreamWriter(a.CreateEntry(e).Open()); w.Write(t); }
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    private FakeGitHub Release(string tag, byte[] hostZip, string hostVersion, string? shaOverride = null)
    {
        var manifest = new UpdateManifest
        {
            Version = tag.TrimStart('v'), Tag = tag, Notes = "notes",
            Components = { ["host"] = new ComponentInfo { Version = hostVersion, File = "host.zip", Sha256 = shaOverride ?? Sha(hostZip), Size = hostZip.Length } },
        };
        var gh = new FakeGitHub();
        gh.Assets["1"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));
        gh.Assets["2"] = hostZip;
        gh.ReleaseJson = JsonSerializer.Serialize(new
        {
            tag_name = tag, html_url = "https://example/release", draft = false,
            assets = new[] { new { name = "manifest.json", url = "https://api.github.com/assets/1" }, new { name = "host.zip", url = "https://api.github.com/assets/2" } },
        });
        return gh;
    }

    private (Updater up, Settings s) NewUpdater(FakeGitHub gh)
    {
        var s = new Settings();
        s.Updates.Repository = "owner/repo";
        return (new Updater(s, new HostService(s), new ClientService(s), gh), s);
    }

    private static void InstallHost(string version)
    {
        Directory.CreateDirectory(Path.Combine(Paths.HostDir, "bin"));
        File.WriteAllText(Path.Combine(Paths.HostDir, "bin", "sunshine.exe"), "old binary");
        File.WriteAllText(Paths.HostVersionFile, version);
    }

    [Fact]
    public async Task A_newer_host_component_is_found_downloaded_verified_and_swapped_in()
    {
        InstallHost("old.1");
        var zip = Zip(("bin/sunshine.exe", "new binary"), ("VERSION.txt", "new.2"));
        var gh = Release("v9.9.9", zip, "new.2");
        var (up, _) = NewUpdater(gh);

        var check = await up.CheckAsync();
        Assert.NotNull(check);
        Assert.Contains(check!.Pending, p => p.Component == "host" && p.From == "old.1" && p.To == "new.2");

        Assert.True(await up.ApplyAsync(check));
        Assert.Equal("new binary", File.ReadAllText(Path.Combine(Paths.HostDir, "bin", "sunshine.exe")));
        Assert.Equal("new.2", Updater.InstalledHost);
        Assert.False(Directory.Exists(Paths.HostDir + ".old"));
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_rejected_and_nothing_changes()
    {
        InstallHost("old.1");
        var gh = Release("v9.9.9", Zip(("bin/sunshine.exe", "tampered"), ("VERSION.txt", "new.2")), "new.2", shaOverride: new string('0', 64));
        var (up, _) = NewUpdater(gh);
        var check = await up.CheckAsync();

        Assert.False(await up.ApplyAsync(check!));
        Assert.Equal("old binary", File.ReadAllText(Path.Combine(Paths.HostDir, "bin", "sunshine.exe")));
        Assert.Equal("old.1", Updater.InstalledHost);
        Assert.Contains("checksum", up.LastError);
    }

    [Fact]
    public async Task A_component_that_is_up_to_date_is_not_downloaded()
    {
        InstallHost("same.1");
        var gh = Release("v0.0.1", Zip(("VERSION.txt", "same.1")), "same.1");
        var (up, _) = NewUpdater(gh);
        var check = await up.CheckAsync();
        Assert.False(check!.Any);
    }

    [Fact]
    public async Task The_second_check_uses_the_etag_and_costs_no_download()
    {
        InstallHost("old.1");
        var gh = Release("v9.9.9", Zip(("VERSION.txt", "new.2")), "new.2");
        var (up, _) = NewUpdater(gh);
        await up.CheckAsync();
        var again = await up.CheckAsync();
        Assert.Equal(1, gh.NotModified);
        Assert.True(again!.Any);   // still known from the cached answer
    }

    [Fact]
    public async Task A_private_repository_needs_the_token_and_the_token_is_sent()
    {
        InstallHost("old.1");
        var gh = Release("v9.9.9", Zip(("VERSION.txt", "new.2")), "new.2");
        gh.RequireToken = true;
        var (up, s) = NewUpdater(gh);
        await up.CheckAsync();
        Assert.Contains("private", up.LastError);

        s.Updates.Token = "tok123";
        var check = await up.CheckAsync();
        Assert.True(check!.Any);
        Assert.Equal("Bearer tok123", gh.SeenAuthorization);
    }
}

[CollectionDefinition("paths", DisableParallelization = true)]
public class PathsCollection { }
