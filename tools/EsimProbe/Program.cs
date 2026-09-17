using System.Text;
using CellPort.Core.Models;
using CellPort.Core.Services;

// 验证 CCHO/CGLA 逻辑通道路径（CellDock / lpac 同款协议）能否在本模块上读取 eUICC。
// 输出写入 probe-verify.txt。

Console.OutputEncoding = Encoding.UTF8;

var log = new StringBuilder();
void Log(string s) { Console.WriteLine(s); log.AppendLine(s); Console.Out.Flush(); }

Log("=== 验证逻辑通道 eUICC 读取（CCHO / CGLA）===");

await using var modem = new ModemManager();
modem.TraceMessage += (_, m) => { };

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var ok = await modem.ConnectAsync(null, cts.Token);
Log($"连接：{ok}  ({modem.ChannelDescription})");

if (!ok || modem.Info is null)
{
    Log("连接失败，无法验证。");
    File.WriteAllText("probe-verify.txt", log.ToString());
    return 1;
}

// 1) 原始指令探测
var es10 = new LogicalChannelEs10(modem.Engine!);
Log($"\n[CCHO=? 支持]  {await RawAsync(modem, "AT+CCHO=?")}");
Log($"[CCHC=? 支持]  {await RawAsync(modem, "AT+CCHC=?")}");
Log($"[CGLA=? 支持]  {await RawAsync(modem, "AT+CGLA=?")}");

var supported = await es10.CheckSupportedAsync(cts.Token);
Log($"\nCheckSupportedAsync => {supported}");

if (!supported)
{
    Log("固件不支持逻辑通道指令 —— 走 CSIM 诊断路径。");
    var cap = await modem.Info.ProbeEuiccAsync(cts.Token);
    Log($"ProbeEuiccAsync => {cap.Capability}  ({cap.Detail})");
    File.WriteAllText("probe-verify.txt", log.ToString());
    return 0;
}

// 2) 打开 ISD-R 逻辑通道
var channel = await es10.OpenIsdrAsync(ct: cts.Token);
Log($"\nOpenIsdrAsync => 通道 {channel?.ToString() ?? "(失败)"}");

if (channel is null)
{
    Log("CCHO 未能打开 ISD-R —— 该卡可能不是 eUICC。");
    File.WriteAllText("probe-verify.txt", log.ToString());
    return 0;
}

// 3) 读 EID（同时打印原始 CGLA 响应，便于诊断）
Log("\n--- 原始 CGLA 交换：EID ---");
var eidRaw = await RawAsync(modem, "AT+CGLA=1,10,\"81CA9F7F00\"");
Log($"[EID 原始] {eidRaw}");

var eid = await es10.GetEidAsync(cts.Token);
Log($"\nGetEidAsync => {eid ?? "(失败)"}");

// 4) 读 Profile 列表（同时打印原始 CGLA 响应）
Log("\n--- 原始 CGLA 交换：Profiles ---");
var profRaw = await RawAsync(modem, "AT+CGLA=1,16,\"81E2910003BF2D00\"");
Log($"[Profiles 原始] {profRaw}");

var profiles = await es10.GetProfilesAsync(cts.Token);
Log($"\nGetProfilesAsync => {(profiles is null ? "(失败)" : $"{profiles.Count} 个 Profile")}");
foreach (var p in profiles ?? [])
{
    Log($"  · {p.DisplayName} | {p.StateText} | ICCID={p.Iccid} | SP={p.ServiceProvider} | Class={p.ClassText}");
}

// 5) 生产代码全链路
var probe = await modem.Info.ProbeEuiccAsync(cts.Token);
Log($"\nProbeEuiccAsync => {probe.Capability}");
Log($"  Eid     : {probe.Eid ?? "--"}");
Log($"  Profiles: {probe.Profiles.Count}");
Log($"  Detail  : {probe.Detail}");

