using System.Diagnostics;
using System.Threading;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
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

    /// <summary>A diagnostic line in build/e2e-trace.log. The console of a test run is not shown by "dotnet test", so the details of
    /// the window and dialog lookups are kept in this file.</summary>
    private static void Trace(string line)
    {
        try { File.AppendAllText(Path.Combine(Root, "build", "e2e-trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}"); }
        catch { }
    }

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
        if (!Retry.WhileFalse(() => Tolerant(() => button.IsEnabled, false), TimeSpan.FromSeconds(20)).Result)
            throw new InvalidOperationException($"the button '{name}' stayed disabled");
        // Pair… opens a dialog from its handler: an Invoke call would stay open for as long as the dialog is up, and every other
        // UI Automation call to the window would time out. The keyboard presses the focused button instead (Space).
        if (name == "Pair…")
        {
            ClickButtonWindow(w, name);
            return;
        }
        // the handler of this button waits for a question box: the Invoke call is made on its own thread, and the test goes on
        if (name is "Forget selected desktop" or "Start displays")
        {
            _ = Task.Run(() =>
            {
                try { button.Invoke(); Trace($"invoked '{name}'"); }
                catch (Exception ex) { Trace($"invoke '{name}' failed: {ex.GetType().Name} {ex.Message}"); }
            });
            return;
        }
        if (OpensDialog.Contains(name))
        {
            w.SetForeground();
            button.Click();
        }
        else button.Invoke();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc callback, IntPtr lParam);

    private const uint BM_CLICK = 0x00F5, WM_CHAR = 0x0102;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    /// <summary>Types text into the lowest text box of a dialog, by sending the characters to that box's own window (no focus needed,
    /// and the box sees ordinary keystrokes, so its changed-text handling runs).</summary>
    private static void TypeIntoLowestEdit(string dialogTitle, string text)
    {
        var dialog = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var t = new System.Text.StringBuilder(256); GetWindowText(h, t, t.Capacity);
            if (t.ToString() == dialogTitle) dialog = h;
            return dialog == IntPtr.Zero;
        }, IntPtr.Zero);
        if (dialog == IntPtr.Zero) throw new InvalidOperationException($"no window '{dialogTitle}'");
        var edits = new List<(IntPtr h, int top)>();
        EnumChildWindows(dialog, (child, _) =>
        {
            var cls = new System.Text.StringBuilder(128); GetClassName(child, cls, cls.Capacity);
            if (cls.ToString().Contains("Edit", StringComparison.OrdinalIgnoreCase)) { GetWindowRect(child, out var r); edits.Add((child, r.Top)); }
            return true;
        }, IntPtr.Zero);
        var box = edits.OrderBy(e => e.top).Last().h;
        foreach (var c in text) PostMessage(box, WM_CHAR, (IntPtr)c, IntPtr.Zero);
    }

    /// <summary>Clicks a button by its window: the button's own window gets a click message. It needs no focus, and it does not go
    /// through UI Automation, whose Invoke call would stay open while the handler opens a dialog.</summary>
    private static void ClickButtonWindow(Window w, string text)
    {
        // the window that has the button: the only visible window with this button is the one of the settings being tested
        var button = Retry.WhileNull(() =>
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                if (!IsWindowVisible(top)) return true;
                EnumChildWindows(top, (child, _) =>
                {
                    var t = new System.Text.StringBuilder(128); GetWindowText(child, t, t.Capacity);
                    if (t.ToString() == text) found = child;
                    return true;
                }, IntPtr.Zero);
                return found == IntPtr.Zero;
            }, IntPtr.Zero);
            return found == IntPtr.Zero ? (IntPtr?)null : found;
        }, TimeSpan.FromSeconds(20)).Result ?? throw new InvalidOperationException($"no button '{text}' in the window");
        PostMessage(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>The names of the text controls under an element. A window that is changing can refuse to answer for a moment: such
    /// reads are skipped, and the caller waits again.</summary>
    private static List<string> SafeTexts(AutomationElement root)
    {
        var result = new List<string>();
        var items = Tolerant(() => root.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), Array.Empty<AutomationElement>());
        foreach (var e in items)
        {
            var name = Tolerant(() => e.Name, null);
            if (name != null) result.Add(name);
        }
        return result;
    }

    /// <summary>Runs a UI Automation read, retrying a few times: a window that is changing raises COM errors for a moment
    /// ("an event was unable to invoke any of the subscribers"). After the last try the fallback is the answer.</summary>
    private static T Tolerant<T>(Func<T> read, T fallback)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return read(); }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5) { Thread.Sleep(250); }
            catch (System.Runtime.InteropServices.COMException) { return fallback; }
        }
    }

    private static int CountOf(AutomationElement root, ControlType type) =>
        Tolerant(() => root.FindAllDescendants(cf => cf.ByControlType(type)).Length, 0);

    /// <summary>Writes what a window shows (its text labels) and a screenshot, for a step that failed.</summary>
    private void Dump(string what, AutomationElement root)
    {
        _out.WriteLine($"{what}: texts = {string.Join(" | ", SafeTexts(root).Take(40))}");
        Snap(what.Replace(' ', '-'));
    }

    /// <summary>The text of every label on the window that matches the pattern (the status labels have no other handle).</summary>
    private static string? TextMatching(AutomationElement root, string pattern) =>
        SafeTexts(root).FirstOrDefault(n => Regex.IsMatch(n, pattern));

    private static string? WaitText(AutomationElement root, string pattern, int seconds = 60) =>
        Retry.WhileNull(() => TextMatching(root, pattern), TimeSpan.FromSeconds(seconds)).Result;

    /// <summary>A dialog of the app by its title. The title is read through Win32 and the element is taken from the window's handle:
    /// while a dialog is open, the app's window list through UI Automation can come back empty.</summary>
    private static Window? Dialog(Application app, UIA3Automation a, string title, int seconds = 60) =>
        Retry.WhileNull(() =>
        {
            var pid = (uint)app.ProcessId;
            var handles = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var owner);
                if (owner != pid || !IsWindowVisible(h)) return true;
                var text = new System.Text.StringBuilder(256);
                GetWindowText(h, text, text.Capacity);
                if (text.ToString() == title) handles.Add(h);
                return true;
            }, IntPtr.Zero);
            Trace($"dialog '{title}': {handles.Count} matching window(s)");
            foreach (var h in handles)
            {
                try { return a.FromHandle(h).AsWindow(); }
                catch (Exception ex) { Trace($"dialog {title} ({h}): {ex.GetType().Name} {ex.Message}"); }
            }
            return null;
        }, TimeSpan.FromSeconds(seconds)).Result;

    /// <summary>A window's title; a window that is closing or busy can throw while UI Automation reads it, which counts as no title.</summary>
    private static string SafeTitle(Window w)
    {
        try { return w.Title; }
        catch (System.Runtime.InteropServices.COMException) { return ""; }
    }

    /// <summary>Selects the first row of a list, then waits until the button that acts on the row is enabled. A row can be outside
    /// the visible part of its tab, where a click cannot reach it, so the selection goes through the pattern. The list is rebuilt
    /// when a refresh runs and that drops the selection, so the selection is repeated until the button is enabled.</summary>
    private static void SelectRowFor(Window w, string rowName, string buttonName)
    {
        var button = Need(w, buttonName).AsButton();
        var ok = Retry.WhileFalse(() =>
        {
            var row = Tolerant(() => w.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(i => Tolerant(() => i.Name, "") == rowName), null);
            if (row == null) return false;
            Tolerant(() => { row.Patterns.SelectionItem.Pattern.Select(); return true; }, false);
            return Tolerant(() => button.IsEnabled, false);
        }, TimeSpan.FromSeconds(30)).Result;
        if (!ok) throw new InvalidOperationException($"no row could be selected for '{buttonName}'");
    }

    /// <summary>Moves a drop-down list one item down (or up) with the arrow keys: the selection changes at once, and the form
    /// saves it. The items of an open list are not reachable through UI Automation in this window, so the keys are used.</summary>
    private static void MoveCombo(Window w, AutomationElement combo, bool down = true)
    {
        w.SetForeground();
        combo.Focus();
        Keyboard.Type(down ? VirtualKeyShort.DOWN : VirtualKeyShort.UP);
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

    /// <summary>Starts a run with fresh settings and pairings. The laptop keeps its id and the desktop keeps its Moonlight copies:
    /// the copies live in a folder named by the laptop's id, and Windows asks the firewall question once per program path,
    /// so moving them every run would ask again every run. Only the Moonlight pairing data in the copies is reset.</summary>
    private static void ResetFolders()
    {
        var idFile = Path.Combine(LaptopData, "host", "host-id.txt");
        var keptId = File.Exists(idFile) ? File.ReadAllText(idFile) : null;
        // a folder that a program or a shell still has open is left as it is (its old files are harmless: settings and pairings are reset)
        try { if (Directory.Exists(LaptopData)) Directory.Delete(LaptopData, true); } catch (IOException) { }
        Directory.CreateDirectory(Path.Combine(LaptopData, "host"));
        if (keptId != null) File.WriteAllText(idFile, keptId);

        if (Directory.Exists(DesktopData))
            foreach (var entry in Directory.GetFileSystemEntries(DesktopData))
            {
                if (Path.GetFileName(entry).Equals("streams", StringComparison.OrdinalIgnoreCase)) continue;
                try { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); }
                catch (IOException) { }
            }
        Directory.CreateDirectory(DesktopData);

        var streams = Path.Combine(DesktopData, "streams");
        if (Directory.Exists(streams))
            foreach (var ini in Directory.GetFiles(streams, "Moonlight.ini", SearchOption.AllDirectories)) File.Delete(ini);
    }

    /// <summary>The app's log so far. The app is still writing to it, so it is opened for shared reading (a plain read fails while the app writes).</summary>
    private static string ReadLog(string dataDir)
    {
        var p = Path.Combine(dataDir, "logs", "landock.log");
        if (!File.Exists(p)) return "";
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
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
            catch (Exception ex) { Trace($"window {h} (pid {pid}): {ex.GetType().Name} {ex.Message}"); }
        }
        Trace($"pid {pid}: {handles.Count} visible windows, {windows.Count} automated");
        return windows;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    /// <summary>A message box of the app (a question or an information box), found by its window class (#32770) and title. While
    /// a message box is open, the window list of the app cannot be read, so the box is found by its own handle.</summary>
    private const int IDOK = 1, IDYES = 6;
    private const uint WM_COMMAND = 0x0111;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Answers a message box of the app with the button's command, as the button itself would. The box is found by its
    /// window class (#32770) and its title through Win32: while a box is open, the app's windows cannot be read through UI Automation.</summary>
    private static void AnswerMessageBox(Application app, string title, int buttonId, int seconds = 20)
    {
        var box = Retry.WhileNull(() =>
        {
            var pid = (uint)app.ProcessId;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var owner);
                if (owner != pid || !IsWindowVisible(h)) return true;
                var cls = new System.Text.StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                var text = new System.Text.StringBuilder(256);
                GetWindowText(h, text, text.Capacity);
                if (cls.ToString() == "#32770" && text.ToString() == title) found = h;
                return true;
            }, IntPtr.Zero);
            return found == IntPtr.Zero ? (IntPtr?)null : found;
        }, TimeSpan.FromSeconds(seconds)).Result ?? throw new InvalidOperationException($"no message box '{title}' appeared");
        PostMessage(box, WM_COMMAND, (IntPtr)buttonId, IntPtr.Zero);
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
        ResetFolders();
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
            // the app's own log says when the host is ready; the status label follows on its next refresh
            var ready = Retry.WhileFalse(() => ReadLog(LaptopData).Contains("host ready"), TimeSpan.FromSeconds(120)).Result;
            if (!ready) Dump("laptop host not ready", lw);
            Assert.True(ready, "the laptop host did not become ready (see the laptop log)");
            var running = WaitText(lw, @"^Host: running$", 60);
            if (running == null) Dump("laptop status not running", lw);
            Assert.NotNull(running);
            var pin = Retry.WhileNull(() => SafeTexts(lw).FirstOrDefault(n => Regex.IsMatch(n, @"^\d{4}$")), TimeSpan.FromSeconds(20)).Result;
            if (pin == null) Dump("laptop PIN missing", lw);
            Assert.NotNull(pin);
            var fingerprint = TextMatching(lw, @"certificate fingerprint [0-9A-F]{64}");
            if (fingerprint == null) Dump("laptop fingerprint missing", lw);
            Assert.NotNull(fingerprint);
            Step($"laptop: running, PIN {pin}, {fingerprint}");

            // ---- the desktop: the laptop is found on the LAN by itself -----------------------------------------------
            Step("desktop: open the Client tab; the laptop must be found on the LAN without any setup");
            desktop = Launch(DesktopData, "client");
            _out.WriteLine($"desktop process {desktop.ProcessId}, exited {desktop.HasExited}");
            var dw = MainWindow(desktop, a);
            Tab(dw, "Client (desktop PC)");
            Assert.NotNull(WaitText(dw, @"LAN: [1-9]\d* laptop\(s\) found", 90));
            Step("desktop: the laptop is listed; pairing it through the Pair… dialog");

            // the list has one row: select it, then press Pair…. The list is rebuilt when the discovery refreshes, which drops the
            // selection, so the row is selected again until Pair… is enabled.
            var pairButton = Need(dw, "Pair…").AsButton();
            var selected = Retry.WhileFalse(() =>
            {
                var rs = Tolerant(() => dw.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)), Array.Empty<AutomationElement>());
                if (rs.Length == 0) return false;
                Tolerant(() => { rs[0].Patterns.SelectionItem.Pattern.Select(); return true; }, false);
                return Tolerant(() => pairButton.IsEnabled, false);
            }, TimeSpan.FromSeconds(30)).Result;
            Assert.True(selected, "no laptop row could be selected for Pair…");
            Press(dw, "Pair…");
            var add = Dialog(desktop, a, "Add a host", 15);
            if (add == null)
            {
                Snap("pair-no-dialog");
                _out.WriteLine("windows of the desktop: " + string.Join(" | ", WindowsOf(desktop, a).Select(w => w.Title)));
            }
            add ??= Dialog(desktop, a, "Add a host", 60);
            if (add == null)
            {
                var seen = new List<string>();
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out var owner);
                    if (owner != (uint)desktop.ProcessId) return true;
                    var cls = new System.Text.StringBuilder(64); GetClassName(h, cls, cls.Capacity);
                    var txt = new System.Text.StringBuilder(256); GetWindowText(h, txt, txt.Capacity);
                    seen.Add($"{cls}/'{txt}'/visible={IsWindowVisible(h)}");
                    return true;
                }, IntPtr.Zero);
                _out.WriteLine("desktop windows: " + string.Join(" | ", seen));
                Snap("no-add-dialog");
            }
            Assert.NotNull(add);
            Press(add!, "Check host");
            var found = Retry.WhileNull(() => add!.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(e => e.Name).FirstOrDefault(n => n != null && n.StartsWith("Found:")), TimeSpan.FromSeconds(30)).Result;
            Assert.NotNull(found);
            Assert.Contains("SHOHAM", found);
            // the PIN box is the lowest text box of the dialog (the order of the controls in the tree is not their order on screen)
            var pinBox = add!.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).OrderBy(e => e.BoundingRectangle.Top).Last().AsTextBox();
            TypeIntoLowestEdit("Add a host", pin!);
            Trace($"pin typed; the box reads '{pinBox.Text}'");
            Press(add!, "Pair");

            var pairing = Dialog(desktop, a, "LANDock pairing", 300);
            Assert.NotNull(pairing);
            var pairMsg = SafeTexts(pairing!).FirstOrDefault() ?? "";
            Step($"desktop: pairing said: {pairMsg}");
            Assert.Contains("All streams are paired", pairMsg);
            Ok(pairing);

            // ---- the laptop lists the paired desktop -------------------------------------------------------------------
            Step("laptop: the desktop appears in the list of paired desktops");
            // selecting the tab again makes the window lay out the list (and its rows) afresh
            Tab(lw, "Host (laptop)");
            var listed = Retry.WhileFalse(() => CountOf(lw, ControlType.ListItem) >= 1, TimeSpan.FromSeconds(30)).Result;
            if (!listed)
            {
                var onDisk = Path.Combine(LaptopData, "host", "paired-desktops.json");
                _out.WriteLine($"laptop: {CountOf(lw, ControlType.ListItem)} list items, {CountOf(lw, ControlType.List)} lists; paired-desktops.json: " +
                               (File.Exists(onDisk) ? File.ReadAllText(onDisk) : "missing"));
                Dump("laptop desktop list missing", lw);
            }
            Assert.True(listed, "the laptop does not list the paired desktop");

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
            var nick = Popup(desktop, a, w => SafeTitle(w) == "Nickname", 20);
            if (nick == null)
            {
                _out.WriteLine("windows of the desktop: " + string.Join(" | ", WindowsOf(desktop, a).Select(SafeTitle)));
                Snap("no-nickname-dialog");
            }
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
            // the mode goes to ultrasound and back to audible (only the choice is saved: the listening and announcing stay off)
            MoveCombo(dw, combos[0], down: true);
            Assert.True(Retry.WhileFalse(() => Settings(DesktopData)["Sound"]!["Mode"]!.GetValue<string>() == "ultrasound", TimeSpan.FromSeconds(10)).Result,
                "the sound mode was not saved as ultrasound");
            MoveCombo(dw, combos[0], down: false);
            // the microphone and the speakers: the first real device (choosing a device plays nothing)
            MoveCombo(dw, combos[1]);
            MoveCombo(dw, combos[2]);
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
            SelectRowFor(lw, Environment.MachineName, "Forget selected desktop");
            Press(lw, "Forget selected desktop");
            // "Forget ...?" is answered Yes; the saved list on disk then has no desktop left, and the result box is answered OK
            AnswerMessageBox(laptop, "LANDock", IDYES);
            var laptopPairings = Path.Combine(LaptopData, "host", "paired-desktops.json");
            Assert.True(Retry.WhileFalse(() => !File.Exists(laptopPairings) || !File.ReadAllText(laptopPairings).Contains("SHOHAM-LAPTOP"),
                TimeSpan.FromSeconds(30)).Result, "the laptop still lists the desktop after Forget");
            AnswerMessageBox(laptop, "LANDock", IDOK);
            Step("desktop: starting the displays now is refused");
            Tab(dw, "Client (desktop PC)");
            // the first row (its name is the row number: the list shows the laptop's name in another column)
            SelectRowFor(dw, "1", "Start displays");
            Press(dw, "Start displays");
            // the refusal is shown in a box; the desktop's log has the same message
            AnswerMessageBox(desktop!, "LANDock", IDOK, 60);
            Assert.True(Retry.WhileFalse(() => ReadLog(DesktopData).Contains("no longer paired"), TimeSpan.FromSeconds(10)).Result,
                "the desktop did not refuse to start the displays");

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
