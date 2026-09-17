using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Text;

// 探针目的：
//   1. 列出系统全部串口，并标注每个端口是否真的能打开（用短超时）。
//   2. 重点测量「幽灵端口」COM13 / COM14 / COM16 / COM17 的 Open() 阻塞时长。
//   3. 用 SetupAPI 风格的 PnP 查询，区分 Status=OK 与 Status=Unknown 的实例。
//
// 输出写入 probe-result.txt（本工具所在目录）。

var outp = new StringBuilder();
void W(string s)
{
    outp.AppendLine(s);
    Console.WriteLine(s);
}

W("=== 串口枚举 ===");
string[] names;
try
{
    names = SerialPort.GetPortNames().OrderBy(Nat).ToArray();
}
catch (Exception ex)
{
    names = [];
    W("GetPortNames 失败：" + ex.Message);
}
W("系统报告端口：" + string.Join(", ", names));

W("");
W("=== Win32_SerialPort（真实物理串口）===");
try
{
    using var searcher = new ManagementObjectSearcher(
        "SELECT DeviceID, Description, Status, PNPDeviceID FROM Win32_SerialPort");
    foreach (var o in searcher.Get())
    {
        W($"  {o["DeviceID"]} | {o["Description"]} | Status={o["Status"]} | {o["PNPDeviceID"]}");
    }
}
catch (Exception ex)
{
    W("  查询失败：" + ex.Message);
}

W("");
W("=== Win32_PnPEntity 中所有 COM 端口（含状态）===");
try
{
    using var searcher = new ManagementObjectSearcher(
        "SELECT Name, DeviceID, Status, ConfigManagerErrorCode FROM Win32_PnPEntity " +
        "WHERE Name LIKE '%(COM%'");
    foreach (var o in searcher.Get())
    {
        W($"  [{o["Status"],-8}] err={o["ConfigManagerErrorCode"],-3} {o["Name"]}");
        W($"            {o["DeviceID"]}");
    }
}
catch (Exception ex)
{
    W("  查询失败：" + ex.Message);
}

W("");
W("=== 逐端口实测 Open() 耗时 ===");
foreach (var n in names)
{
    var sw = Stopwatch.StartNew();
    var ok = false;
    string note = "";
    try
    {
        using var p = new SerialPort(n, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 100,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true,
        };
        p.Open();
        ok = true;
    }
    catch (Exception ex)
    {
        note = ex.GetType().Name + ": " + ex.Message;
        if (ex.InnerException is not null)
        {
            note += " <-- " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
        }
    }
    sw.Stop();
    W($"  {n,-6} open={ok,-5} {sw.ElapsedMilliseconds,6} ms  {note}");
}

W("");
W("=== 每个可打开端口发 AT 探测 ===");
foreach (var n in names)
{
    try
    {
        using var p = new SerialPort(n, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 400,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true,
        };
        p.Open();
        p.DiscardInBuffer();
        p.DiscardOutBuffer();
        p.Write("AT\r");

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1200)
        {
            try
            {
                var s = p.ReadExisting();
                if (!string.IsNullOrEmpty(s))
                {
                    sb.Append(s);
                    if (sb.ToString().Contains("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch
            {
                break;
            }
            Thread.Sleep(30);
        }

        var text = sb.ToString().Replace("\r", "\\r").Replace("\n", "\\n");
        W($"  {n,-6} at='{Trunc(text, 60)}' {sw.ElapsedMilliseconds} ms");
    }
    catch (Exception ex)
    {
        W($"  {n,-6} 打不开：{ex.Message}");
    }
}

File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe-result.txt"), outp.ToString(), Encoding.UTF8);
Console.WriteLine();
Console.WriteLine("结果已写入 probe-result.txt");

static string Nat(string s) => s.PadLeft(10, '0');
static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
