using System.Diagnostics;
using System.Threading;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit.Abstractions;

namespace LANDock.E2E;

/// <summary>
/// End-to-end tests of the settings windows, driven through UI Automation (FlaUI), with two separate LANDock instances on this PC:
/// the laptop (hosts the streams) and the desktop (finds it, pairs, and keeps its own settings). Each instance has its own data
/// folder, so they are separate computers as far as LANDock is concerned: own settings, own identity, own Moonlight copies.
///
/// No sound is used: the tests never turn on the ultrasound/listening settings, and the log is checked to show that no sound
/// service started. Streams are set to audio "host" so nothing plays on this PC even when a display starts.
/// </summary>
[CollectionDefinition("e2e", DisableParallelization = true)]
public class E2ECollection { }

[Collection("e2e")]
[Trait("Category", "E2E")]
public class E2ETests
{
    private readonly ITestOutputHelper _out;
    public E2ETests(ITestOutputHelper output) => _out = output;

    private static readonly string Root = FindRoot();
    private static readonly string Install = Path.Combine(Root, "build", "run");
    private static readonly string Work = Path.Combine(Root, "build", "e2e");
    private static readonly string LaptopData = Path.Combine(Work, "laptop");
    private static readonly string DesktopData = Path.Combine(Work, "desktop");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the LANDock repository root was not found");
    }

    private void Step(string what) => _out.WriteLine($"[{DateTime.Now:HH:mm:ss}] {what}");

    /// <summary>A screenshot of the screen, saved for a failed run (to see what the windows showed).</summary>
    private void Snap(string name)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(Work, "shots"));
            FlaUI.Core.Capturing.Capture.Screen().ToFile(Path.Combine(Work, "shots", $"{DateTime.Now:HHmmss}-{name}.png"));
        }
        catch (Exception ex) { _out.WriteLine("screenshot failed: " + ex.Message); }
    }

    private static Application Launch(string dataDir, string tab)
    {
        var psi = new ProcessStartInfo(Path.Combine(Install, "LANDock.exe"), $"--settings --tab {tab}") { UseShellExecute = false };
        psi.Environment["LANDOCK_DATA_DIR"] = dataDir;
        psi.Environment["LANDOCK_INSTALL_DIR"] = Install;
        return Application.Launch(psi);
    }

    private static void Shutdown(Application? app)
    {
        if (app == null) return;
        try { app.Close(); } catch { }
        try { if (!app.HasExited) app.Kill(); } catch { }
        app.Dispose();
    }

    private static Window MainWindow(Application app, UIA3Automation a) =>
        Retry.WhileNull(() => app.GetMainWindow(a), TimeSpan.FromSeconds(30)).Result
        ?? throw new InvalidOperationException("the LANDock window did not open");

    private static AutomationElement? Named(AutomationElement root, string name) =>
        root.FindFirstDescendant(cf => cf.ByName(name));

    private static AutomationElement Need(AutomationElement root, string name, int timeoutSeconds = 20) =>
        Retry.WhileNull(() => Named(root, name), TimeSpan.FromSeconds(timeoutSeconds)).Result
        ?? throw new InvalidOperationException($"no control named '{name}'");

    /// <summary>Selects a tab of the settings window (the tab item itself, not a label with the same text).</summary>
    private static void Tab(Window w, string name)
    {
        var tab = Retry.WhileNull(() => w.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName(name))),
            TimeSpan.FromSeconds(20)).Result ?? throw new InvalidOperationException($"no tab named '{name}'");
        tab.AsTabItem().Select();
    }

    /// <summary>Buttons that open a dialog: pressed with a real click in the front window, so the test does not wait for the dialog.</summary>
    private static readonly HashSet<string> OpensDialog = new() { "Pair…", "Rename…", "Forget selected desktop", "Remove" };

    /// <summary>Presses a button once it is enabled. Ordinary buttons use the Invoke pattern; buttons that open a dialog get focus and a
    /// click, because Invoke waits for the handler, and the handler waits for the dialog.</summary>
    private static void Press(Window w, string name)
    {
        var button = Need(w, name).AsButton();
        Retry.WhileFalse(() => button.IsEnabled, TimeSpan.FromSeconds(20));
        if (OpensDialog.Contains(name))
        {
            w.SetForeground();
            button.Click();
        }
        else button.Invoke();
    }

    /// <summary>The names of the text controls under an element. A window that is changing can refuse to answer for a moment: such
    /// reads are skipped, and the caller waits again.</summary>
    private static List<string> SafeTexts(AutomationElement root)
    {
        var result = new List<string>();
        AutomationElement[] items;
        try { items = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)); }
        catch (System.Runtime.InteropServices.COMException) { return result; }
        foreach (var e in items)
        {
            try { if (e.Name is { } n) result.Add(n); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return result;
    }

    /// <summary>The text of every label on the window that matches the pattern (the status labels have no other handle).</summary>
    private static string? TextMatching(AutomationElement root, string pattern) =>
        SafeTexts(root).FirstOrDefault(n => Regex.IsMatch(n, pattern));

    private static string? WaitText(AutomationElement root, string pattern, int seconds = 60) =>
        Retry.WhileNull(() => TextMatching(root, pattern), TimeSpan.FromSeconds(seconds)).Result;

    private static Window? Dialog(Application app, UIA3Automation a, string title, int seconds = 60) =>
        Retry.WhileNull(() => WindowsOf(app, a).FirstOrDefault(w => SafeTitle(w) == title), TimeSpan.FromSeconds(seconds)).Result;

    /// <summary>A window's title; a window that is closing or busy can throw while UI Automation reads it, which counts as no title.</summary>
    private static string SafeTitle(Window w)
    {
        try { return w.Title; }
        catch (System.Runtime.InteropServices.COMException) { return ""; }
    }

    private static void Ok(Window dialog) => dialog.FindFirstDescendant(cf => cf.ByName("OK"))?.AsButton().Invoke();

    /// <summary>Ends processes left over from an earlier run (the app and its Apollo instances, or Moonlight) that were started
    /// from this repository's run or test folders. Other programs, such as an installed Apollo, are not touched.</summary>
    private static void KillStale()
    {
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && (path.StartsWith(Install, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Work, StringComparison.OrdinalIgnoreCase)))
                {
                    p.Kill(true);
                    p.WaitForExit(5000);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        Thread.Sleep(1500);
    }

    private static string ReadLog(string dataDir)
    {
        var p = Path.Combine(dataDir, "logs", "landock.log");
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    private static JsonNode Settings(string dataDir) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(dataDir, "settings.json")))!;

    /// <summary>Changes the desktop's settings file while the desktop is closed (the way a fixture sets up a state).</summary>
    private static void EditSettings(string dataDir, Action<JsonNode> change)
    {
        var path = Path.Combine(dataDir, "settings.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        change(node);
        File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>The visible top-level windows of one app. They are found with the Win32 window list (by process id) and turned into
    /// automation elements one by one: the whole-desktop UI Automation search is slow, and it misses dialogs in some states.</summary>
    private static List<Window> WindowsOf(Application app, UIA3Automation a)
    {
        var handles = new List<IntPtr>();
        var pid = (uint)app.ProcessId;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid && IsWindowVisible(h)) handles.Add(h);
            return true;
        }, IntPtr.Zero);
        var windows = new List<Window>();
        foreach (var h in handles)
        {
            try { windows.Add(a.FromHandle(h).AsWindow()); }
            catch (Exception ex) { Console.WriteLine($"window {h} (pid {pid}): {ex.GetType().Name} {ex.Message}"); }
        }
        Console.WriteLine($"pid {pid}: {handles.Count} visible windows, {windows.Count} automated");
        return windows;
    }

    /// <summary>A popup window of the app (a message box or a dialog) that matches the test.</summary>
    private static Window? Popup(Application app, UIA3Automation a, Func<Window, bool> match, int seconds = 30) =>
        Retry.WhileNull(() => WindowsOf(app, a).FirstOrDefault(w => { try { return match(w); } catch (System.Runtime.InteropServices.COMException) { return false; } }), TimeSpan.FromSeconds(seconds)).Result;

    private static string FirstText(Window w) =>
        SafeTexts(w).FirstOrDefault() ?? "";

    [Fact]
    public async Task Laptop_and_desktop_settings_end_to_end_without_sound()
    {
        if (!File.Exists(Path.Combine(Install, "LANDock.exe")) || !File.Exists(Path.Combine(Install, "client", "moonlight", "Moonlight.exe")))
        {
            Step("skipped: build/run is not assembled (see docs/building.md)");
            return;
        }
        KillStale();
        if (Directory.Exists(Work)) Directory.Delete(Work, true);
        Directory.CreateDirectory(LaptopData);
        Directory.CreateDirectory(DesktopData);
        using var a = new UIA3Automation();
        Application? laptop = null, desktop = null;
        try
        {
            // ---- the laptop: start the host from its settings window ---------------------------------------------------
            Step("laptop: open the Host tab and start the host");
            laptop = Launch(LaptopData, "host");
            var lw = MainWindow(laptop, a);
            Tab(lw, "Host (laptop)");
            Press(lw, "Start host");
            Assert.NotNull(WaitText(lw, @"^Host: running$", 90));
            var pin = Retry.WhileNull(() => SafeTexts(lw).FirstOrDefault(n => Regex.IsMatch(n, @"^\d{4}$")), TimeSpan.FromSeconds(20)).Result;
            Assert.NotNull(pin);
            var fingerprint = TextMatching(lw, @"certificate fingerprint [0-9A-F]{64}");
            Assert.NotNull(fingerprint);
            Step($"laptop: running, PIN {pin}, {fingerprint}");

            // ---- the desktop: the laptop is found on the LAN by itself -----------------------------------------------
            Step("desktop: open the Client tab; the laptop must be found on the LAN without any setup");
            desktop = Launch(DesktopData, "client");
            var dw = MainWindow(desktop, a);
            Tab(dw, "Client (desktop PC)");
            Assert.NotNull(WaitText(dw, @"LAN: [1-9]\d* laptop\(s\) found", 90));
            Step("desktop: the laptop is listed; pairing it through the Pair… dialog");

            // the list has one row: select it, then press Pair…
            var rows = dw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            Assert.NotEmpty(rows);
            rows[0].Patterns.SelectionItem.Pattern.Select();
            Press(dw, "Pair…");
            var add = Dialog(desktop, a, "Add a host", 15);
            if (add == null)
            {
                Snap("pair-no-dialog");
                _out.WriteLine("windows of the desktop: " + string.Join(" | ", WindowsOf(desktop, a).Select(w => w.Title)));
            }
            add ??= Dialog(desktop, a, "Add a host", 60);
            if (add == null) Snap("no-add-dialog");
            Assert.NotNull(add);
            Press(add!, "Check host");
            var found = Retry.WhileNull(() => add!.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(e => e.Name).FirstOrDefault(n => n != null && n.StartsWith("Found:")), TimeSpan.FromSeconds(30)).Result;
            Assert.NotNull(found);
            Assert.Contains("SHOHAM", found);
            var pinBox = add!.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Last().AsTextBox();
            pinBox.Enter(pin!);
            Press(add!, "Pair");

            var pairing = Dialog(desktop, a, "LANDock pairing", 300);
            Assert.NotNull(pairing);
            var pairMsg = pairing!.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(e => e.Name).FirstOrDefault() ?? "";
            Step($"desktop: pairing said: {pairMsg}");
            Assert.Contains("All streams are paired", pairMsg);
            Ok(pairing);

            // ---- the laptop lists the paired desktop -------------------------------------------------------------------
            Step("laptop: the desktop appears in the list of paired desktops");
            var listed = Retry.WhileFalse(() => lw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length >= 1, TimeSpan.FromSeconds(20)).Result;
            if (!listed)
            {
                // diagnostics: which controls the laptop window exposes right now
                var items = lw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Select(e => e.Name).ToArray();
                var lists = lw.FindAllDescendants(cf => cf.ByControlType(ControlType.List)).Length;
                var texts = SafeTexts(lw).Where(t => t.Contains("SHOHAM") || t.Contains("paired", StringComparison.OrdinalIgnoreCase)).Take(5);
                _out.WriteLine($"laptop desktop list: {items.Length} list items, {lists} lists, texts: {string.Join(" | ", texts)}");
                Snap("laptop-no-desktop-list");
            }
            Assert.True(listed);

            // ---- the desktop saves the pairing; closing and reopening keeps it -----------------------------------------
            Shutdown(desktop); desktop = null;
            EditSettings(DesktopData, s =>
            {
                // streams play nothing on this PC (the sound stays on the laptop), so tests never make sound
                foreach (var h in s["Client"]!["Hosts"]!.AsArray())
                    foreach (var st in h!["Streams"]!.AsArray()) st!["Audio"] = "host";
            });
            desktop = Launch(DesktopData, "client");
            dw = MainWindow(desktop, a);
            Tab(dw, "Client (desktop PC)");
            Assert.NotNull(WaitText(dw, @"^Laptops this PC|LAN: ", 60));
            var saved = Settings(DesktopData)["Client"]!["Hosts"]!.AsArray();
            Assert.Single(saved);
            Assert.False(string.IsNullOrEmpty(saved[0]!["TokenProtected"]?.GetValue<string>()), "the pairing token is saved");
            Step("desktop: pairing kept after a restart");

            // ---- nickname and favorite ---------------------------------------------------------------------------------
            Step("desktop: give the laptop a nickname and make it a favorite");
            dw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))[0].Click();
            Press(dw, "Rename…");
            var nick = Popup(desktop, a, w => w.Title == "Nickname", 20);
            Assert.NotNull(nick);
            var nickBox = nick!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)).AsTextBox();
            nickBox.Enter("Office PC");
            Ok(nick);
            Press(dw, "★ Favorite");
            Retry.WhileFalse(() => Settings(DesktopData)["Client"]!["Hosts"]!.AsArray()[0]!["Favorite"]?.GetValue<bool>() == true, TimeSpan.FromSeconds(10));
            var named = Settings(DesktopData)["Client"]!["Hosts"]!.AsArray()[0]!;
            Assert.Equal("Office PC", named["Name"]!.GetValue<string>());
            Assert.True(named["Favorite"]!.GetValue<bool>());
            Step("desktop: nickname and favorite saved");

            // ---- manual order with a second (offline) laptop ----------------------------------------------------------
            Shutdown(desktop); desktop = null;
            EditSettings(DesktopData, s =>
            {
                var hosts = s["Client"]!["Hosts"]!.AsArray();
                hosts.Add(new JsonObject
                {
                    ["Id"] = "fake-garage", ["Name"] = "Garage PC", ["Address"] = "10.255.255.1", ["ControlPort"] = 49900,
                    ["HostId"] = "fake-garage", ["Fingerprint"] = "AB", ["TokenProtected"] = "", ["Streams"] = new JsonArray(),
                });
            });
            desktop = Launch(DesktopData, "client");
            dw = MainWindow(desktop, a);
            Tab(dw, "Client (desktop PC)");
            Assert.True(Retry.WhileFalse(() => dw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2, TimeSpan.FromSeconds(30)).Result);
            var rowsNow = dw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            rowsNow[1].Click();
            Press(dw, "Move up");
            var order = Retry.WhileNull(() =>
            {
                var m = Settings(DesktopData)["Client"]!["ManualOrder"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                return m.Count == 2 && m[0] == "fake-garage" ? m : null;
            }, TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(order);
            Step("desktop: manual order saved (Move up)");

            // ---- the checkboxes of finding laptops ---------------------------------------------------------------------
            Step("desktop: turn LAN discovery off and on again");
            var lan = Need(dw, "Find laptops on this network (LAN discovery)").AsCheckBox();
            Assert.True(lan.IsChecked == true, "LAN discovery is on by default");
            lan.Toggle();
            Assert.NotNull(WaitText(dw, @"LAN discovery is off", 15));
            Assert.False(Settings(DesktopData)["Client"]!["DiscoverLan"]!.GetValue<bool>());
            Need(dw, "Find laptops on this network (LAN discovery)").AsCheckBox().Toggle();
            Assert.NotNull(WaitText(dw, @"LAN: \d+ laptop", 30));
            Assert.True(Settings(DesktopData)["Client"]!["DiscoverLan"]!.GetValue<bool>());

            // the sound checkboxes stay as they are: this suite never turns them on
            Assert.False(Need(dw, "Let laptops hear this PC by sound (plays its id now and then; off by default: keep off near pets)").AsCheckBox().IsChecked == true);
            Assert.False(Settings(DesktopData)["Client"]!["DiscoverUltrasound"]!.GetValue<bool>());
            Assert.False(Settings(DesktopData)["Client"]!["AutoConnect"]!.GetValue<bool>());

            // ---- General: the sound mode and the devices are saved -----------------------------------------------------
            Step("desktop: General tab: choose the microphone and the speakers, and the sound mode");
            Tab(dw, "General / About");
            // the sound section's combo boxes, in order: mode, microphone, speakers
            var combos = dw.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox)).Select(c => c.AsComboBox()).ToList();
            var modeBox = combos[0];
            modeBox.Items.First(i => i.Name.StartsWith("Audible")).Select();
            combos[1].Items.First(i => i.Name.Contains("Realtek")).Select();
            combos[2].Items.First(i => i.Name.Contains("Realtek")).Select();
            var sound = Retry.WhileNull(() =>
            {
                var s = Settings(DesktopData)["Sound"]!;
                return s["InputDeviceId"]!.GetValue<string>() != "" && s["OutputDeviceId"]!.GetValue<string>() != "" ? s : null;
            }, TimeSpan.FromSeconds(15)).Result;
            Assert.NotNull(sound);
            Assert.Equal("audible", sound!["Mode"]!.GetValue<string>());
            Step("desktop: devices and mode saved");

            // ---- forget: the laptop revokes the desktop, and the desktop can no longer connect ------------------------
            Step("laptop: forget the desktop");
            Tab(lw, "Host (laptop)");
            var deskRow = Retry.WhileNull(() => lw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(), TimeSpan.FromSeconds(20)).Result;
            Assert.NotNull(deskRow);
            deskRow!.Click();
            Press(lw, "Forget selected desktop");
            var confirm = Popup(laptop, a, w => w.FindFirstDescendant(cf => cf.ByName("Yes")) != null, 20);
            Assert.NotNull(confirm);
            confirm!.FindFirstDescendant(cf => cf.ByName("Yes")).AsButton().Invoke();
            var forgotten = Popup(laptop, a, w => w.FindFirstDescendant(cf => cf.ByName("OK")) != null && FirstText(w).Contains("forgotten"), 20);
            Assert.NotNull(forgotten);
            Assert.Contains("forgotten", FirstText(forgotten!));
            Ok(forgotten);
            Step("desktop: starting the displays now is refused");
            Tab(dw, "Client (desktop PC)");
            var offered = dw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            offered[0].Click();
            Press(dw, "Start displays");
            var refused = WaitText(dw, @"no longer paired", 60);
            Assert.NotNull(refused);

            // ---- sound stayed off the whole time ------------------------------------------------------------------------
            Step("checking that no sound service started");
            var log = ReadLog(LaptopData) + ReadLog(DesktopData);
            Assert.DoesNotContain("announcing this PC by sound", log);
            Assert.DoesNotContain("listening for nearby desktops", log);
            Assert.Equal(false, Settings(LaptopData)["Host"]?["ListenUltrasound"]?.GetValue<bool>() ?? false);
            Step("done: the settings flow passed and no sound was made");
        }
        finally
        {
            Shutdown(desktop);
            if (laptop != null)
            {
                try
                {
                    var lw = laptop.GetMainWindow(a);
                    if (lw != null) { Tab(lw, "Host (laptop)"); Named(lw, "Stop host")?.AsButton().Invoke(); }
                }
                catch { }
                Shutdown(laptop);
            }
        }
    }
}
