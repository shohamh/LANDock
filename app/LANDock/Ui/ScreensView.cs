using System.Drawing;
using System.Windows.Forms;
using LANDock.Host;

namespace LANDock.Ui;

/// <summary>
/// Draws a desktop's screens where the laptop's placement puts them, next to the laptop's own screen, and names the laptop display
/// each screen gets (or says that the screen shows a copy of the laptop's screen). It is drawn from the same placement and display
/// choice that Connect uses (LaptopLayout.GroupOrigin, NoticeService.DisplaysFor).
/// </summary>
public sealed class ScreensView : Control
{
    private List<MonitorOffer> _screens = new();
    private ScreenInfo? _laptop;
    private LaptopPlacement _placement = LaptopPlacement.Left;
    private string? _copyScreenId;

    public ScreensView()
    {
        DoubleBuffered = true;
        BackColor = SystemColors.Window;
        Size = new Size(700, 230);
    }

    /// <summary>Shows the desktop's screens. copyScreenId is the screen that shows a copy of the laptop's screen (null for none).</summary>
    public void ShowLayout(IReadOnlyList<MonitorOffer> screens, ScreenInfo? laptop, LaptopPlacement placement, string? copyScreenId)
    {
        _screens = screens.ToList();
        _laptop = laptop;
        _placement = placement;
        _copyScreenId = copyScreenId;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var textFont = new Font(Font.FontFamily, 9f);
        using var boldFont = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        if (_laptop == null || _screens.Count == 0)
        {
            g.DrawString("Select a desktop to see where its screens go.", textFont, SystemBrushes.GrayText, new PointF(8, 8));
            return;
        }

        // the desktop's arrangement and the laptop's screen, in the laptop's coordinates (the laptop's screen at 0,0)
        var (ox, oy) = LaptopLayout.GroupOrigin(_laptop, _screens, _placement);
        var minX = _screens.Min(s => s.X);
        var minY = _screens.Min(s => s.Y);
        var laptopRect = new RectangleF(0, 0, _laptop.Width, _laptop.Height);
        var screenRects = _screens.Select(s => new RectangleF(ox + s.X - minX, oy + s.Y - minY, s.Width, s.Height)).ToList();

        var all = screenRects.Append(laptopRect).ToList();
        var left = all.Min(r => r.Left);
        var top = all.Min(r => r.Top);
        var right = all.Max(r => r.Right);
        var bottom = all.Max(r => r.Bottom);

        // fit the drawing into the control, keeping the proportions, with room at the bottom for the legend
        const int pad = 12, legend = 24;
        var availW = Math.Max(1, Width - 2 * pad);
        var availH = Math.Max(1, Height - 2 * pad - legend);
        var scale = Math.Min(availW / (right - left), availH / (bottom - top));
        PointF ToView(float x, float y) => new(pad + (x - left) * scale, pad + (y - top) * scale);
        RectangleF Scaled(RectangleF r)
        {
            var p = ToView(r.Left, r.Top);
            return new RectangleF(p.X, p.Y, r.Width * scale, r.Height * scale);
        }

        // the laptop's own screen
        var lap = Scaled(laptopRect);
        g.FillRectangle(new SolidBrush(Color.FromArgb(214, 240, 216)), lap);
        g.DrawRectangle(Pens.DarkGreen, lap.X, lap.Y, lap.Width, lap.Height);
        g.DrawString($"This laptop {_laptop.Width}x{_laptop.Height}", boldFont, Brushes.DarkGreen, lap.X + 4, lap.Y + 4);

        // the desktop's screens, each with the display that shows it
        var displays = NoticeService.DisplaysFor(_screens).ToDictionary(x => x.Screen.Id, x => x.Label, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _screens.Count; i++)
        {
            var s = _screens[i];
            var r = Scaled(screenRects[i]);
            var copy = _copyScreenId != null && s.Id.Equals(_copyScreenId, StringComparison.OrdinalIgnoreCase);
            var fill = copy ? Color.FromArgb(230, 230, 230) : Color.FromArgb(214, 230, 248);
            g.FillRectangle(new SolidBrush(fill), r);
            g.DrawRectangle(Pens.SteelBlue, r.X, r.Y, r.Width, r.Height);
            var name = s.Primary ? $"{s.Id}  primary" : s.Id;
            var how = copy ? "copy of laptop screen"
                : displays.TryGetValue(s.Id, out var label) ? $"display {label}" : "no display";
            // three short lines, so that they stay inside a small screen
            g.DrawString(name, boldFont, Brushes.Black, r.X + 4, r.Y + 3);
            g.DrawString($"{s.Width}x{s.Height}", textFont, Brushes.Black, r.X + 4, r.Y + 20);
            g.DrawString(how, textFont, Brushes.Black, r.X + 4, r.Y + 36);
        }

        // legend under the drawing
        g.DrawString("Green: this laptop. Blue: a screen of the desktop, with the laptop display that shows it. Grey: a copy of the laptop's screen.",
            textFont, SystemBrushes.GrayText, new PointF(pad, Height - legend));
    }
}
