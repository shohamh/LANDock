using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using LANDock.Host;

namespace LANDock.Ui;

/// <summary>
/// The small notice that comes up when a paired desktop is recognized: its name, a diagram of its screens (rectangles with their
/// relative positions, sizes and resolutions), and Connect, Snooze and Dismiss. It never takes the keyboard focus or the foreground:
/// it is shown without activation, sits above the taskbar in the corner of the clock, and goes away by itself when ignored.
/// </summary>
public sealed class DesktopNotice : Form
{
    /// <summary>How long the notice stays when nobody presses a button; it does not come back by itself.</summary>
    public static readonly TimeSpan IgnoredAfter = TimeSpan.FromSeconds(30);

    private const int WidthPx = 300, HeightPx = 214;
    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;

    private readonly NoticeInfo _info;
    private readonly Label _status;
    private readonly Button _connect, _snooze, _dismiss;
    private readonly List<NoticeBox> _boxes;
    private readonly System.Windows.Forms.Timer _ignore = new() { Interval = (int)IgnoredAfter.TotalMilliseconds };

    private static readonly Rectangle DiagramArea = new(12, 40, WidthPx - 24, 104);

    private readonly Func<string, Task<string?>> _connectAction;
    private readonly Action<string> _dismissed;
    private readonly Action<string> _snoozed;

    public DesktopNotice(NoticeInfo info, int snoozeMinutes, Func<string, Task<string?>> connect, Action<string> dismiss, Action<string> snooze)
    {
        _info = info;
        _connectAction = connect;
        _dismissed = dismiss;
        _snoozed = snooze;
        _boxes = NoticeLayout.Diagram(info.Screens, DiagramArea);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = SystemColors.Window;
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        ClientSize = new Size(WidthPx, HeightPx);
        Location = NoticeLayout.Corner(Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720), Size,
            CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft);

        var title = new Label
        {
            Text = info.Name, Location = new Point(12, 8), AutoSize = false, Size = new Size(WidthPx - 24, 26),
            Font = new Font(Font, FontStyle.Bold), ForeColor = SystemColors.WindowText, UseMnemonic = false,
        };
        Controls.Add(title);

        _status = new Label { Text = "", Location = new Point(12, 150), Size = new Size(WidthPx - 24, 16), ForeColor = SystemColors.GrayText, Font = new Font(Font.FontFamily, 7.5f) };
        Controls.Add(_status);

        _connect = MakeButton("Connect", 12);
        _snooze = MakeButton($"Snooze {snoozeMinutes} min", 98);
        _dismiss = MakeButton("Dismiss", 200);
        _connect.Click += async (_, _) => await ConnectAsync();
        _snooze.Click += (_, _) => { _snoozed(_info.DesktopId); Close(); };
        _dismiss.Click += (_, _) => { _dismissed(_info.DesktopId); Close(); };
        Controls.Add(_connect); Controls.Add(_snooze); Controls.Add(_dismiss);

        Paint += (_, e) => PaintDiagram(e.Graphics);
        _ignore.Tick += (_, _) => { _ignore.Stop(); Close(); };
        Shown += (_, _) => _ignore.Start();
        FormClosed += (_, _) => _ignore.Dispose();
    }

    // a click on a notice must not take the focus from the window the user is in
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;   // not in the taskbar or Alt+Tab, never activated
            return cp;
        }
    }

    private static Button MakeButton(string text, int x)
    {
        // TabStop off: keyboard focus stays with the window the user is in
        return new Button { Text = text, Location = new Point(x, HeightPx - 42), Size = new Size(86, 28), TabStop = false, FlatStyle = FlatStyle.System };
    }

    private async Task ConnectAsync()
    {
        _connect.Enabled = _snooze.Enabled = _dismiss.Enabled = false;
        _status.Text = "Starting the displays on this laptop...";
        var error = await _connectAction(_info.DesktopId);
        if (error == null)
        {
            _status.Text = "The desktop is starting its windows.";
            _ignore.Stop();
            var done = new System.Windows.Forms.Timer { Interval = 1500 };
            done.Tick += (_, _) => { done.Stop(); done.Dispose(); Close(); };
            done.Start();
        }
        else
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = error;
            _connect.Enabled = _snooze.Enabled = _dismiss.Enabled = true;
        }
    }

    private void PaintDiagram(Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var labelFont = new Font(Font.FontFamily, 7f);
        foreach (var box in _boxes)
        {
            var fill = box.Primary ? Color.FromArgb(70, 130, 200) : Color.FromArgb(200, 220, 240);
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(Color.FromArgb(40, 70, 110));
            g.FillRectangle(brush, box.Bounds);
            g.DrawRectangle(pen, box.Bounds);
            // the resolution, small, inside the screen (only when it fits)
            var size = g.MeasureString(box.Label, labelFont);
            if (size.Width + 4 < box.Bounds.Width && size.Height + 2 < box.Bounds.Height)
            {
                var color = box.Primary ? Color.White : Color.FromArgb(30, 50, 80);
                using var text = new SolidBrush(color);
                g.DrawString(box.Label, labelFont, text, box.Bounds.X + (box.Bounds.Width - size.Width) / 2f, box.Bounds.Y + (box.Bounds.Height - size.Height) / 2f);
            }
        }
        if (_boxes.Count == 0)
        {
            using var none = new SolidBrush(SystemColors.GrayText);
            g.DrawString("The desktop has not reported its screens yet.", labelFont, none, DiagramArea.X, DiagramArea.Y + 40);
        }
    }
}
