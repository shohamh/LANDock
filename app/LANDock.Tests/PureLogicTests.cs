using System.Drawing;
using LANDock.Client;
using LANDock.Core;
using LANDock.Host;
using LANDock.Update;

namespace LANDock.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.9", 1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.1.0", "0.1.0", 0)]
    [InlineData("0.1.0-rc1", "0.1.0", -1)]            // a pre-release is older than its release
    [InlineData("0.1.0-rc.2", "0.1.0-rc.10", -1)]     // dot-separated numbers compare as numbers (SemVer rule)
    [InlineData("0.1.0-alpha", "0.1.0-beta", -1)]
    [InlineData("v0.3.0", "0.2.0", 1)]                // a leading v is fine
    [InlineData("0.1.0+abc123", "0.1.0", 0)]          // build metadata is ignored
    public void Compare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(SemVer.Compare(a, b)));
}

public class UpdatePlannerTests
{
    private static UpdateManifest Manifest(string app, string host, string client) => new()
    {
        Version = app, Tag = "v" + app,
        Components =
        {
            ["app"] = new ComponentInfo { Version = app, File = $"LANDock-app-{app}.zip" },
            ["host"] = new ComponentInfo { Version = host, File = "host.zip" },
            ["client"] = new ComponentInfo { Version = client, File = "client.zip" },
        },
    };

    [Fact]
    public void Nothing_to_do_when_everything_matches() =>
        Assert.Empty(UpdatePlanner.Plan(Manifest("0.2.0", "h1", "c1"), "0.2.0", "h1", "c1"));

    [Fact]
    public void Only_the_changed_parts_are_planned_and_the_program_comes_last()
    {
        var plan = UpdatePlanner.Plan(Manifest("0.3.0", "h2", "c1"), "0.2.0", "h1", "c1");
        Assert.Equal(new[] { "host", "app" }, plan.Select(p => p.Component).ToArray());
    }

    [Fact]
    public void A_part_that_is_not_installed_is_not_added_by_an_update()
    {
        var plan = UpdatePlanner.Plan(Manifest("0.2.0", "h2", "c2"), "0.2.0", "", "c1");
        Assert.Equal(new[] { "client" }, plan.Select(p => p.Component).ToArray());
    }

    [Fact]
    public void An_older_release_never_downgrades_the_program() =>
        Assert.Empty(UpdatePlanner.Plan(Manifest("0.1.0", "h1", "c1"), "0.2.0", "h1", "c1"));

    [Fact]
    public void Manifest_json_round_trips()
    {
        var json = """{"schema":1,"version":"0.2.0","tag":"v0.2.0","notes":"n","components":{"host":{"version":"a.b","file":"f.zip","sha256":"00","size":5}}}""";
        var m = UpdateManifest.Parse(json);
        Assert.Equal("a.b", m.Components["host"].Version);
        Assert.Equal(5, m.Components["host"].Size);
    }
}

public class HostPlanningTests
{
    private const string Screens =
        "\\\\.\\DISPLAY1 pos=0,0 size=1920x1080 hz=60 orient=0 primary\n" +
        "\\\\.\\DISPLAY6 pos=1920,0 size=1920x1080 hz=60 orient=0\n" +
        "\\\\.\\DISPLAY7 pos=-3840,0 size=3840x2160 hz=30 orient=0\n";

    [Fact]
    public void Screens_are_parsed()
    {
        var s = HostPlanning.ParseScreens(Screens);
        Assert.Equal(3, s.Count);
        Assert.True(s["DISPLAY1"].Primary);
        Assert.Equal((-3840, 3840, 2160), (s["DISPLAY7"].X, s["DISPLAY7"].Width, s["DISPLAY7"].Height));
    }

    [Fact]
    public void The_active_display_is_the_one_created_after_the_last_removal()
    {
        var log = new[] { @"Virtual Display created at \\.\DISPLAY6", "Virtual Display removed", @"Virtual Display created at \\.\DISPLAY7" };
        Assert.Equal("DISPLAY7", HostPlanning.ActiveDisplay(log));
        Assert.Null(HostPlanning.ActiveDisplay(log.Take(2)));
        Assert.Null(HostPlanning.ActiveDisplay(Array.Empty<string>()));
    }

    [Fact]
    public void Displays_are_laid_out_side_by_side_after_the_laptop_in_stream_order()
    {
        var s = HostPlanning.ParseScreens(Screens);
        // M1 = DISPLAY7 (4K, misplaced), M2 = none yet, TV = DISPLAY6
        var todo = HostPlanning.PlanLayout(s, new string?[] { "DISPLAY7", null, "DISPLAY6" });
        Assert.Equal(new[] { "DISPLAY7=1920,0", "DISPLAY6=5760,0" }, todo);
    }

