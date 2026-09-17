// 端到端验证：用与 WPF 程序完全相同的核心库执行一次完整业务流
// （连接 → 状态 → 短信列表 → AT 控制台 → 断开），确认 App 侧可用。
using CellPort.Core.At;
using CellPort.Core.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== CellPort 核心库端到端验证 ===\n");

await using var modem = new ModemManager();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

Console.WriteLine("[1] 连接模块（自动探测串口）…");
var sw = System.Diagnostics.Stopwatch.StartNew();
bool ok;
try
{
    ok = await modem.ConnectAsync(null, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("    超时。");
    return 1;
}
sw.Stop();
Console.WriteLine($"    {(ok ? "已连接" : "失败")} · 耗时 {sw.ElapsedMilliseconds}ms");

if (!ok)
{
    Console.WriteLine("    自动探测未成功，改用 COM7 重试…");
    ok = await modem.ConnectAsync("COM7", cts.Token);
    Console.WriteLine($"    {(ok ? "已连接（COM7）" : "仍然失败")}");
    if (!ok) return 1;
}

Console.WriteLine($"    通道：{modem.ChannelDescription}");

Console.WriteLine("\n[2] 状态快照…");
await modem.RefreshStatusAsync(cts.Token);
var s = modem.Status;
Console.WriteLine($"    型号={s.Model} 固件={s.FirmwareRevision}");
Console.WriteLine($"    IMEI={s.Imei} ICCID={s.Iccid}");
Console.WriteLine($"    号码={s.OwnNumber} 运营商={s.Operator}");
Console.WriteLine($"    {s.SignalText} · {s.NetworkMode} · {s.RegistrationStatus}");

Console.WriteLine("\n[3] 短信服务…");
if (modem.Sms is not null)
{
    var inbox = modem.Sms.Inbox;
    Console.WriteLine($"    已存短信：{inbox.Count} 条");
    foreach (var m in inbox.Take(3))
    {
        var body = m.Body ?? string.Empty;
        var preview = body.Length > 30 ? body[..30] + "…" : body;
        var ts = m.Timestamp?.ToString("MM-dd HH:mm") ?? "--";
        Console.WriteLine($"      [{ts}] {m.Number}: {preview}");
    }
}
else
{
    Console.WriteLine("    短信服务未就绪。");
}

Console.WriteLine("\n[4] 通话服务…");
Console.WriteLine(modem.Calls is not null ? "    已就绪" : "    未就绪");

Console.WriteLine("\n[5] AT 控制台（执行 AT+CGMI）…");
var r = await modem.ExecuteAtAsync("AT+CGMI", cts.Token);
Console.WriteLine($"    {(r.Succeeded ? "OK" : "FAIL")} → {string.Join(" | ", r.Lines)}");

Console.WriteLine("\n[6] 断开连接…");
await modem.DisconnectAsync();
Console.WriteLine($"    IsConnected={modem.IsConnected}");

Console.WriteLine("\n=== 验证完成 ===");
return 0;
