using System.Drawing.Drawing2D;
using LANDock.Ultrasound;

namespace LANDock.Ui;

/// <summary>
/// Bars for the microphone's level in the ultrasound band: one bar per frequency, height = dB above the room's noise floor.
/// A played tone lights up its bar, so it is obvious when the microphone really picks the sound up.
/// </summary>
public sealed class LevelMeter : Control
{
    private double[] _values = new double[BandMeter.Bars];
    private double _low = 15000, _high = 19500;
    /// <summary>The level at which a bar is full (dB above the floor).</summary>
    private const double FullScaleDb = 25;

    /// <summary>Shows the frequencies of this band under the bars (the mode's band: audible or ultrasound).</summary>
    public void SetBand(double low, double high)
    {
        _low = low;
        _high = high;
        Invalidate();
    }

    public LevelMeter()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.Gainsboro;
        Size = new Size(700, 150);
    }

    public void SetValues(double[] values)
    {
        _values = values;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var n = _values.Length;
        var labelH = 34;
        var gap = 6;
        var barW = (ClientSize.Width - gap * (n + 1)) / (float)n;
        var plotH = ClientSize.Height - labelH - 8;
        using var fontSmall = new Font(Font.FontFamily, 7.5f);
        using var grid = new Pen(Color.FromArgb(60, 60, 66));
        // a line at the noise floor and one at half scale
        for (var frac = 0.0; frac <= 1.0; frac += 0.5)
        {
            var y = 4 + plotH * (1 - frac);
            g.DrawLine(grid, 0, (float)y, ClientSize.Width, (float)y);
        }
        for (var i = 0; i < n; i++)
        {
            var db = Math.Max(0, _values[i]);
            var h = (float)Math.Min(1.0, db / FullScaleDb) * plotH;
            var x = gap + i * (barW + gap);
            var rect = new RectangleF(x, 4 + plotH - h, barW, Math.Max(h, 1f));
            using var brush = new LinearGradientBrush(
                new RectangleF(x, 4, barW, plotH), Color.FromArgb(40, 180, 90), Color.FromArgb(230, 60, 50), 90f);
            g.FillRectangle(brush, rect);
            var label = (BandMeter.BarHz(i, _low, _high) / 1000).ToString("0.0");
            var size = g.MeasureString(label, fontSmall);
            g.DrawString(label, fontSmall, Brushes.Gainsboro, x + (barW - size.Width) / 2, ClientSize.Height - labelH + 4);
            var db2 = db > 0.5 ? $"{db:0}" : "";
            if (db2 != "")
            {
                var s2 = g.MeasureString(db2, fontSmall);
                g.DrawString(db2, fontSmall, Brushes.White, x + (barW - s2.Width) / 2, Math.Max(0, 4 + plotH - h - s2.Height));
            }
        }
        g.DrawString("kHz", fontSmall, Brushes.DarkGray, 2, ClientSize.Height - 14);
    }
}