    [Fact]
    public void Nothing_to_do_when_the_layout_is_already_right()
    {
        var s = HostPlanning.ParseScreens("\\\\.\\DISPLAY1 pos=0,0 size=1920x1080 hz=60 orient=0 primary\n\\\\.\\DISPLAY6 pos=1920,0 size=1920x1080 hz=60 orient=0\n");
        Assert.Empty(HostPlanning.PlanLayout(s, new string?[] { "DISPLAY6", null, null }));
    }

    [Fact]
    public void Pins_are_four_digits()
    {
        for (var i = 0; i < 200; i++) Assert.Matches(@"^\d{4}$", HostPlanning.NewPin());
    }
}

public class ClientTests
{
    [Fact]
    public void A_window_that_fills_the_monitor_covers_it() =>
        Assert.True(ClientService.Covers(new Native.RECT { L = 1920, T = 0, R = 3840, B = 1080 }, new Rectangle(1920, 0, 1920, 1080)));

    [Fact]
    public void A_smaller_window_does_not() =>
        Assert.False(ClientService.Covers(new Native.RECT { L = 2000, T = 80, R = 3296, B = 839 }, new Rectangle(1920, 0, 1920, 1080)));
}

public class SettingsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "landock-test-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    [Fact]
    public void Settings_survive_a_save_and_load()
    {
        var s = new Settings();
        s.Client.HostAddress = "192.168.1.50";
        s.Updates.Mode = "auto";
        s.Updates.IntervalMinutes = 7;
        s.Save(_file);
        var back = Settings.Load(_file);
        Assert.Equal("192.168.1.50", back.Client.HostAddress);
        Assert.Equal("auto", back.Updates.Mode);
        Assert.Equal(7, back.Updates.IntervalMinutes);
        Assert.Equal(3, back.Client.Streams.Count);
    }

    [Fact]
    public void The_token_is_stored_encrypted_and_reads_back()
    {
        var s = new Settings();
        s.Updates.Token = "ghp_secret_value";
        s.Save(_file);
        Assert.DoesNotContain("ghp_secret_value", File.ReadAllText(_file));
        Assert.Equal("ghp_secret_value", Settings.Load(_file).Updates.Token);
    }

    [Fact]
    public void Out_of_range_values_are_pulled_back()
    {
        var s = new Settings();
        s.Updates.IntervalMinutes = 0; s.Updates.Mode = "whatever"; s.Host.PairingWindowMinutes = 9999; s.Client.DisplayMode = "sideways";
        s.Normalize();
        Assert.Equal(1, s.Updates.IntervalMinutes);
        Assert.Equal("ask", s.Updates.Mode);
        Assert.Equal(120, s.Host.PairingWindowMinutes);
        Assert.Equal("fullscreen", s.Client.DisplayMode);
    }

    [Fact]
    public void A_broken_file_gives_defaults_instead_of_a_crash()
    {
        File.WriteAllText(_file, "{ this is not json");
        Assert.Equal("shohamh/LANDock", Settings.Load(_file).Updates.Repository);
    }
}

public class FilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "landock-test-" + Guid.NewGuid().ToString("N"));
    public FilesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeZip(string name, params (string entry, string text)[] files)
    {
        var zip = Path.Combine(_dir, name);
        using var fs = File.Create(zip);
        using var a = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
        {
            using var w = new StreamWriter(a.CreateEntry(entry).Open());
            w.Write(text);
        }
        return zip;
    }

    [Fact]
    public void Zip_is_extracted_with_its_folders()
    {
        var zip = MakeZip("a.zip", ("bin/x.txt", "hello"), ("VERSION.txt", "1"));
        Files.ExtractSafe(zip, Path.Combine(_dir, "out"));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dir, "out", "bin", "x.txt")));
    }

    [Fact]
    public void An_archive_that_escapes_its_folder_is_refused()
    {
        var zip = MakeZip("evil.zip", ("../escaped.txt", "x"));
        Assert.Throws<InvalidDataException>(() => Files.ExtractSafe(zip, Path.Combine(_dir, "out")));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public void Swapping_a_folder_replaces_it_and_leaves_no_old_copy()
    {
        var cur = Path.Combine(_dir, "host"); var next = cur + ".new";
        Directory.CreateDirectory(cur); File.WriteAllText(Path.Combine(cur, "v.txt"), "old");
        Directory.CreateDirectory(next); File.WriteAllText(Path.Combine(next, "v.txt"), "new");
        Files.SwapFolder(cur, next);
        Assert.Equal("new", File.ReadAllText(Path.Combine(cur, "v.txt")));
        Assert.False(Directory.Exists(cur + ".old")); Assert.False(Directory.Exists(next));
    }

    [Fact]
    public void Sha256_matches_a_known_value()
    {
        var f = Path.Combine(_dir, "abc.txt");
        File.WriteAllText(f, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Files.Sha256Of(f));
    }
}
