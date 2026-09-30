using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace RapooBattery;

/// <summary>Small borderless popup shown when the tray icon is clicked.</summary>
sealed class Flyout : Form
{
    const int W = 264, Pad = 16, RowH = 32;

    readonly Func<int, Task> _selectStage;
    int? _battery;
    bool _fresh;
    bool _charging, _wired;
    int _stage = -1;
    DpiStages? _stages;
    string _stagesMessage = "Loading DPI settings...";
    int _hover = -1;
    DateTime _closedAt = DateTime.MinValue;

    public Flyout(Func<int, Task> selectStage)
    {
        _selectStage = selectStage;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoScaleMode = AutoScaleMode.None; // layout is in logical pixels, scaled by hand in S
        Font = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        ApplyLayout();
    }

    /// <summary>Display scale (1.0 at 96 DPI). All drawing and hit-testing is done in logical pixels.</summary>
    float S => DeviceDpi / 96f;

    // WS_EX_TOOLWINDOW keeps the popup out of Alt-Tab.
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

    public void UpdateState(int? battery, bool fresh, bool charging, bool wired, int stage, DpiStages? stages, string stagesMessage)
    {
        _battery = battery; _fresh = fresh; _charging = charging; _wired = wired; _stage = stage; _stages = stages; _stagesMessage = stagesMessage;
        ApplyLayout();
        Invalidate();
    }

    void ApplyLayout()
    {
        int rows = _stages?.EnabledCount ?? 1;
        var size = new Size((int)Math.Ceiling(W * S), (int)Math.Ceiling((130 + rows * RowH + 14) * S));
        if (ClientSize == size) return;

        // Grow upward: the popup sits at the bottom of the screen, above the taskbar.
        int bottom = Bottom;
        ClientSize = size;
        if (Visible) Location = new Point(Left, bottom - Height);
        using var path = Ui.RoundedRect(new RectangleF(0, 0, Width, Height), 10 * S);
        Region = new Region(path);
    }

    public bool RecentlyClosed => (DateTime.UtcNow - _closedAt).TotalMilliseconds < 250;

    public void ShowNearTray()
    {
        ApplyLayout();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int margin = (int)(12 * S);
        Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);
        Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _closedAt = DateTime.UtcNow;
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Hide();
        base.OnKeyDown(e);
    }

    // ---- painting -----------------------------------------------------------------------

    Rectangle RowRect(int i) => new(Pad - 6, 130 + i * RowH, W - 2 * (Pad - 6), RowH - 2);

    protected override void OnPaint(PaintEventArgs e)
    {
        bool light = Ui.AppsUseLightTheme;
        var bg = light ? Color.FromArgb(248, 248, 248) : Color.FromArgb(32, 32, 32);
        var fg = light ? Color.FromArgb(28, 28, 28) : Color.White;
        var dim = light ? Color.FromArgb(110, 110, 110) : Color.FromArgb(160, 160, 160);
        var track = light ? Color.FromArgb(220, 220, 220) : Color.FromArgb(64, 64, 64);
        var hover = light ? Color.FromArgb(232, 232, 232) : Color.FromArgb(50, 50, 50);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(bg);
        g.ScaleTransform(S, S);

        using var dimBrush = new SolidBrush(dim);
        using var fgBrush = new SolidBrush(fg);

        using (var small = new Font("Segoe UI", 12f, GraphicsUnit.Pixel))
            g.DrawString("Rapoo mouse", small, dimBrush, Pad, 12);

        string big = _battery is int b && _fresh ? $"{b}%" : _battery is int last ? $"{last}%" : "--";
        using (var bigFont = new Font("Segoe UI Semibold", 30f, FontStyle.Regular, GraphicsUnit.Pixel))
            g.DrawString(big, bigFont, _fresh ? fgBrush : dimBrush, Pad - 2, 30);

        string sub = _battery is null ? "Waiting for mouse..."
            : !_fresh ? "Asleep or off (last known)"
            : _wired ? "Charging  ·  Wired (USB)"
            : _charging ? "Probably charging  ·  Wireless"
            : "Battery  ·  Wireless (2.4 GHz)";
        using (var small = new Font("Segoe UI", 12f, GraphicsUnit.Pixel))
            g.DrawString(sub, small, dimBrush, Pad, 72);

        // Battery bar
        var bar = new RectangleF(Pad, 96, W - 2 * Pad, 8);
        using (var p = Ui.RoundedRect(bar, 4)) using (var b2 = new SolidBrush(track)) g.FillPath(b2, p);
        if (_battery is int pct)
        {
            var fill = new RectangleF(bar.X, bar.Y, Math.Max(8, bar.Width * pct / 100f), bar.Height);
            using var p = Ui.RoundedRect(fill, 4);
            using var b2 = new SolidBrush(!_fresh ? dim : _charging ? Ui.Accent : Ui.LevelColor(pct));
            g.FillPath(b2, p);
        }

        // Divider + DPI section
        using (var pen = new Pen(track)) g.DrawLine(pen, Pad, 116, W - Pad, 116);

        if (_stages is null)
        {
            g.DrawString(_stagesMessage, Font, dimBrush, Pad, 128);
            return;
        }

        for (int i = 0; i < _stages.EnabledCount; i++)
        {
            var r = RowRect(i);
            if (i == _hover)
            {
                using var p = Ui.RoundedRect(r, 5); using var hb = new SolidBrush(hover); g.FillPath(hb, p);
            }
            bool current = i == _stage;
            if (current)
            {
                using var acc = new SolidBrush(Ui.Accent);
                g.FillRoundedBar(acc, new RectangleF(r.X + 2, r.Y + 7, 3, r.Height - 14));
            }
            using var f = new Font("Segoe UI", 13.5f, current ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString($"{_stages.Values[i]:N0} DPI", f, fgBrush, new RectangleF(r.X + 14, r.Y, r.Width - 30, r.Height), sf);
            if (current) g.DrawString("✓", f, fgBrush, new RectangleF(r.Right - 26, r.Y, 22, r.Height), sf);
        }
    }

    // ---- input --------------------------------------------------------------------------

    int RowAt(Point physical)
    {
        if (_stages is null) return -1;
        var p = new Point((int)(physical.X / S), (int)(physical.Y / S));
        for (int i = 0; i < _stages.EnabledCount; i++)
            if (RowRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1; Invalidate();
        base.OnMouseLeave(e);
    }

    protected override async void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = RowAt(e.Location);
        if (i >= 0 && i != _stage) await _selectStage(i);
    }
}

static class GraphicsExtensions
{
    public static void FillRoundedBar(this Graphics g, Brush brush, RectangleF r)
    {
        using var p = Ui.RoundedRect(r, r.Width / 2);
        g.FillPath(brush, p);
    }
}
