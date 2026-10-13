using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LANDock.Update;

public sealed class ComponentInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>manifest.json, published with every release: which version each part has, and which file (with checksum) carries it.</summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("components")] public Dictionary<string, ComponentInfo> Components { get; set; } = new();

    public static UpdateManifest Parse(string json) =>
        JsonSerializer.Deserialize<UpdateManifest>(json) ?? throw new InvalidDataException("empty manifest");
}

/// <summary>One part that has a newer version available.</summary>
public sealed record PendingUpdate(string Component, string From, string To, ComponentInfo Info);

public static class AppInfo
{
    /// <summary>The version of the running program (the release workflow stamps it into the build).</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}

public static class SemVer
{
    private static readonly Regex Pattern = new(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$", RegexOptions.Compiled);

    public static bool TryParse(string s, out (int major, int minor, int patch, string pre) v)
    {
        var m = Pattern.Match((s ?? "").Trim());
        v = default;
        if (!m.Success) return false;
        v = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Value);
        return true;
    }

    /// <summary>Negative when a is older than b. A pre-release (1.2.0-rc1) is older than its release (1.2.0).</summary>
    public static int Compare(string a, string b)
    {
        if (!TryParse(a, out var x) || !TryParse(b, out var y)) return string.CompareOrdinal(a, b);
        var c = x.major.CompareTo(y.major);
        if (c == 0) c = x.minor.CompareTo(y.minor);
        if (c == 0) c = x.patch.CompareTo(y.patch);
        if (c != 0) return c;
        if (x.pre == y.pre) return 0;
        if (x.pre == "") return 1;
        if (y.pre == "") return -1;
        // compare dot-separated identifiers: numbers numerically, text alphabetically, numbers below text
        var pa = x.pre.Split('.'); var pb = y.pre.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var an = int.TryParse(pa[i], out var ai); var bn = int.TryParse(pb[i], out var bi);
            var r = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (r != 0) return r;
        }
        return pa.Length.CompareTo(pb.Length);
    }
}

public static class UpdatePlanner
{
    /// <summary>
    /// What needs updating. Only ever to a higher version: the program updates when the release is newer than the installed one.
    /// The host and client parts have content ids (the pinned source commit), which have no order, so they are only offered
    /// together with a newer release, and only when their id differs; a part that is not installed on this computer is never
    /// added by an update, and an older release never brings a part back.
    /// </summary>
    public static List<PendingUpdate> Plan(UpdateManifest m, string installedApp, string installedHost, string installedClient)
    {
        var res = new List<PendingUpdate>();
        var releaseIsNewer = SemVer.Compare(m.Version, installedApp) > 0;
        if (!releaseIsNewer) return res;
        if (m.Components.TryGetValue("host", out var h) && installedHost != "" && !string.Equals(h.Version, installedHost, StringComparison.OrdinalIgnoreCase))
            res.Add(new PendingUpdate("host", installedHost, h.Version, h));
        if (m.Components.TryGetValue("client", out var c) && installedClient != "" && !string.Equals(c.Version, installedClient, StringComparison.OrdinalIgnoreCase))
            res.Add(new PendingUpdate("client", installedClient, c.Version, c));
        if (m.Components.TryGetValue("app", out var a) && SemVer.Compare(a.Version, installedApp) > 0)
            res.Add(new PendingUpdate("app", installedApp, a.Version, a));   // last: applying it restarts the program
        return res;
    }
}

public static class Files
{
    public static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Unpacks a zip, refusing any entry that would land outside the target folder ("zip slip").</summary>
    public static void ExtractSafe(string zip, string target)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
        using var archive = ZipFile.OpenRead(zip);
        foreach (var e in archive.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(target, e.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"unsafe path in archive: {e.FullName}");
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
        }
    }

    /// <summary>Replaces a folder by a new one in two renames, so a failure never leaves it half-updated.</summary>
    public static void SwapFolder(string current, string replacement)
    {
        var old = current + ".old";
        if (Directory.Exists(old)) Directory.Delete(old, true);
        if (Directory.Exists(current)) Directory.Move(current, old);
        try { Directory.Move(replacement, current); }
        catch
        {
            if (Directory.Exists(old) && !Directory.Exists(current)) Directory.Move(old, current);   // put it back
            throw;
        }
        try { Directory.Delete(old, true); } catch { /* removed on the next start */ }
    }
}
