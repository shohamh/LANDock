using System.Drawing;

namespace LANDock.Ui;

/// <summary>A desktop screen as the notice draws it: where it is and how big, in pixels as Windows shows it (rotation already applied).</summary>
public sealed record NoticeScreen(string Id, int X, int Y, int Width, int Height, bool Primary, bool Enabled = true);

/// <summary>A screen's rectangle inside the notice, its resolution label, and whether it is the primary screen. Enabled is false for a
/// screen that is not extended to the laptop (drawn greyed out; a click turns it on again).</summary>
public sealed record NoticeBox(Rectangle Bounds, string Label, bool Primary, bool Portrait, string Id = "", bool Enabled = true);

public static class NoticeLayout
{
    /// <summary>
    /// Where the notice sits: in the corner of the work area (the screen minus the taskbar) where the taskbar's clock is, which is
    /// on the left for a right-to-left language.
    /// </summary>
    public static Point Corner(Rectangle workArea, Size size, bool rightToLeft, int margin = 12) =>
        new(rightToLeft ? workArea.Left + margin : workArea.Right - size.Width - margin,
            workArea.Bottom - size.Height - margin);

    /// <summary>
    /// Lays the screens out inside 'area' keeping their relative positions and sizes: one scale for all of them, so a screen with a
    /// bigger resolution is drawn bigger. Each box is inset by 'gap' so neighbours do not touch.
    /// </summary>
    public static List<NoticeBox> Diagram(IReadOnlyList<NoticeScreen> screens, Rectangle area, int gap = 4, int minSide = 10)
    {
        var res = new List<NoticeBox>();
        if (screens.Count == 0 || area.Width <= 0 || area.Height <= 0) return res;
        var minX = screens.Min(s => s.X);
        var minY = screens.Min(s => s.Y);
        var boxW = Math.Max(1, screens.Max(s => s.X + s.Width) - minX);
        var boxH = Math.Max(1, screens.Max(s => s.Y + s.Height) - minY);
        var scale = Math.Min((double)area.Width / boxW, (double)area.Height / boxH);
        // centred in the area
        var originX = area.Left + (area.Width - boxW * scale) / 2.0;
        var originY = area.Top + (area.Height - boxH * scale) / 2.0;
        foreach (var s in screens)
        {
            var left = originX + (s.X - minX) * scale;
            var top = originY + (s.Y - minY) * scale;
            var w = Math.Max(minSide, (int)Math.Round(s.Width * scale) - gap);
            var h = Math.Max(minSide, (int)Math.Round(s.Height * scale) - gap);
            var bounds = new Rectangle((int)Math.Round(left) + gap / 2, (int)Math.Round(top) + gap / 2, w, h);
            res.Add(new NoticeBox(bounds, $"{s.Width}x{s.Height}", s.Primary, s.Height > s.Width, s.Id, s.Enabled));
        }
        return res;
    }
}
