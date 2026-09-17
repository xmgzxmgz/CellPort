using System.Text.RegularExpressions;
using CellPort.Core.At;

namespace CellPort.Core.Services;

/// <summary>
/// USSD 会话服务（AT+CUSD，3GPP TS 27.007）。
/// 典型用途：查话费 / 流量 / 余额（*100# 等由运营商决定）。
/// </summary>
public sealed class CusdService(AtEngine engine)
{
    private readonly AtEngine _engine = engine;

    /// <summary>一次 USSD 交互的结果。</summary>
    /// <param name="Ok">模块是否给出可用文本响应。</param>
    /// <param name="Text">解码后的文本（GSM 7-bit / UCS2 自动识别）。</param>
    /// <param name="Raw">+CUSD 原始行，供诊断。</param>
    public sealed record CusdResult(bool Ok, string? Text, string? Raw);

    /// <summary>
    /// 发起一次 USSD 请求并等待响应。仅处理首响应；
    /// 若返回 n=1（会话未结束），会在文本尾部提示继续方式。
    /// </summary>
    public async Task<CusdResult> SendAsync(string code, CancellationToken ct = default)
    {
        code = code.Trim();
        if (code.Length == 0)
        {
            return new CusdResult(false, null, "USSD 码为空");
        }

        // <m>=1 发起；<dcs>=15（GSM 默认字母表请求）。部分网络会忽略 DCS 请求，
        // 按自身选择返回，因此解码端做自动识别。
        AtResult? r;
        try
        {
            r = await _engine.ExecuteAsync(
                $@"AT+CUSD=1,""{code}"",15", AtEngine.LongTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CusdResult(false, null, ex.Message);
        }

        if (r is null)
        {
            return new CusdResult(false, null, "无响应");
        }

        var line = r.LinesWithPrefix("+CUSD").FirstOrDefault();
        if (line is null)
        {
            return new CusdResult(false, null,
                r.Succeeded ? "模块未返回 +CUSD（可能不支持 USSD 或未注网）" : r.RawText);
        }

        var m = Regex.Match(line, @"\+CUSD:\s*(\d)\s*,\s*""([^""]*)""(?:\s*,\s*(\d+))?");
        if (!m.Success)
        {
            return new CusdResult(false, null, line);
        }

        var status = int.Parse(m.Groups[1].Value);
        var payload = m.Groups[2].Value;
        var dcs = int.TryParse(m.Groups[3].Value, out var d) ? d : -1;

        var text = DecodePayload(payload, dcs);
        if (status == 1)
        {
            text += "\n（会话未结束，可在 AT 控制台用 AT+CUSD=1,\"<码>\",15 继续）";
        }

        // 0 = 正常结束；1 = 还有后续；2 = 会话被网络终止；4 = 网络不支持
        return new CusdResult(status is 0 or 1, text, line);
    }

    /// <summary>
    /// 解码 +CUSD 文本。DCS=72 → UCS2；「纯 hex 且成对」先按 GSM 7-bit 打包解码
    /// （EG25 + CUSD 的默认返回形式），若字节流呈「每两字节高位为 0」的 UCS2 特征
    /// 则改按 UCS2 解码；非 hex 原样返回。
    /// </summary>
    private static string? DecodePayload(string payload, int dcs)
    {
        if (payload.Length == 0)
        {
            return payload;
        }

        var isHex = payload.Length % 2 == 0 && payload.All(Uri.IsHexDigit);
        if (!isHex)
        {
            return payload;
        }

        try
        {
            var bytes = Convert.FromHexString(payload);
            if (dcs == 72)
            {
                return SmsCodec.DecodeUcs2(payload);
            }

            // UCS2 特征启发式：偶数下标字节全为 0x00（ASCII 区文本）
            var looksUcs2 = bytes.Length >= 4 && bytes.Length % 2 == 0;
            if (looksUcs2)
            {
                for (var i = 0; i < bytes.Length; i += 2)
                {
                    if (bytes[i] != 0x00)
                    {
                        looksUcs2 = false;
                        break;
                    }
                }
            }

            return looksUcs2
                ? SmsCodec.DecodeUcs2(payload)
                : SmsCodec.DecodeGsm7Bit(bytes);
        }
        catch
        {
            return payload;
        }
    }
}
