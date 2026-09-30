using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RapooBattery;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\RapooBatteryTray", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        Application.Run(new TrayContext());
    }

    /// <summary>Appends to %LOCALAPPDATA%\RapooBattery\error.log so a silent exit can be diagnosed.</summary>
    static void LogError(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RapooBattery");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch (Exception) { }
    }
}

sealed class TrayContext : ApplicationContext
{
    const int Hysteresis = 5;
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    static readonly TimeSpan StageRetry = TimeSpan.FromSeconds(30);

    readonly NotifyIcon _tray;
    readonly MouseMonitor _monitor = new();
    readonly Flyout _flyout;
    readonly System.Windows.Forms.Timer _staleTimer = new() { Interval = 10_000 };
    readonly ToolStripMenuItem _statusItem = new("Searching for mouse...") { Enabled = false };
    readonly ToolStripMenuItem _dpiMenu = new("DPI");
    readonly ToolStripMenuItem _startupItem = new("Start with Windows") { CheckOnClick = true };
    readonly ToolStripMenuItem _showPopups = new("Show pop-ups") { CheckOnClick = true };
    readonly Osd _osd = new();
    readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

    const int RiseToDetect = 3;
    static readonly TimeSpan RiseWindow = TimeSpan.FromSeconds(45);

    readonly Queue<(DateTime At, int Level)> _recentLevels = new();
    bool _inferredCharging;
    int _chargePeak;
    DateTime _lastRise;

    MouseStatus? _status;
    DateTime _lastReport = DateTime.MinValue;
    bool _connected;
    bool _lowNotified, _criticalNotified;
    DpiStages? _stages;
    DateTime _stagesTriedAt = DateTime.MinValue;
    bool _loadingStages;
    Icon? _icon;

    public TrayContext()
    {
        _flyout = new Flyout(SelectStageAsync);

        _startupItem.Checked = StartupRegistration.IsEnabled;
        _startupItem.CheckedChanged += (_, _) => StartupRegistration.Set(_startupItem.Checked);
        _showPopups.Checked = Settings.ShowPopups;
        _showPopups.CheckedChanged += (_, _) => Settings.ShowPopups = _showPopups.Checked;

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RebuildDpiMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_dpiMenu);
        menu.Items.Add(_showPopups);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
        Refresh();

        _monitor.StatusReported += s => _ui.Post(_ => OnStatus(s), null);
        _monitor.ConnectionChanged += c => _ui.Post(_ => { _connected = c; Refresh(); }, null);
        _staleTimer.Tick += (_, _) => Refresh();
        _staleTimer.Start();
        _monitor.Start();

