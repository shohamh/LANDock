using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LANDock.Core;

public sealed class StreamSettings
{
    public string Name { get; set; } = "";
    public int Port { get; set; }
    /// <summary>1-based, left to right on the desktop PC (see "monitors").</summary>
    public int Monitor { get; set; } = 1;
    public string Resolution { get; set; } = "1920x1080";
    public int Fps { get; set; } = 60;
    /// <summary>kbit/s; 0 lets Moonlight choose.</summary>
    public int Bitrate { get; set; }
    /// <summary>"client" plays the sound on this PC, "host" keeps it on the laptop.</summary>
    public string Audio { get; set; } = "client";
    /// <summary>Empty = the general display mode.</summary>
    public string DisplayMode { get; set; } = "";
    /// <summary>Where a windowed stream sits on its monitor: x, y, width, height (null = 80, 80, 1296, 759).</summary>
    public int[]? WindowedRect { get; set; }
}

public sealed class ClientSettings
{
    public string HostAddress { get; set; } = "";
    /// <summary>"fullscreen" or "windowed".</summary>
    public string DisplayMode { get; set; } = "fullscreen";
    public bool AutoStartStreams { get; set; }
    public List<StreamSettings> Streams { get; set; } = DefaultStreams();

    public static List<StreamSettings> DefaultStreams() => new()
    {
        new StreamSettings { Name = "M1", Port = 49989, Monitor = 1, Resolution = "1920x1080", Fps = 60 },
        new StreamSettings { Name = "M2", Port = 50989, Monitor = 2, Resolution = "1920x1080", Fps = 60, Audio = "host" },
        new StreamSettings { Name = "TV", Port = 51989, Monitor = 3, Resolution = "3840x2160", Fps = 30, Bitrate = 20000, Audio = "host" },
    };
}

public sealed class HostSettings
{
    /// <summary>Start the host automatically when LANDock starts (the laptop is then always ready).</summary>
    public bool AutoStart { get; set; }
    /// <summary>How long new desktop PCs may pair after the host starts or "New pairing PIN" is pressed.</summary>
    public int PairingWindowMinutes { get; set; } = 10;
    /// <summary>Keep the laptop awake and unlocked-looking while the host runs (input cannot be injected into a locked session).</summary>
    public bool KeepAwake { get; set; } = true;
}

public sealed class UpdateSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 10;
    /// <summary>"ask" shows a notice and waits; "auto" installs by itself (host and client updates wait until no stream is active).</summary>
    public string Mode { get; set; } = "ask";
    public bool IncludePrereleases { get; set; }
    /// <summary>owner/name of the GitHub repository whose releases are checked.</summary>
    public string Repository { get; set; } = "shohamh/LANDock";
    /// <summary>Optional GitHub token, only needed while the repository is private. Stored encrypted for this Windows user.</summary>
    public string TokenProtected { get; set; } = "";

    [JsonIgnore]
    public string Token
    {
        get => Protect.Unprotect(TokenProtected);
        set => TokenProtected = Protect.ProtectText(value);
    }

    /// <summary>Update the user said "not now" to (offered again when a newer one appears).</summary>
    public string SkippedVersion { get; set; } = "";
}

public sealed class Settings
{
    public bool StartWithWindows { get; set; } = true;
    public HostSettings Host { get; set; } = new();
    public ClientSettings Client { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load(string? file = null)
    {
        file ??= Paths.SettingsFile;
        try
        {
            if (File.Exists(file))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json) ?? new Settings();
                s.Normalize();
                return s;
            }
        }
        catch (Exception ex) { Log.Error("settings file unreadable, using defaults", ex); }
        return new Settings();
    }

    public void Save(string? file = null)
    {
        file ??= Paths.SettingsFile;
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, file, true);
    }

    /// <summary>Keeps the values inside what the rest of the program can handle.</summary>
    public void Normalize()
    {
        Updates.IntervalMinutes = Math.Clamp(Updates.IntervalMinutes, 1, 24 * 60);
        if (Updates.Mode != "auto") Updates.Mode = "ask";
        Host.PairingWindowMinutes = Math.Clamp(Host.PairingWindowMinutes, 1, 120);
        if (Client.DisplayMode != "windowed") Client.DisplayMode = "fullscreen";
        Client.HostAddress = (Client.HostAddress ?? "").Trim();
        if (Client.Streams.Count == 0) Client.Streams = ClientSettings.DefaultStreams();
    }
}

/// <summary>Encrypts small secrets for the current Windows user (DPAPI), so a copied settings.json does not leak them.</summary>
public static class Protect
{
    public static string ProtectText(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }
}
