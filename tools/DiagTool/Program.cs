using CellPort.Core.At;
using CellPort.Core.Services;
using CellPort.Core.Transport;

// ============================================================
//  模块连接诊断工具
//  用法： dotnet run --project tools/DiagTool -- [--at "命令"]
//  作用： 不依赖界面，独立验证与模块的通信链路是否打通。
//         这是排查「模块插上了但程序连不上」的第一手段。
// ============================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("=== DJI 4G 模块诊断工具 ===\n");

// ---- 1. 设备枚举 ----
Console.WriteLine("[1] 扫描 USB 设备…");
var scan = ModuleScanner.Scan();

if (!scan.Found)
{
    Console.WriteLine("    未发现模块。");
    Console.WriteLine("    请确认：");
    Console.WriteLine("      · 模块已通过支持数据传输的 USB 线接入");
    Console.WriteLine("      · SIM 卡已插入");
    Console.WriteLine("      · 若线缆或接口有问题，换一个 USB 口试试");
    Console.WriteLine("\n    若设备管理器中有黄色感叹号的 EG25D-QDC507，");
    Console.WriteLine("    说明模块已被识别，只是尚未绑定驱动 —— 这是正常状态，");
    Console.WriteLine("    继续执行第 2 步检查是否有可用串口。");
}
else
{
    Console.WriteLine($"    发现模块 {scan.VendorId:X4}:{scan.ProductId:X4}" +
                      $"（{(scan.IsQuectelIdentity ? "移远标准身份" : "大疆定制身份")}）");
    Console.WriteLine($"    接口数量：{scan.Interfaces.Count}");
    foreach (var itf in scan.Interfaces.OrderBy(x => x.InterfaceNumber))
    {
        var key = itf.IsAtChannel ? "  <== AT 通道" : "";
        Console.WriteLine($"      {itf}{key}");
    }
    Console.WriteLine($"    AT 通道：{(scan.HasAtChannel ? "存在" : "未找到")}");
}

// ---- 2. 串口枚举 ----
Console.WriteLine("\n[2] 扫描串口…");
var ports = SerialAtChannel.EnumerateCandidates();
if (ports.Count == 0)
{
    Console.WriteLine("    未发现任何串口。");
    Console.WriteLine("    说明 Quectel 官方驱动尚未安装。");
    Console.WriteLine("    本程序可通过 WinUSB 直控方式工作，无需串口。");
}
else
{
    Console.WriteLine($"    发现 {ports.Count} 个串口：{string.Join(", ", ports.Select(p => p.PortName))}");
}

// ---- 3. 尝试建立连接 ----
Console.WriteLine("\n[3] 尝试连接模块…");

// 支持 --port 手动指定串口
var portIndex = Array.IndexOf(args, "--port");
var preferredPort = portIndex >= 0 && portIndex + 1 < args.Length ? args[portIndex + 1] : null;
if (preferredPort is not null)
{
    Console.WriteLine($"    使用指定串口：{preferredPort}");
}

await using var modem = new ModemManager();

// 实时打印连接阶段诊断，便于定位卡点（自动 flush，避免缓冲导致看不到进度）
modem.TraceMessage += (_, msg) =>
{
    Console.WriteLine($"    {msg}");
    Console.Out.Flush();
};

bool connected;
try
{
    // 整体限时 20 秒，避免个别驱动异常导致长时间挂起
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    connected = await modem.ConnectAsync(preferredPort, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("    连接超时（20 秒）。");
    connected = false;
}
catch (Exception ex)
{
    Console.WriteLine($"    连接异常：{ex.Message}");
    connected = false;
}

if (!connected)
{
    Console.WriteLine("    连接失败。");
    Console.WriteLine();
    Console.WriteLine("    已发现的串口若为 COM4..COM9，可手动指定重试：");
    Console.WriteLine("        cellport-diag --port COM5");
    Console.WriteLine();
    Console.WriteLine("    排查要点：");
    Console.WriteLine("      1) 确认串口未被其他程序（如 Quectel 工具、串口助手）占用；");
    Console.WriteLine("      2) AT 口通常是模块暴露的多个 COM 口中的某一个；");
    Console.WriteLine("      3) 若全部串口都无响应，需在设备管理器中把 MI_02 绑定为 WinUSB。");
    return 1;
}

Console.WriteLine($"    已连接：{modem.ChannelDescription}");

// ---- 4. 基础 AT 探测 ----
Console.WriteLine("\n[4] 执行 AT 指令探测…");
foreach (var cmd in new[] { "AT", "ATI", "AT+CGMM", "AT+CPIN?" })
{
    var r = await modem.ExecuteAtAsync(cmd);
    var display = !r.Succeeded && r.Lines.Count == 0 ? "(无响应)" : string.Join(" | ", r.Lines);
    Console.WriteLine($"    {cmd,-12} => {(r.Succeeded ? "OK " : "FAIL")}  {display}");
    Console.WriteLine($"         raw={r.RawText.Replace("\r", "\\r").Replace("\n", "\\n")}  dur={r.Duration.TotalMilliseconds:F0}ms");
}

// ---- 5. 状态读取 ----
Console.WriteLine("\n[5] 读取模块状态…");
await modem.RefreshStatusAsync();
var s = modem.Status;

Console.WriteLine($"    型号      : {s.Model}");
Console.WriteLine($"    固件      : {s.FirmwareRevision ?? "--"}");
Console.WriteLine($"    IMEI      : {s.Imei ?? "--"}");
Console.WriteLine($"    ICCID     : {s.Iccid ?? "--"}");
Console.WriteLine($"    IMSI      : {s.Imsi ?? "--"}");
Console.WriteLine($"    本机号码  : {s.OwnNumber ?? "--"}");
Console.WriteLine($"    运营商    : {s.Operator ?? "--"}");
Console.WriteLine($"    网络制式  : {s.NetworkMode ?? "--"}");
Console.WriteLine($"    信号      : {s.SignalText}");
Console.WriteLine($"    注册状态  : {s.RegistrationStatus ?? "--"}");
Console.WriteLine($"    SIM       : {(s.SimReady ? "就绪" : "未就绪")}");
Console.WriteLine($"    数据附着  : {s.DataAttached switch { true => "已附着", false => "未附着", null => "--" }}");

// ---- 6. 短信存储 ----
Console.WriteLine("\n[6] 检查短信存储…");
var cpms = await modem.ExecuteAtAsync("AT+CPMS?");
foreach (var line in cpms.Lines)
{
    Console.WriteLine($"    {line}");
}

// ---- 7. 用户指定指令 ----
var atIndex = Array.IndexOf(args, "--at");
if (atIndex >= 0 && atIndex + 1 < args.Length)
{
    Console.WriteLine("\n[7] 执行自定义指令…");
    var r = await modem.ExecuteAtAsync(args[atIndex + 1]);
    Console.WriteLine($"    {(r.Succeeded ? "OK" : "FAIL")}");
    foreach (var line in r.Lines)
    {
        Console.WriteLine($"    {line}");
    }
}

Console.WriteLine("\n=== 诊断完成 ===");
return 0;
