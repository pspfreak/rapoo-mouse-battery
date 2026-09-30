using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace RapooBattery;

enum OsdKind { Wired, Wireless, Charging, Dpi }

/// <summary>
/// Brief on-screen notification (à la Logitech's) above the taskbar: fades in, holds, fades out.
/// Never takes focus and ignores the mouse.
/// </summary>
sealed class Osd : Form
{
    const int W = 320, H = 84;
    const double MaxOpacity = 0.97;
    static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(2600);
    static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(350);

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    DateTime _shownAt;
    OsdKind _kind;
    string _title = "", _subtitle = "";
    int? _battery;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Opacity = 0;
        ClientSize = new Size((int)Math.Ceiling(W * S), (int)Math.Ceiling(H * S));
        using var path = Ui.RoundedRect(new RectangleF(0, 0, Width, Height), 14 * S);
        Region = new Region(path);
        _timer.Tick += (_, _) => Step();
    }

    float S => DeviceDpi / 96f;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TOOLWINDOW (no Alt-Tab), NOACTIVATE (no focus), TRANSPARENT (clicks pass through)
            cp.ExStyle |= 0x80 | 0x08000000 | 0x20;
            return cp;
        }
    }

    public void Notify(OsdKind kind, string title, string subtitle, int? battery)
    {
        _kind = kind; _title = title; _subtitle = subtitle; _battery = battery;

        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - (int)(28 * S));
        _shownAt = DateTime.UtcNow;
        Invalidate();

        if (!Visible) { Opacity = 0; Show(); }
        _timer.Start();
    }

    void Step()
    {
        var t = DateTime.UtcNow - _shownAt;
        if (t < FadeIn) Opacity = MaxOpacity * t / FadeIn;
        else if (t < FadeIn + Hold) Opacity = MaxOpacity;
        else if (t < FadeIn + Hold + FadeOut) Opacity = MaxOpacity * (1 - (t - FadeIn - Hold) / FadeOut);
        else { _timer.Stop(); Hide(); Opacity = 0; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool light = Ui.AppsUseLightTheme;
        var bg = light ? Color.FromArgb(246, 246, 246) : Color.FromArgb(36, 36, 36);
        var fg = light ? Color.FromArgb(28, 28, 28) : Color.White;
        var dim = light ? Color.FromArgb(105, 105, 105) : Color.FromArgb(170, 170, 170);
        var track = light ? Color.FromArgb(214, 214, 214) : Color.FromArgb(70, 70, 70);
        var green = light ? Color.FromArgb(16, 124, 16) : Ui.Green;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(bg);
        g.ScaleTransform(S, S);

        // Mouse glyph
        using (var pen = new Pen(fg, 2.2f))
        {
            using var body = Ui.RoundedRect(new RectangleF(24, 20, 26, 44), 13);
            g.DrawPath(pen, body);
            g.DrawLine(pen, 24, 40, 50, 40);
            g.DrawLine(pen, 37, 20, 37, 40);
        }
        using (var wheel = new SolidBrush(fg)) g.FillRectangle(wheel, 35.5f, 26, 3, 8);

        // Badge: bolt for wired/charging, signal arcs for wireless, nothing for DPI
        switch (_kind)
        {
            case OsdKind.Wired:
            case OsdKind.Charging:
                using (var b = new SolidBrush(_kind == OsdKind.Charging ? Ui.Amber : green))
                    g.FillPolygon(b, new PointF[] { new(62, 14), new(53, 30), new(59, 30), new(55, 44), new(68, 25), new(62, 25), new(66, 14) });
                break;
            case OsdKind.Wireless:
                using (var pen = new Pen(fg, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float r = 6 + i * 5;
                        g.DrawArc(pen, 54 - r + 6, 30 - r, r * 2, r * 2, -50, 100);
                    }
                }
                break;
        }

        using var titleFont = new Font("Segoe UI Semibold", 17f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fgBrush = new SolidBrush(fg);
        using var dimBrush = new SolidBrush(dim);
        g.DrawString(_title, titleFont, fgBrush, 90, 16);
        g.DrawString(_subtitle, subFont, dimBrush, 90, 42);

        if (_battery is int pct)
        {
            var bar = new RectangleF(90, 66, W - 90 - 20, 5);
            using (var p = Ui.RoundedRect(bar, 2.5f)) using (var b = new SolidBrush(track)) g.FillPath(b, p);
            var fill = new RectangleF(bar.X, bar.Y, Math.Max(5, bar.Width * pct / 100f), bar.Height);
            using (var p = Ui.RoundedRect(fill, 2.5f))
            using (var b = new SolidBrush(_kind is OsdKind.Wired or OsdKind.Charging ? green : Ui.LevelColor(pct)))
                g.FillPath(b, p);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
