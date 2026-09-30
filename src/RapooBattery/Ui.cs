using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace RapooBattery;

static class Ui
{
    public const int LowThreshold = 20;
    public const int CriticalThreshold = 10;

    public static readonly Color Green = Color.FromArgb(76, 194, 105);
    public static readonly Color Amber = Color.FromArgb(255, 185, 0);
    public static readonly Color Red = Color.FromArgb(232, 65, 55);
    public static readonly Color Accent = Color.FromArgb(0, 120, 212);

    public static Color LevelColor(int pct) =>
        pct <= CriticalThreshold ? Red : pct <= LowThreshold ? Amber : Green;

    static bool ReadLightFlag(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int v && v != 0;
        }
        catch (Exception) { return false; }
    }

    public static bool AppsUseLightTheme => ReadLightFlag("AppsUseLightTheme");
    public static bool TaskbarIsLight => ReadLightFlag("SystemUsesLightTheme");

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
