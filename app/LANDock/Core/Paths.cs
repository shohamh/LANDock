namespace LANDock.Core;

/// <summary>Where things live. The install folder holds the program and the two components; the data folder holds
/// everything the app generates (settings, keys, logs, the per-stream Moonlight copies).</summary>
public static class Paths
{
    private static string _installDir =
        Full(Environment.GetEnvironmentVariable("LANDOCK_INSTALL_DIR") ?? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
    private static string _dataDir =
        Full(Environment.GetEnvironmentVariable("LANDOCK_DATA_DIR")
             ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LANDock"));

    /// <summary>Always one spelling (backslashes, no trailing slash): paths are compared as text, for example to find our own processes.</summary>
    private static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\');

    /// <summary>Folder with LANDock.exe, host\ and client\. LANDOCK_INSTALL_DIR overrides it (tests, portable use).</summary>
    public static string InstallDir { get => _installDir; set => _installDir = Full(value); }

    /// <summary>Settings, credentials, certificates, logs. LANDOCK_DATA_DIR overrides it.</summary>
    public static string DataDir { get => _dataDir; set => _dataDir = Full(value); }

    public static string HostDir => Path.Combine(InstallDir, "host");
    public static string HostBin => Path.Combine(HostDir, "bin");
    public static string Sunshine => Path.Combine(HostBin, "sunshine.exe");
    public static string VddTool => Path.Combine(HostBin, "vddtool.exe");
    public static string AppsJson => Path.Combine(HostDir, "apps.json");
    public static string HostVersionFile => Path.Combine(HostDir, "VERSION.txt");

    public static string ClientDir => Path.Combine(InstallDir, "client");
    public static string MoonlightSource => Path.Combine(ClientDir, "moonlight");
    public static string ClientVersionFile => Path.Combine(ClientDir, "VERSION.txt");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HostState => Path.Combine(DataDir, "host");
    public static string Credentials => Path.Combine(HostState, "credentials.txt");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string StreamsDir => Path.Combine(DataDir, "streams");
    public static string UpdatesDir => Path.Combine(DataDir, "updates");

    public static string InstanceDir(string key) => Path.Combine(HostState, key);
    public static string InstanceConf(string key) => Path.Combine(HostState, key + ".conf");
    public static string InstanceLog(string key) => Path.Combine(InstanceDir(key), "apollo.log");
    public static string InstanceOut(string key) => Path.Combine(InstanceDir(key), "out.txt");

    public static bool HostInstalled => File.Exists(Sunshine);
    public static bool ClientInstalled => File.Exists(Path.Combine(MoonlightSource, "Moonlight.exe"));

    /// <summary>A path with forward slashes, the form Apollo's config files use.</summary>
    public static string Fwd(string p) => p.Replace('\\', '/');

    public static string ReadVersion(string file) =>
        File.Exists(file) ? File.ReadAllText(file).Trim() : "";
}
