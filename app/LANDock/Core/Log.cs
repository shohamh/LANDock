namespace LANDock.Core;

/// <summary>One log file (landock.log in the data folder, rotated at 1 MB) plus an event the UI and the command line listen to.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;

    public static event Action<string>? Line;

    public static void Info(string message) => Write(message);
    public static void Error(string message, Exception? ex = null) => Write(ex == null ? "ERROR: " + message : $"ERROR: {message}: {ex.Message}");

    private static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                var file = Path.Combine(Paths.LogDir, "landock.log");
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    var old = file + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(file, old);
                }
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch { /* logging must never break the app */ }
        }
        try { Line?.Invoke(line); } catch { }
    }

    /// <summary>The last lines of the log, for the About tab and bug reports.</summary>
    public static string Tail(int lines = 200)
    {
        try
        {
            var file = Path.Combine(Paths.LogDir, "landock.log");
            if (!File.Exists(file)) return "";
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd().Split('\n');
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)).Select(l => l.TrimEnd('\r')));
        }
        catch { return ""; }
    }
}
