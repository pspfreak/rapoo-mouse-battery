using HidSharp;

namespace RapooBattery;

/// <param name="Wired">A data cable is attached, which is also when the mouse charges.</param>
public readonly record struct MouseStatus(int Battery, int Stage, int Dpi, bool Wired);

public sealed record DpiStages(int[] Values, int EnabledCount);

/// <summary>
/// Talks to the Rapoo 2.4G dongle (VID 24AE).
///
/// Status (passive): usage FF00:0002, report ID 7, pushed every few seconds. Byte 2 = DPI stage,
/// bytes 3-4 = DPI X (LE), byte 8 = battery percent.
///
/// Control (active): commands go out on FF00:000E as report 6 [06 A5 op len addr(4, LE) data...],
/// with op A4 = read, A5 = write; the reply is read as feature report 8 on FF00:000F (status byte 0,
/// data from byte 4). Profile 0 lives at 0x0600; within it the X DPI list is at +648 (7 x u16 LE),
/// enabled-stage count minus one at +662 and the current stage at +664. Verified against this mouse.
/// </summary>
public sealed class MouseMonitor : IDisposable
{
    const int VendorId = 0x24AE;
    const int WiredProductId = 0x4613; // the mouse itself while a data cable is attached (dongle is 0x1413)
    const uint VendorPage = 0xFF00;
    const byte StatusReportId = 7;
    const int StageIndex = 2, DpiIndex = 3, BatteryIndex = 8;

    const int ProfileBase = 0x0600;
    const int DpiListAddr = ProfileBase + 648;
    const int EnabledGearAddr = ProfileBase + 662;
    const int CurrentStageAddr = ProfileBase + 664;
    const int MaxStages = 7;
    static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(4);

    readonly CancellationTokenSource _cts = new();
    readonly Queue<int> _recent = new();
    readonly object _controlLock = new();

    public event Action<MouseStatus>? StatusReported;
    public event Action<bool>? ConnectionChanged;

    readonly HashSet<string> _readers = new();
    bool _connected;

    public void Start() => new Thread(Supervise) { IsBackground = true, Name = "HID supervisor" }.Start();

    /// <summary>
    /// Keeps one reader per status endpoint. The dongle (PID 1413) is always there; a second endpoint
    /// (the mouse itself, PID 4613) appears while a data cable is attached.
    /// </summary>
    void Supervise()
    {
        while (!_cts.IsCancellationRequested)
        {
            foreach (var device in FindDevices(0x0002))
            {
                lock (_readers)
                {
                    if (!_readers.Add(device.DevicePath)) continue;
                }
                if (!TryOpen(device, out var stream))
                {
                    lock (_readers) _readers.Remove(device.DevicePath);
                    continue;
                }
                new Thread(() => Read(device, stream)) { IsBackground = true, Name = "HID reader" }.Start();
            }
            _cts.Token.WaitHandle.WaitOne(3000);
        }
    }

    void Read(HidDevice device, HidStream stream)
    {
        UpdateConnected();
        using (stream)
        {
            try { ReadLoop(device, stream); }
            catch (Exception) { /* device unplugged or read failed; the supervisor will re-add it */ }
        }
        lock (_readers) _readers.Remove(device.DevicePath);
        UpdateConnected();
    }

    void UpdateConnected()
    {
        bool now;
        lock (_readers) now = _readers.Count > 0;
        if (now == _connected) return;
        _connected = now;
        ConnectionChanged?.Invoke(now);
    }

    void ReadLoop(HidDevice device, HidStream stream)
    {
        stream.ReadTimeout = Timeout.Infinite;
        var buf = new byte[device.GetMaxInputReportLength()];
        while (!_cts.IsCancellationRequested)
        {
            int n = stream.Read(buf);
            if (n <= BatteryIndex || buf[0] != StatusReportId) continue;

            int pct = buf[BatteryIndex];
            if (pct > 100) continue;

            // Low nibble of byte 1 is the connection type: 0 = wireless, 2 = wired (cable attached).
            bool wired = (buf[1] & 0x0F) == 2;

            // The reported value dips a point or two under load; a short median keeps the display steady.
            int median;
            lock (_recent)
            {
                _recent.Enqueue(pct);
                while (_recent.Count > 5) _recent.Dequeue();
                var sorted = _recent.Order().ToArray();
                median = sorted[sorted.Length / 2];
            }

            int dpi = buf[DpiIndex] | (buf[DpiIndex + 1] << 8);
            StatusReported?.Invoke(new MouseStatus(median, buf[StageIndex], dpi, wired));
        }
    }

    // ---- control channel -------------------------------------------------------------------

