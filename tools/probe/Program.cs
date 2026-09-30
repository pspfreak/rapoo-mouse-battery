using HidSharp;
using System.Diagnostics;
string Hex(byte[] b,int n)=>BitConverter.ToString(b,0,Math.Min(n,b.Length));
var devs = DeviceList.Local.GetHidDevices(0x24AE).ToList();
var streams = new List<(HidDevice d,HidStream s)>();
foreach (var d in devs)
{
    var p = d.DevicePath;
    if (!p.Contains("col0") && !p.Contains("mi_02")) continue;
    if (p.EndsWith("kbd")) continue;
    if (d.GetMaxFeatureReportLength()>0) {
        try {
            using var s = d.Open();
            var rd = d.GetReportDescriptor();
            foreach (var r in rd.FeatureReports) {
                var buf = new byte[d.GetMaxFeatureReportLength()]; buf[0]=r.ReportID;
                try { s.GetFeature(buf); Console.WriteLine($"FEATURE id={r.ReportID} {Hex(buf,buf.Length)}"); } catch(Exception e){Console.WriteLine($"feat fail {e.Message}");}
            }
        } catch(Exception e){Console.WriteLine("open fail "+e.Message);}
    }
    if (d.GetMaxInputReportLength()>0) {
        try { var s=d.Open(); s.ReadTimeout=Timeout.Infinite; streams.Add((d,s)); } catch(Exception e){Console.WriteLine("open fail "+p+" "+e.Message);}
    }
}
Console.WriteLine($"listening on {streams.Count} endpoints for 60s... move mouse, click, etc.");
var sw=Stopwatch.StartNew();
foreach (var (d,s) in streams) {
    var name = d.DevicePath.Contains("mi_02") ? "mi02" : d.DevicePath.Split('&').First(x=>x.StartsWith("col")).Substring(0,5);
    new Thread(()=>{ var buf=new byte[d.GetMaxInputReportLength()]; try{ while(true){ int n=s.Read(buf); Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] {name} {Hex(buf,Math.Min(n,40))}"); } }catch{} }){IsBackground=true}.Start();
}
// usage: probe <seconds> [hex bytes of one output report to send to the FF00:0002 channel, e.g. 07-10-...]
if (args.Length > 1)
{
    Thread.Sleep(2000);
    var target = streams.First(x => x.d.DevicePath.Contains("col09"));
    var data = args[1].Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();
    Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] SEND {Hex(data, data.Length)}");
    try { target.s.Write(data); Console.WriteLine("write ok"); } catch (Exception e) { Console.WriteLine("write failed: " + e.Message); }
}
Thread.Sleep(1000 * (args.Length > 0 ? int.Parse(args[0]) : 180));