        if (Environment.GetCommandLineArgs().Contains("--demo-osd")) PlayOsdDemo();
    }

    /// <summary>Run with --demo-osd to preview each pop-up in turn.</summary>
    void PlayOsdDemo()
    {
        var steps = new Action[]
        {
            () => _osd.Notify(OsdKind.Wired, "Wired · charging", "USB cable connected  ·  78%", 78),
            () => _osd.Notify(OsdKind.Wireless, "Wireless", "Cable removed  ·  78%", 78),
            () => _osd.Notify(OsdKind.Charging, "Charging", "Wireless  ·  78%", 78),
            () => _osd.Notify(OsdKind.Dpi, "1,600 DPI", "Stage 4 of 7", null),
        };
        int i = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 3500 };
        timer.Tick += (_, _) => { if (i < steps.Length) steps[i++](); else timer.Stop(); };
        timer.Start();
    }

    bool Fresh => _status is not null && DateTime.UtcNow - _lastReport < StaleAfter;

    // ---- events ---------------------------------------------------------------------------

    /// <summary>
    /// A charge-only cable is invisible to the dongle, so guess from the battery number: plugging in made
    /// the reading jump ~3 points within seconds, and it then only rises or holds. We call it charging
    /// after a quick rise of 3+ over the last 45s, and stop when the level drops below its peak (or has
    /// gone 30 minutes without rising). It can't see charging that began before the app started.
    /// </summary>
    void UpdateInferredCharging(MouseStatus s)
    {
        var now = DateTime.UtcNow;
        if (s.Wired)
        {
            _inferredCharging = false;
            _recentLevels.Clear();
            return;
        }

        _recentLevels.Enqueue((now, s.Battery));
        while (now - _recentLevels.Peek().At > RiseWindow) _recentLevels.Dequeue();

        if (!_inferredCharging)
        {
            if (s.Battery - _recentLevels.Min(l => l.Level) >= RiseToDetect)
            {
                _inferredCharging = true;
                _chargePeak = s.Battery;
                _lastRise = now;
            }
        }
        else if (s.Battery > _chargePeak)
        {
            _chargePeak = s.Battery;
            _lastRise = now;
        }
        else if (s.Battery < _chargePeak || (_chargePeak < 100 && now - _lastRise > TimeSpan.FromMinutes(30)))
        {
            _inferredCharging = false;
            _recentLevels.Clear();
            _recentLevels.Enqueue((now, s.Battery));
        }
    }

    void OnStatus(MouseStatus s)
    {
        var prev = _status;
        bool wasCharging = _inferredCharging;
        _status = s;
        _lastReport = DateTime.UtcNow;
        UpdateInferredCharging(s);
        if (prev is { } p && _showPopups.Checked) Announce(p, wasCharging, s);
        if (!s.Wired && !_inferredCharging) NotifyIfLow(s.Battery); // no point nagging while it's charging
        if (_stages is null) _ = LoadStagesAsync();
        Refresh();
    }

    /// <summary>Shows at most one on-screen pop-up per report, for the most important change.</summary>
    void Announce(MouseStatus prev, bool wasCharging, MouseStatus now)
    {
        int level = now.Battery;
        if (prev.Wired != now.Wired)
        {
            if (now.Wired) _osd.Notify(OsdKind.Wired, "Wired · charging", $"USB cable connected  ·  {level}%", level);
            else _osd.Notify(OsdKind.Wireless, "Wireless", $"Cable removed  ·  {level}%", level);
        }
        else if (wasCharging != _inferredCharging && !now.Wired)
        {
            if (_inferredCharging) _osd.Notify(OsdKind.Charging, "Charging", $"Wireless  ·  {level}%", level);
            else _osd.Notify(OsdKind.Wireless, "Stopped charging", $"Wireless  ·  {level}%", level);
        }
        else if (prev.Stage != now.Stage && now.Dpi > 0)
        {
            _osd.Notify(OsdKind.Dpi, $"{now.Dpi:N0} DPI", $"Stage {now.Stage + 1} of {_stages?.EnabledCount ?? 7}", null);
        }
    }

    void NotifyIfLow(int pct)
    {
        if (pct > Ui.LowThreshold + Hysteresis) _lowNotified = false;
        if (pct > Ui.CriticalThreshold + Hysteresis) _criticalNotified = false;

        if (pct <= Ui.CriticalThreshold && !_criticalNotified)
        {
            _criticalNotified = _lowNotified = true;
            _tray.ShowBalloonTip(10_000, "Mouse battery critical", $"{pct}% remaining. Charge or replace the battery now.", ToolTipIcon.Error);
        }
        else if (pct <= Ui.LowThreshold && !_lowNotified)
        {
            _lowNotified = true;
            _tray.ShowBalloonTip(10_000, "Mouse battery low", $"{pct}% remaining.", ToolTipIcon.Warning);
        }
    }

    async Task LoadStagesAsync()
    {
        if (_loadingStages || DateTime.UtcNow - _stagesTriedAt < StageRetry) return;
        _loadingStages = true;
        _stagesTriedAt = DateTime.UtcNow;
        try { _stages = await _monitor.ReadStagesAsync(); }
        finally { _loadingStages = false; }
        Refresh();
    }

    async Task SelectStageAsync(int stage)
    {
        bool ok = await _monitor.SetStageAsync(stage);
        if (ok && _status is { } s && _stages is { } st)
        {
            _status = s with { Stage = stage, Dpi = st.Values[stage] };
            Refresh();
        }
        else if (!ok)
        {
            _tray.ShowBalloonTip(4000, "Couldn't change DPI", "The mouse didn't respond. It may be asleep; move it and try again.", ToolTipIcon.Warning);
        }
    }

    void ToggleFlyout()
    {
        if (_flyout.Visible) { _flyout.Hide(); return; }
        if (_flyout.RecentlyClosed) return; // the click that just dismissed it
        _stagesTriedAt = DateTime.MinValue;
        if (_stages is null) _ = LoadStagesAsync();
        Refresh();
        _flyout.ShowNearTray();
    }

    void RebuildDpiMenu()
    {
        _dpiMenu.DropDownItems.Clear();
        if (_stages is null)
        {
            _dpiMenu.Enabled = false;
            return;
        }
        _dpiMenu.Enabled = true;
        for (int i = 0; i < _stages.EnabledCount; i++)
        {
            int stage = i;
            var item = new ToolStripMenuItem($"{_stages.Values[i]:N0} DPI") { Checked = _status?.Stage == i };
            item.Click += async (_, _) => await SelectStageAsync(stage);
            _dpiMenu.DropDownItems.Add(item);
        }
    }

    // ---- display --------------------------------------------------------------------------

    void Refresh()
    {
        bool fresh = Fresh;
        int? battery = _status?.Battery;
        bool wired = fresh && _status is { Wired: true };
        bool charging = wired || (fresh && _inferredCharging);

        string status = battery switch
        {
            null => _connected ? "Waiting for mouse..." : "Dongle not found",
            _ when !fresh => $"Asleep or off (last seen {battery}%)",
            _ => (wired ? $"Charging {battery}%  ·  Wired"
                  : charging ? $"Probably charging {battery}%  ·  Wireless"
                  : $"Battery {battery}%  ·  Wireless")
                 + (_status is { Dpi: > 0 } s ? $"  ·  {s.Dpi:N0} DPI" : ""),
        };

        var old = _icon;
        _icon = RenderIcon(fresh ? battery : null, charging);
        _tray.Icon = _icon;
        old?.Dispose();

        _tray.Text = Truncate("Rapoo mouse – " + status, 127);
        _statusItem.Text = status;

        string message = _connected ? "DPI settings unavailable until the mouse wakes" : "Dongle not found";
        _flyout.UpdateState(battery, fresh, charging, wired, _status?.Stage ?? -1, _stages, _stages is null && _loadingStages ? "Loading DPI settings..." : message);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    static readonly string[] IconFonts = ["Bahnschrift SemiBold Condensed", "Bahnschrift Condensed", "Arial Narrow", "Segoe UI"];
    static readonly string IconFont =
        IconFonts.FirstOrDefault(f => FontFamily.Families.Any(x => x.Name == f)) ?? "Segoe UI";

    /// <summary>
    /// Just the percentage, scaled to fill the whole icon at the tray's real icon size (a 32px icon
    /// shrunk to 16px is unreadable). Colour carries the state: normal, amber/red when low, green while charging.
    /// </summary>
    static Icon RenderIcon(int? pct, bool charging)
    {
        int size = SystemInformation.SmallIconSize.Width;
        bool light = Ui.TaskbarIsLight;

        Color color;
        if (pct is not int v) color = Color.Gray;
        else if (charging) color = light ? Color.FromArgb(16, 124, 16) : Color.FromArgb(84, 214, 110);
        else if (v <= Ui.CriticalThreshold) color = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 84);
        else if (v <= Ui.LowThreshold) color = light ? Color.FromArgb(180, 100, 0) : Ui.Amber;
        else color = light ? Color.FromArgb(24, 24, 24) : Color.White;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Build the text as a path, then scale and centre it to fill the icon exactly.
            using var path = new GraphicsPath();
            using var family = new FontFamily(IconFont);
            path.AddString(pct?.ToString() ?? "?", family, (int)FontStyle.Bold, size, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            float scale = Math.Min((size - 1f) / b.Width, (size - 1f) / b.Height);
            using var m = new Matrix();
            m.Translate(-b.X - b.Width / 2, -b.Y - b.Height / 2);
            m.Scale(scale, scale, MatrixOrder.Append);
            m.Translate(size / 2f, size / 2f, MatrixOrder.Append);
            path.Transform(m);

            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _staleTimer.Stop();
        _monitor.Dispose();
        _flyout.Dispose();
        _osd.Dispose();
        _tray.Dispose();
        base.ExitThreadCore();
    }
}

static class Settings
{
    const string KeyPath = @"Software\RapooBattery";

    public static bool ShowPopups
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("ShowPopups") is not int v || v != 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("ShowPopups", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}

static class StartupRegistration
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "RapooBattery";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null) return;
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
