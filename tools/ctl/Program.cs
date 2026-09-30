using HidSharp;
// usage: ctl read <addr-hex> <len>      read-only EEPROM read (FF00:000E cmd, FF00:000F response)
//        ctl set-stage <0-6>            write the single "current DPI stage" byte (profile 0)
string Hex(byte[] b,int n)=>BitConverter.ToString(b,0,Math.Min(n,b.Length));
// The mouse enumerates as PID 4613 when a data cable is attached (wired, connection byte FF);
// otherwise commands go through the dongle (PID 1413, connection byte A5). CTL_PID=1413 forces the dongle.
int forcePid = Environment.GetEnvironmentVariable("CTL_PID") is { } s ? Convert.ToInt32(s, 16) : 0;
HidDevice? Find(uint usage) {
    foreach (var d in DeviceList.Local.GetHidDevices(0x24AE).OrderByDescending(d => d.ProductID == 0x4613))
        try { if (forcePid != 0 && d.ProductID != forcePid) continue;
            foreach (var it in d.GetReportDescriptor().DeviceItems) foreach (var u in it.Usages.GetAllValues())
            if (u == ((0xFF00u<<16)|usage)) return d; } catch {}
    return null;
}
var cmdDev = Find(0x000E)!; var rspDev = Find(0x000F)!;
byte conn = cmdDev.ProductID == 0x4613 ? (byte)0xFF : (byte)0xA5;
Console.WriteLine($"using PID {cmdDev.ProductID:X4}, connection byte {conn:X2}");
using var cmd = cmdDev.Open(); using var rsp = rspDev.Open();
byte[] Exchange(byte op, int addr, byte[] payload, int len) {
    var pkt = new byte[cmdDev.GetMaxOutputReportLength()];
    pkt[0]=6; pkt[1]=conn; pkt[2]=op; pkt[3]=(byte)len;
    pkt[4]=(byte)addr; pkt[5]=(byte)(addr>>8); pkt[6]=(byte)(addr>>16); pkt[7]=(byte)(addr>>24);
    payload.CopyTo(pkt, 8);
    cmd.Write(pkt);
    Thread.Sleep(150);
    var fb = new byte[rspDev.GetMaxFeatureReportLength()]; fb[0]=8;
    rsp.GetFeature(fb);
    return fb;
}
if (args.Length >= 3 && args[0] == "read") {
    var r = Exchange(0xA4, Convert.ToInt32(args[1],16), Array.Empty<byte>(), int.Parse(args[2]));
    Console.WriteLine("RESP " + Hex(r, r.Length));
} else if (args.Length >= 4 && args[0] == "dump") {
    // ctl dump <start-hex> <end-hex> <outfile>   read-only; one line per chunk: "addr: hex bytes" ("??" = no valid reply)
    int start = Convert.ToInt32(args[1],16), end = Convert.ToInt32(args[2],16);
    const int Chunk = 24;
    using var w = new StreamWriter(args[3]);
    for (int a = start; a < end; a += Chunk) {
        string line;
        try {
            var r = Exchange(0xA4, a, Array.Empty<byte>(), Chunk);
            line = r[0] == 0x01 ? Hex(r[4..(4+Chunk)], Chunk) : "?? status=" + r[0].ToString("X2");
        } catch (Exception e) { line = "?? " + e.Message; }
        w.WriteLine($"{a:X4}: {line}");
    }
    Console.WriteLine("dump done: " + args[3]);
} else if (args.Length >= 2 && args[0] == "set-stage") {
    byte stage = byte.Parse(args[1]);
    if (stage > 6) { Console.WriteLine("stage must be 0-6"); return; }
    const int CurAddr = 0x0600 + 664;
    var before = Exchange(0xA4, CurAddr, Array.Empty<byte>(), 1);
    Console.WriteLine($"before: status={before[0]:X2} stage={before[4]}");
    var w = Exchange(0xA5, CurAddr, new[]{stage}, 1);
    Console.WriteLine("write resp: " + Hex(w, 12));
    Thread.Sleep(300);
    var after = Exchange(0xA4, CurAddr, Array.Empty<byte>(), 1);
    Console.WriteLine($"after: status={after[0]:X2} stage={after[4]}");
} else Console.WriteLine("usage: ctl read <addr-hex> <len> | ctl set-stage <0-6>");