// 判据：Ready 即通过（EID 或 Profile 任一可读）。
// 实测 9eSIM 这类 SGP.02 老卡：Profile 全部可读，但 BF3E/GET DATA 两种 EID 路径均不被 ISD-R 支持。
var pass = probe.Capability == EuiccCapability.Ready;
Log($"\n结果：{(pass ? "PASS - eUICC 可读" : "NOT READY")}");

// 6) 切卡链路：启用一个未启用 Profile → 校验 → 恢复原状 → 重启模块恢复会话
bool switchOk = false;
if (profiles is { Count: > 1 })
{
    using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var current = profiles.FirstOrDefault(p => p.Enabled && !string.IsNullOrEmpty(p.IsdpAid));
    var target = profiles.FirstOrDefault(p => !p.Enabled && !string.IsNullOrEmpty(p.IsdpAid));

    if (current is null || target is null)
    {
        Log("\n[切卡测试] 跳过：需要至少一个已启用 + 一个未启用的 Profile。");
    }
    else
    {
        Log($"\n--- 切卡测试：启用 {target.DisplayName} ---");
        var en = await es10.SetProfileStateAsync(target.IsdpAid!, true, cts2.Token);
        Log($"EnableProfile => Ok={en.Ok} Code={en.Code} ({en.Message})");

        if (!en.Ok)
        {
            // 部分老卡不支持隐式停用，先停用当前再启用
            var dis = await es10.SetProfileStateAsync(current.IsdpAid!, false, cts2.Token);
            Log($"先停用 {current.DisplayName} => Ok={dis.Ok} Code={dis.Code} ({dis.Message})");
            en = await es10.SetProfileStateAsync(target.IsdpAid!, true, cts2.Token);
            Log($"重试启用 => Ok={en.Ok} Code={en.Code} ({en.Message})");
        }

        var afterEnable = await es10.GetProfilesAsync(cts2.Token);
        foreach (var p in afterEnable ?? [])
        {
            Log($"  · {p.DisplayName} | {p.StateText}");
        }

        var targetNow = afterEnable?.FirstOrDefault(p => p.Iccid == target.Iccid);

        // ---- 恢复原状 ----
        Log($"--- 恢复：启用 {current.DisplayName} ---");
        var back = await es10.SetProfileStateAsync(current.IsdpAid!, true, cts2.Token);
        Log($"EnableProfile => Ok={back.Ok} Code={back.Code} ({back.Message})");

        if (targetNow?.Enabled ?? false)
        {
            // 多激活卡不会自动停用测试目标，显式停掉以还原
            var disT = await es10.SetProfileStateAsync(target.IsdpAid!, false, cts2.Token);
            Log($"停用测试目标 {target.DisplayName} => Ok={disT.Ok} Code={disT.Code} ({disT.Message})");
        }

        var afterRestore = await es10.GetProfilesAsync(cts2.Token);
        foreach (var p in afterRestore ?? [])
        {
            Log($"  · {p.DisplayName} | {p.StateText}");
        }

        var currentNow = afterRestore?.FirstOrDefault(p => p.Iccid == current.Iccid);
        switchOk = en.Ok && back.Ok && (currentNow?.Enabled ?? false);

        if (switchOk)
        {
            // 卡状态已恢复原状；重启模块让 modem 会话回到干净状态
            Log("\n重启模块（AT+CFUN=1,1）以恢复网络会话…");
            try { await modem.ExecuteAtAsync("AT+CFUN=1,1"); } catch { }
            await Task.Delay(45000);
            try
            {
                await modem.ConnectAsync(null, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            }
            catch { }
            Log($"重启后连接：{modem.IsConnected}  ({modem.ChannelDescription})");
        }
    }
}
Log($"\n切卡链路：{(switchOk ? "OK - 可读可切换" : "未验证或失败（见上方输出）")}");

File.WriteAllText("probe-verify.txt", log.ToString());
return pass ? 0 : 2;

static async Task<string> RawAsync(ModemManager modem, string cmd)
{
    var r = await modem.ExecuteAtAsync(cmd);
    var text = string.Join(" / ", r.Lines);
    return $"{(r.Succeeded ? "OK" : "FAIL")}  {text}";
}
