using System.Text.RegularExpressions;
using CellPort.Core.At;
using CellPort.Core.Models;

namespace CellPort.Core.Services;

/// <summary>
/// 查询模块与 SIM 状态。
/// </summary>
public sealed class DeviceInfoService(AtEngine engine)
{
    private readonly AtEngine _engine = engine;

    /// <summary>
    /// 读取一份完整状态快照。各字段独立容错，单项失败不影响整体。
    /// </summary>
    public async Task<DeviceStatus> ReadStatusAsync(CancellationToken ct = default)
    {
        var status = new DeviceStatus { Connected = true };

        // 型号
        var cgmm = await TryAsync("AT+CGMM", ct).ConfigureAwait(false);
        if (cgmm is not null)
        {
            status.Model = cgmm.FirstLine ?? status.Model;
        }

        // 固件版本
        var cgmr = await TryAsync("AT+CGMR", ct).ConfigureAwait(false);
        if (cgmr is not null)
        {
            status.FirmwareRevision = cgmr.FirstLine;
        }

        // IMEI
        var cgsn = await TryAsync("AT+CGSN", ct).ConfigureAwait(false);
        if (cgsn is not null)
        {
            status.Imei = cgsn.FirstLine;
        }

        // SIM 状态
        var cpin = await TryAsync("AT+CPIN?", ct).ConfigureAwait(false);
        if (cpin is not null)
        {
            status.SimReady = cpin.RawText.Contains("READY", StringComparison.OrdinalIgnoreCase);
        }

        if (status.SimReady)
        {
            var iccid = await TryAsync("AT+QCCID", ct).ConfigureAwait(false);
            if (iccid is not null)
            {
                status.Iccid = ExtractAfterColon(iccid.FirstLine);
            }

            var imsi = await TryAsync("AT+CIMI", ct).ConfigureAwait(false);
            if (imsi is not null)
            {
                status.Imsi = imsi.FirstLine;
            }

            var num = await TryAsync("AT+CNUM", ct).ConfigureAwait(false);
            status.OwnNumber = ParseCnum(num);
        }

        // 信号强度
        var csq = await TryAsync("AT+CSQ", ct).ConfigureAwait(false);
        if (csq is not null)
        {
            var m = Regex.Match(csq.RawText, @"\+CSQ:\s*(\d+)\s*,\s*(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var rssi))
            {
                status.SignalRssi = rssi;
            }
        }

        // 运营商
        var cops = await TryAsync("AT+COPS?", ct).ConfigureAwait(false);
        if (cops is not null)
        {
            var m = Regex.Match(cops.RawText, "\"([^\"]+)\"");
            if (m.Success)
            {
                status.Operator = m.Groups[1].Value;
            }
        }

        // 注册状态
        var creg = await TryAsync("AT+CREG?", ct).ConfigureAwait(false);
        status.RegistrationStatus = ParseReg(creg);

        // 网络制式与附着（移远专有）
        var qnwinfo = await TryAsync("AT+QNWINFO", ct).ConfigureAwait(false);
        if (qnwinfo is not null)
        {
            var m = Regex.Match(qnwinfo.RawText, "\"([^\"]+)\"");
            if (m.Success)
            {
                status.NetworkMode = m.Groups[1].Value;
            }
        }

        var cgatt = await TryAsync("AT+CGATT?", ct).ConfigureAwait(false);
        if (cgatt is not null)
        {
            var m = Regex.Match(cgatt.RawText, @"\+CGATT:\s*(\d)");
            if (m.Success)
            {
                status.DataAttached = m.Groups[1].Value == "1";
            }
        }

        status.UpdatedAt = DateTime.Now;
        return status;
    }

    /// <summary>GSMA SGP.22 的 ISD-R 应用 AID（16 字节）。</summary>
    public const string IsdrAid = "A0000005591010FFFFFFFF8900000100";

    /// <summary>
    /// 探测成功后保留下来的逻辑通道客户端，供后续读取 Profile 等操作复用。
    /// 仅当探测结论为 Ready 时非空。
    /// </summary>
    public LogicalChannelEs10? Es10 { get; private set; }

    /// <summary>eUICC 探测的完整结果。</summary>
    /// <param name="Capability">结论。</param>
    /// <param name="Eid">EID（仅 Ready 时有值）。</param>
    /// <param name="Profiles">Profile 列表（仅 Ready 时有值，可能为空列表）。</param>
    /// <param name="Detail">供界面/日志展示的诊断细节。</param>
    public sealed record EuiccProbeResult(
        EuiccCapability Capability,
        string? Eid,
        IReadOnlyList<EsimProfileInfo> Profiles,
        string? Detail);

    /// <summary>
    /// 探测本模块读取 eUICC 的能力，成功时顺带读出 EID 与 Profile 列表。
    ///
    /// 通道选择（重要）：优先走 3GPP 逻辑通道指令（CCHO / CGLA）——
    /// CCHO 由模块固件内部完成 ISD-R 选择，完全避开本固件 AT+CSIM 透传的
    /// 约 8 字节长度上限。这正是 CellDock / lpac 在同一模块上验证可用的路径。
    /// 仅当固件不支持 CCHO/CGLA 时，才回退到 CSIM 路径做进一步诊断。
    /// </summary>
    public async Task<EuiccProbeResult> ProbeEuiccAsync(CancellationToken ct = default)
    {
        // ---------- 路径一：逻辑通道（CCHO / CGLA）----------
        var es10 = new LogicalChannelEs10(_engine);

        bool supported;
        try
        {
            supported = await es10.CheckSupportedAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            supported = false;
        }

        if (supported)
        {
            var channel = await es10.OpenIsdrAsync(ct: ct).ConfigureAwait(false);
            if (channel is int ch)
            {
                var eid = await es10.GetEidAsync(ct).ConfigureAwait(false);

                List<EsimProfileInfo> profiles = [];
                var profilesOk = false;
                try
                {
                    profiles = await es10.GetProfilesAsync(ct).ConfigureAwait(false) ?? [];
                    profilesOk = true;
                }
                catch
                {
                    // Profile 读取失败不算致命
                }

                // 判据：EID 或 Profile 任一可读即认为 eUICC 可用。
                // 实测部分卡（如 9eSIM）不支持 ES10c GetEuiccData，但 Profile 可读。
                if (!string.IsNullOrWhiteSpace(eid) || profilesOk)
                {
                    Es10 = es10;
                    return new EuiccProbeResult(
                        EuiccCapability.Ready, eid, profiles, $"逻辑通道 {ch}");
                }

                await es10.CloseAsync().ConfigureAwait(false);
                return new EuiccProbeResult(
                    EuiccCapability.IsdrNotFound, null, [],
                    $"CCHO 已打开逻辑通道 {ch}，但 EID 与 Profile 均读取失败 —— 卡内无 eUICC 应用或响应异常。");
            }

            await es10.CloseAsync().ConfigureAwait(false);
            return new EuiccProbeResult(
                EuiccCapability.IsdrNotFound, null, [],
                "AT+CCHO 未能为 ISD-R AID 打开逻辑通道 —— 卡内大概率没有 eUICC 应用。");
        }

        // ---------- 路径二：CSIM 诊断（仅在固件不支持逻辑通道时）----------
        var capability = await ProbeViaCsimAsync(ct).ConfigureAwait(false);
        return new EuiccProbeResult(capability, null, [],
            "固件不支持 CCHO/CGLA 逻辑通道指令，已回退 CSIM 诊断。");
    }

    /// <summary>
    /// 旧版 CSIM 路径的探测与诊断（仅在 CCHO/CGLA 不可用时被调用）。
    ///
    /// 实测限制（QDC507 固件 QDC507GLEFM21_01.001.02.001）：
    ///   AT+CSIM 透传通道对单条 APDU 的长度上限约为 16 个十六进制字符（8 字节），
    ///   实测 7 字节 → 9000、8 字节 → 可执行、9 字节及以上 → 无任何响应。
    ///   SELECT ISD-R 至少需要 10 字节，必然超出限制。
    /// </summary>
    private async Task<EuiccCapability> ProbeViaCsimAsync(CancellationToken ct)
    {
        // 1) 通道基线：SELECT MF（7 字节，实测可用）
        var mf = await TryAsync("AT+CSIM=14,\"00A40000023F00\"", ct).ConfigureAwait(false);
        if (mf is null || !mf.RawText.Contains("9000", StringComparison.OrdinalIgnoreCase))
        {
            return EuiccCapability.ChannelUnavailable;
        }

        // 2) 长度探针：8 字节（实测可执行）与 10 字节（实测沉默）
        //    用 SELECT MF 后补零的方式构造，语义上无害。
        var len8 = await TryAsync("AT+CSIM=16,\"00A40000023F0000\"", ct).ConfigureAwait(false);
        var len10 = await TryAsync("AT+CSIM=20,\"00A40000023F00000000\"", ct).ConfigureAwait(false);

        var ack8 = len8 is not null && len8.Lines.Count > 0;
        var ack10 = len10 is not null && len10.Lines.Count > 0;

        if (ack8 && !ack10)
        {
            // 通道能干活，但 8 字节以上被截断 —— ISD-R 的 16 字节 AID 不可能送达。
            return EuiccCapability.ApduLengthLimited;
        }

        // 3) 通道无长度问题，尝试真正的 SELECT ISD-R
        var apdu = "00A4040010" + IsdrAid;
        var sel = await TryAsync($"AT+CSIM={apdu.Length},\"{apdu}\"", ct, AtEngine.LongTimeout)
            .ConfigureAwait(false);

        if (sel is not null && sel.RawText.Contains("9000", StringComparison.OrdinalIgnoreCase))
        {
            return EuiccCapability.Ready;
        }

        return EuiccCapability.IsdrNotFound;
    }

    private async Task<AtResult?> TryAsync(string cmd, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            var r = await _engine.ExecuteAsync(cmd, timeout, ct).ConfigureAwait(false);
            return r.Succeeded ? r : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractAfterColon(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
        var idx = line.IndexOf(':');
        return idx >= 0 ? line[(idx + 1)..].Trim().Trim('"') : line.Trim().Trim('"');
    }

    private static string? ParseCnum(AtResult? result)
    {
        if (result is null)
        {
            return null;
        }

        // 常见返回共三种形态：
        //   +CNUM: "name","+8613800138000",145     ← 有名字
        //   +CNUM: ,"+8613800138000",145             ← 无名字（本模块实测）
        //   +CNUM: "+8613800138000",145              ← 仅号码
        // 统一策略：在该行内找出所有带引号的字段，取其中「看起来像号码」的那个。
        var line = result.LinesWithPrefix("+CNUM").FirstOrDefault()
                   ?? result.RawText;

        var quoted = Regex.Matches(line, "\"([^\"]+)\"");
        foreach (var q in quoted.Cast<Match>())
        {
            var value = q.Groups[1].Value;
            // 号码特征：含 + 或全数字，且长度 >= 5
            if (value.Length >= 5 &&
                (value.StartsWith('+') || value.All(char.IsDigit)))
            {
                return value;
            }
        }

        // 退化：匹配 ,"xxx",145 形式（无引号号码）
        var m = Regex.Match(line, @",\s*([+]?\d{5,})\s*,\s*\d+");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ParseReg(AtResult? result)
    {
        if (result is null)
        {
            return null;
        }
        var m = Regex.Match(result.RawText, @"\+CREG:\s*\d\s*,\s*(\d)");
        if (!m.Success)
        {
            return null;
        }
        return m.Groups[1].Value switch
        {
            "0" => "未注册",
            "1" => "已注册（本地）",
            "2" => "搜索中",
            "3" => "注册被拒",
            "4" => "未知",
            "5" => "已注册（漫游）",
            _ => null,
        };
    }
}
