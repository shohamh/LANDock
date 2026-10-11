using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LANDock.Core;

public sealed class StreamSettings
{
    /// <summary>The name the host gives this display (M1, M2, TV).</summary>
    public string Name { get; set; } = "";
    /// <summary>Moonlight's port on the host. Set by the host at pairing and at every start.</summary>
    public int Port { get; set; }
    /// <summary>1-based, left to right on this PC (see "monitors").</summary>
    public int Monitor { get; set; } = 1;
    /// <summary>A display that is not in use (for example a fourth display on a desktop with three screens) is kept but not started
    /// and not reported to the laptop.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The size the host agreed to for the last start (chosen from the monitor).</summary>
    public string Resolution { get; set; } = "";
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

/// <summary>A host this PC has paired with. Everything needed to connect again later without a new PIN is kept here.</summary>
public sealed class HostProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>What the user calls it.</summary>
    public string Name { get; set; } = "";
    /// <summary>IP address or host name of the laptop.</summary>
    public string Address { get; set; } = "";
    /// <summary>The host's control port (where the handshake happens).</summary>
    public int ControlPort { get; set; } = 49900;
    /// <summary>The host's own id, checked at every connection (a different laptop on the same address is refused).</summary>
    public string HostId { get; set; } = "";
    /// <summary>Favorites come first in the list (see ClientRanking).</summary>
    public bool Favorite { get; set; }
    /// <summary>Certificate fingerprint the host showed when this PC first paired. Every connection must present it.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>Token the host issued to this PC, encrypted for this Windows user.</summary>
    public string TokenProtected { get; set; } = "";
    public string LastConnected { get; set; } = "";
    public List<StreamSettings> Streams { get; set; } = new();

    [JsonIgnore]
    public string Token
    {
        get => Protect.Unprotect(TokenProtected);
        set => TokenProtected = Protect.ProtectText(value);
    }

    [JsonIgnore]
    public bool Paired => TokenProtected != "";

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}

public sealed class ClientSettings
{
    /// <summary>The host "Start displays" uses when none is named.</summary>
    public string DefaultHostId { get; set; } = "";
    /// <summary>Find laptops on the LAN (mDNS) and list them.</summary>
    public bool DiscoverLan { get; set; } = true;
    /// <summary>Let laptops hear this PC: play its id as ultrasound now and then.</summary>
    public bool DiscoverUltrasound { get; set; }
    /// <summary>Connect by itself to the first paired laptop that is online, in the order of the list.</summary>
    public bool AutoConnect { get; set; }
    /// <summary>This PC's ultrasound id (8 bytes, hex). Made once; sent to the laptops it pairs with.</summary>
    public string UltrasoundId { get; set; } = "";
    /// <summary>Laptop ids in the order the user set with Move up / Move down.</summary>
    public List<string> ManualOrder { get; set; } = new();
    /// <summary>"fullscreen" or "windowed" (a stream can override it).</summary>
    public string DisplayMode { get; set; } = "fullscreen";
    public bool AutoStartStreams { get; set; }
    public List<HostProfile> Hosts { get; set; } = new();

    [JsonIgnore]
    public HostProfile? DefaultHost => Hosts.FirstOrDefault(h => h.Id == DefaultHostId) ?? Hosts.FirstOrDefault();
}

public sealed class HostSettings
{
    /// <summary>Start the host automatically when LANDock starts (the laptop is then always ready).</summary>
    public bool AutoStart { get; set; }
    /// <summary>How long new desktop PCs may pair after the host starts or "New pairing PIN" is pressed.</summary>
    public int PairingWindowMinutes { get; set; } = 10;
    /// <summary>Keep the laptop awake and unlocked-looking while the host runs (input cannot be injected into a locked session).</summary>
    public bool KeepAwake { get; set; } = true;
    /// <summary>The control port desktop PCs connect to. 0 = the first free port from 49900 when the host starts; the chosen port is kept.</summary>
    public int ControlPort { get; set; }
    /// <summary>Listen on the microphone for the ultrasound of nearby desktops (off by default: it uses the microphone).</summary>
    public bool ListenUltrasound { get; set; }
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

/// <summary>How the desktop ids travel (audible chirp or ultrasound), and which microphone and speakers carry them.</summary>
public sealed class SoundSettings
{
    /// <summary>"audible" (a short chirp everyone hears; the default) or "ultrasound" (inaudible, 15-19.5 kHz).</summary>
    public string Mode { get; set; } = "audible";
    /// <summary>The microphone's device id; empty = the default microphone.</summary>
    public string InputDeviceId { get; set; } = "";
    /// <summary>The speakers' device id; empty = a physical output chosen automatically (never a virtual one).</summary>
    public string OutputDeviceId { get; set; } = "";
}

public sealed class Settings
{
    public bool StartWithWindows { get; set; } = true;
    public SoundSettings Sound { get; set; } = new();
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

    private static bool IsHex(string? s, int length) =>
        s != null && s.Length == length && s.All(c => Uri.IsHexDigit(c));

    /// <summary>Keeps the values inside what the rest of the program can handle.</summary>
    public void Normalize()
    {
        Updates.IntervalMinutes = Math.Clamp(Updates.IntervalMinutes, 1, 24 * 60);
        if (Updates.Mode != "auto") Updates.Mode = "ask";
        Host.PairingWindowMinutes = Math.Clamp(Host.PairingWindowMinutes, 1, 120);
        Host.ControlPort = Math.Clamp(Host.ControlPort, 0, 65535);
        if (Client.DisplayMode != "windowed") Client.DisplayMode = "fullscreen";
        Client.Hosts ??= new();
        Client.Hosts.RemoveAll(h => string.IsNullOrWhiteSpace(h.Address));
        foreach (var h in Client.Hosts)
        {
            h.Address = h.Address.Trim();
            h.ControlPort = Math.Clamp(h.ControlPort <= 0 ? 49900 : h.ControlPort, 1, 65535);
            h.Streams ??= new();
        }
        if (!Client.Hosts.Any(h => h.Id == Client.DefaultHostId)) Client.DefaultHostId = Client.Hosts.FirstOrDefault()?.Id ?? "";
        Client.ManualOrder ??= new();
        Sound ??= new();
        if (Sound.Mode != "ultrasound") Sound.Mode = "audible";
        Sound.InputDeviceId ??= "";
        Sound.OutputDeviceId ??= "";
        if (!IsHex(Client.UltrasoundId, 16)) Client.UltrasoundId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
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
