using System.Text.RegularExpressions;
using CellPort.Core.At;

namespace CellPort.Core.Services;

/// <summary>SIM 卡电话簿条目。</summary>
/// <param name="Index">SIM 存储槽位。</param>
/// <param name="Number">号码。</param>
/// <param name="Name">联系人名（UCS2 解码后的原文）。</param>
public sealed record PhonebookEntry(int Index, string Number, string Name);

/// <summary>
/// SIM 电话簿读取（AT+CPBS / AT+CPBR，3GPP TS 27.007）。只读，不修改 SIM 内容。
/// 中文联系人名以 UCS2 读取：临时切换 AT+CSCS="UCS2"，读完后恢复原字符集。
/// </summary>
public sealed class PhonebookService(AtEngine engine)
{
    private readonly AtEngine _engine = engine;

    /// <summary>SIM 电话簿容量信息。</summary>
    /// <param name="MaxNumberLength">号码最大长度（字符）。</param>
    /// <param name="MaxNameLength">名字最大长度（字符）。</param>
    public sealed record StorageInfo(int TotalSlots, int MaxNumberLength, int MaxNameLength);

    /// <summary>读取电话簿容量。SIM 不支持时返回 null。</summary>
    public async Task<StorageInfo?> GetStorageInfoAsync(CancellationToken ct = default)
    {
        if (await ExecAsync("AT+CPBS=\"SM\"", ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var q = await ExecAsync("AT+CPBR=?", ct).ConfigureAwait(false);
        if (q is null)
        {
            return null;
        }

        var m = Regex.Match(q.RawText, @"\+CPBR:\s*\(\s*1\s*-\s*(\d+)\s*\)\s*,\s*(\d+)\s*,\s*(\d+)");
        return !m.Success
            ? null
            : new StorageInfo(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value));
    }

    /// <summary>读取全部电话簿条目。读取失败时返回已解析的部分（或空列表）。</summary>
    public async Task<List<PhonebookEntry>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<PhonebookEntry>();

        if (await ExecAsync("AT+CPBS=\"SM\"", ct).ConfigureAwait(false) is null)
        {
            return list;
        }

        var cap = await ExecAsync("AT+CPBR=?", ct).ConfigureAwait(false);
        var m = Regex.Match(cap?.RawText ?? string.Empty, @"\+CPBR:\s*\(\s*1\s*-\s*(\d+)\s*\)");
        var max = m.Success ? int.Parse(m.Groups[1].Value) : 250;

        // 记住原字符集 → 切 UCS2（中文联系人名可读）→ 读完恢复
        var saved = "GSM";
        var csReply = await ExecAsync("AT+CSCS?", ct).ConfigureAwait(false);
        var csLine = csReply?.LinesWithPrefix("+CSCS").FirstOrDefault();
        if (csLine is not null)
        {
            var cm = Regex.Match(csLine, @"""([^""]+)""");
            if (cm.Success)
            {
                saved = cm.Groups[1].Value;
            }
        }

        try
        {
            await ExecAsync(@"AT+CSCS=""UCS2""", ct).ConfigureAwait(false);

            var r = await ExecAsync($"AT+CPBR=1,{max}", ct, AtEngine.LongTimeout)
                .ConfigureAwait(false);
            if (r is not null)
            {
                foreach (var line in r.LinesWithPrefix("+CPBR"))
                {
                    var e = ParseEntry(line);
                    if (e is not null)
                    {
                        list.Add(e);
                    }
                }
            }
        }
        finally
        {
            await ExecAsync($@"AT+CSCS=""{saved}""", ct).ConfigureAwait(false);
        }

        return list.OrderBy(e => e.Index).ToList();
    }

    /// <summary>解析 +CPBR: &lt;i&gt;,"&lt;number&gt;",&lt;type&gt;,"&lt;name&gt;"（UCS2 字符集下文本为 hex）。</summary>
    private static PhonebookEntry? ParseEntry(string line)
    {
        var m = Regex.Match(
            line,
            @"\+CPBR:\s*(\d+)\s*,\s*""([^""]*)""\s*,\s*(\d+)\s*,\s*""([^""]*)""");
        return !m.Success
            ? null
            : new PhonebookEntry(
                int.Parse(m.Groups[1].Value),
                DecodeUcs2IfHex(m.Groups[2].Value),
                DecodeUcs2IfHex(m.Groups[4].Value));
    }

    /// <summary>UCS2 字符集下字符串以 hex 呈现；对不符合特征的值原样返回（防御异厂固件）。</summary>
    private static string DecodeUcs2IfHex(string s)
    {
        if (s.Length >= 4 && s.Length % 4 == 0 && s.All(Uri.IsHexDigit))
        {
            try
            {
                var decoded = SmsCodec.DecodeUcs2(s);
                if (decoded.All(c => !char.IsControl(c)))
                {
                    return decoded;
                }
            }
            catch
            {
                // 解码失败按原样
            }
        }
        return s;
    }

    private async Task<AtResult?> ExecAsync(string cmd, CancellationToken ct, TimeSpan? timeout = null)
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
}