    /// <summary>Reads the DPI stage table from the mouse. Null if the mouse doesn't answer.</summary>
    public Task<DpiStages?> ReadStagesAsync() => RunControl(() =>
    {
        using var ctl = OpenControl();
        var list = Exchange(ctl, 0xA4, DpiListAddr, MaxStages * 2);
        var gear = Exchange(ctl, 0xA4, EnabledGearAddr, 1);
        if (list is null || gear is null) return null;

        var values = new int[MaxStages];
        for (int i = 0; i < MaxStages; i++) values[i] = list[i * 2] | (list[i * 2 + 1] << 8);
        int count = Math.Clamp(gear[0] + 1, 1, MaxStages);
        return new DpiStages(values, count);
    });

    /// <summary>Switches the active DPI stage (0-based). Returns true if the mouse confirmed it.</summary>
    public Task<bool> SetStageAsync(int stage) => RunControl(() =>
    {
        if (stage is < 0 or >= MaxStages) return false;
        using var ctl = OpenControl();
        if (Exchange(ctl, 0xA5, CurrentStageAddr, 1, (byte)stage) is null) return false;
        var check = Exchange(ctl, 0xA4, CurrentStageAddr, 1);
        return check is not null && check[0] == stage;
    });

    async Task<T?> RunControl<T>(Func<T?> work)
    {
        try
        {
            return await Task.Run(() => { lock (_controlLock) return work(); }).WaitAsync(ControlTimeout);
        }
        catch (Exception) { return default; }
    }

    sealed class ControlPair(HidStream cmd, HidStream rsp, int cmdLen, int rspLen, byte conn) : IDisposable
    {
        public HidStream Cmd = cmd, Rsp = rsp;
        public int CmdLen = cmdLen, RspLen = rspLen;
        public byte Conn = conn;
        public void Dispose() { Cmd.Dispose(); Rsp.Dispose(); }
    }

    /// <summary>
    /// With a cable attached the mouse is off the radio link and only answers over USB (connection
    /// byte FF); otherwise commands go through the dongle (connection byte A5). FindDevices lists
    /// the wired mouse first, so the first match is the right one in both cases.
    /// </summary>
    static ControlPair OpenControl()
    {
        var c = FindDevices(0x000E).FirstOrDefault() ?? throw new IOException("control channel not found");
        var r = FindDevices(0x000F).FirstOrDefault(d => d.ProductID == c.ProductID) ?? throw new IOException("response channel not found");
        byte conn = c.ProductID == WiredProductId ? (byte)0xFF : (byte)0xA5;
        return new ControlPair(c.Open(), r.Open(), c.GetMaxOutputReportLength(), r.GetMaxFeatureReportLength(), conn);
    }

    /// <summary>Sends one command and returns the data bytes of the reply (null on a non-OK status).</summary>
    static byte[]? Exchange(ControlPair ctl, byte op, int addr, int len, params byte[] payload)
    {
        var pkt = new byte[ctl.CmdLen];
        pkt[0] = 6; pkt[1] = ctl.Conn; pkt[2] = op; pkt[3] = (byte)len;
        BitConverter.GetBytes(addr).CopyTo(pkt, 4);
        payload.CopyTo(pkt, 8);
        ctl.Cmd.Write(pkt);

        // Status 01 = OK, 02 = busy; give a busy device a few tries.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(80);
            var fb = new byte[ctl.RspLen];
            fb[0] = 8;
            ctl.Rsp.GetFeature(fb);
            if (fb[0] == 0x01) return fb[4..(4 + Math.Max(len, 1))];
            if (fb[0] != 0x02) return null;
        }
        return null;
    }

    // ---- helpers ---------------------------------------------------------------------------

    void SetConnected(ref bool current, bool value)
    {
        if (current == value) return;
        current = value;
        ConnectionChanged?.Invoke(value);
    }

    /// <summary>All HID interfaces exposing the given vendor usage, wired mouse (if attached) first.</summary>
    static List<HidDevice> FindDevices(uint usage)
    {
        var found = new List<HidDevice>();
        foreach (var d in DeviceList.Local.GetHidDevices(VendorId).OrderByDescending(d => d.ProductID == WiredProductId))
        {
            try
            {
                foreach (var item in d.GetReportDescriptor().DeviceItems)
                    if (item.Usages.GetAllValues().Any(u => u == ((VendorPage << 16) | usage)))
                    {
                        found.Add(d);
                        break;
                    }
            }
            catch (Exception) { }
        }
        return found;
    }

    static bool TryOpen(HidDevice d, out HidStream stream)
    {
        try { stream = d.Open(); return true; }
        catch (Exception) { stream = null!; return false; }
    }

    public void Dispose() => _cts.Cancel();
}
