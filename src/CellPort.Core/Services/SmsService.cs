using System.Text.RegularExpressions;
using CellPort.Core.At;

namespace CellPort.Core.Services;

/// <summary>
/// 短信收发服务。负责初始化短信模式、轮询收件、发送短信、拼接长短信。
/// </summary>
public sealed class SmsService(AtEngine engine)
{
    private readonly AtEngine _engine = engine;
    private readonly List<SmsMessage> _inbox = [];
    private readonly Dictionary<(string Number, int Ref), List<SmsMessage>> _pendingConcat = [];
    private readonly object _lock = new();

    /// <summary>收到一条新短信（含拼接完成的长短信）时触发。</summary>
    public event EventHandler<SmsMessage>? MessageReceived;

    /// <summary>当前收件箱快照。</summary>
    public IReadOnlyList<SmsMessage> Inbox
    {
        get
        {
            lock (_lock)
            {
                return [.. _inbox.OrderByDescending(m => m.Timestamp ?? DateTime.MinValue)];
            }
        }
    }

    /// <summary>
    /// 初始化短信子系统：文本模式、PDU 模式（收）、新消息提示。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // 使用 PDU 模式以便完整解码中文与长短信。
        await SafeAsync("AT+CMGF=0", ct).ConfigureAwait(false);
        // 新短信直接上报（+CMT），无需频繁轮询。
        await SafeAsync("AT+CNMI=2,1,0,0,0", ct).ConfigureAwait(false);
        // 选择 SIM 卡存储。
        await SafeAsync("AT+CPMS=\"SM\",\"SM\",\"SM\"", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 从模块读取已存储的短信（首次同步用）。
    /// </summary>
    public async Task<int> SyncStoredAsync(CancellationToken ct = default)
    {
        // 0 = 未读，1 = 已读，2/3 = 未发/已发，4 = 全部
        var result = await SafeAsync("AT+CMGL=4", ct, AtEngine.LongTimeout).ConfigureAwait(false);
        if (result is null)
        {
            return 0;
        }

        var added = 0;
        var text = result.RawText;
        // 逐条匹配 +CMGL: <index>,<stat>,"",<len>\r<pdu>
        var matches = Regex.Matches(
            text,
            @"\+CMGL:\s*(\d+)\s*,\s*(\d+)\s*,[^,]*,\s*(\d+)\s*\r?\n([0-9A-Fa-f]+)");

        foreach (Match m in matches)
        {
            var index = int.Parse(m.Groups[1].Value);
            var stat = int.Parse(m.Groups[2].Value);
            var pdu = m.Groups[4].Value;

            var msg = SmsPduParser.Parse(pdu);
            if (msg is null)
            {
                continue;
            }

            msg.Index = index;
            msg.Status = stat switch
            {
                0 => SmsStatus.Unread,
                1 => SmsStatus.Read,
                2 => SmsStatus.Unsent,
                3 => SmsStatus.Sent,
                _ => SmsStatus.Read,
            };
            msg.Direction = stat is 2 or 3 ? SmsDirection.Outgoing : SmsDirection.Incoming;

            if (Accept(msg))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// 处理一条主动上报的 +CMT 行（新短信到达）。
    /// </summary>
    public bool HandleUnsolicited(string line)
    {
        if (!line.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "+CMT: ,<len>" 之后的下一行是 PDU，但 AtEngine 已把它拆成两行，
        // 这里先缓冲，由调用方把后续行一并传入。
        _lastCmtHeader = line;
        return true;
    }

    private string? _lastCmtHeader;

    /// <summary>
    /// 把一行原始文本喂给短信解析器。自行判断该行是 PDU 还是头。
    /// </summary>
    public SmsMessage? FeedLine(string line)
    {
        if (line.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("+CMTI:", StringComparison.OrdinalIgnoreCase))
        {
            _lastCmtHeader = line;
            return null;
        }

        // +CMTI: "SM",<index> —— 有新短信存入，需要去读
        if (_lastCmtHeader?.StartsWith("+CMTI:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var m = Regex.Match(_lastCmtHeader, @"(\d+)\s*$");
            _lastCmtHeader = null;
            if (m.Success)
            {
                _ = ReadByIndexAsync(int.Parse(m.Groups[1].Value));
            }
            return null;
        }

        // +CMT 后面跟的一行应是 PDU
        if (_lastCmtHeader?.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase) == true &&
            IsHex(line))
        {
            _lastCmtHeader = null;
            var msg = SmsPduParser.Parse(line);
            if (msg is not null && Accept(msg))
            {
                MessageReceived?.Invoke(this, msg);
                return msg;
            }
        }

        return null;
    }

    private async Task ReadByIndexAsync(int index)
    {
        var r = await SafeAsync($"AT+CMGR={index}", null, AtEngine.DefaultTimeout).ConfigureAwait(false);
        if (r is null)
        {
            return;
        }

        var m = Regex.Match(r.RawText, @"\+CMGR:[^\r\n]*\r?\n([0-9A-Fa-f]+)");
        if (!m.Success)
        {
            return;
        }

        var msg = SmsPduParser.Parse(m.Groups[1].Value);
        if (msg is null)
        {
            return;
        }

        msg.Index = index;
        if (Accept(msg))
        {
            MessageReceived?.Invoke(this, msg);
        }
    }

    /// <summary>
    /// 发送短信。自动选择 UCS2 或 GSM 7-bit 编码。
    /// </summary>
    /// <param name="number">目标号码，可带 + 前缀。</param>
    /// <param name="body">正文。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<bool> SendAsync(string number, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var normalized = NormalizeNumber(number);
        var (hex, dcs) = SmsCodec.EncodeForText(body);

        // 构造 SMS-SUBMIT PDU（SMSC 长度置 0，使用模块默认短信中心）。
        var pdu = BuildPdu(normalized, dcs, hex, hex.Length / 2);

        // TPDU 长度 = PDU 总字节数 - 1（SMSC 长度字节）
        var tpduLength = (pdu.Length / 2) - 1;

        // 切到 PDU 模式发送
        await SafeAsync("AT+CMGF=0", ct).ConfigureAwait(false);

        await SafeAsync($"AT+CMGS={tpduLength}", ct, AtEngine.DefaultTimeout).ConfigureAwait(false);

        // CMGS 执行后会返回 ">" 提示符，随后写入 PDU 并以 Ctrl+Z 结束。
        var sendResult = await SafeAsync($"{pdu}\u001A", ct, AtEngine.LongTimeout).ConfigureAwait(false);
        var ok = sendResult?.Succeeded == true;

        if (ok)
        {
            var msg = new SmsMessage
            {
                Number = normalized,
                Body = body,
                Direction = SmsDirection.Outgoing,
                Status = SmsStatus.Sent,
                Timestamp = DateTime.Now,
                VerificationCode = SmsPduParser.ExtractVerificationCode(body),
            };
            lock (_lock)
            {
                _inbox.Add(msg);
            }
        }

        return ok;
    }

    /// <summary>
    /// 构造完整的 SMS-SUBMIT PDU（SMSC 长度置 0，使用模块默认短信中心）。
    /// </summary>
    private static string BuildPdu(string number, string dcs, string userDataHex, int udOctets)
    {
        var international = number.StartsWith('+');
        var digits = number.TrimStart('+');
        var addrBcd = SmsCodec.EncodeNumberBcd(digits, international);
        var addrLen = digits.Length; // 十进制位数（不含国际前缀）
        var toa = international ? "91" : "81";

        return "00"                                  // SMSC 信息长度 = 0
             + "11"                                  // SMS-SUBMIT + UDHI 关闭 + 请求状态报告关闭
             + $"{addrLen:X2}"                       // 目标地址位数
             + toa                                   // 号码类型
             + addrBcd                               // BCD 号码
             + "00"                                  // PID
             + dcs                                   // DCS
             + "00"                                  // 相对有效期
             + $"{udOctets:X2}"                      // UD 长度
             + userDataHex;                          // UD
    }

    private static string NormalizeNumber(string number) =>
        new([.. number.Where(c => char.IsDigit(c) || c == '+')]);

    private static bool IsHex(string s) =>
        s.Length >= 10 && s.Length % 2 == 0 && s.All(Uri.IsHexDigit);

    /// <summary>
    /// 接收一条短信；长短信在此拼接。
    /// </summary>
    private bool Accept(SmsMessage msg)
    {
        lock (_lock)
        {
            if (!msg.IsConcatPart)
            {
                // 普通短信：按 号码+正文+时间 去重
                if (_inbox.Any(m => m.Number == msg.Number && m.Body == msg.Body &&
                                    m.Timestamp.HasValue && msg.Timestamp.HasValue &&
                                    Math.Abs((m.Timestamp.Value - msg.Timestamp.Value).TotalMinutes) < 1))
                {
                    return false;
                }
                _inbox.Add(msg);
                return true;
            }

            var key = (msg.Number, msg.ConcatReference ?? 0);
            if (!_pendingConcat.TryGetValue(key, out var parts))
            {
                parts = [];
                _pendingConcat[key] = parts;
            }

            // 同段号去重
            if (parts.Any(p => p.ConcatPart == msg.ConcatPart))
            {
                return false;
            }

            parts.Add(msg);
            if (parts.Count < (msg.ConcatTotal ?? 1))
            {
                return false; // 还没收齐
            }

            // 收齐了，按段号拼接
            var ordered = parts.OrderBy(p => p.ConcatPart).ToList();
            var merged = new SmsMessage
            {
                Number = msg.Number,
                Body = string.Concat(ordered.Select(p => p.Body)),
                Timestamp = ordered[0].Timestamp,
                Direction = msg.Direction,
                Status = msg.Status,
                Index = msg.Index,
                ConcatReference = msg.ConcatReference,
                ConcatTotal = msg.ConcatTotal,
                ConcatPart = null,
            };
            merged.VerificationCode = SmsPduParser.ExtractVerificationCode(merged.Body);

            _pendingConcat.Remove(key);
            _inbox.Add(merged);
            return true;
        }
    }

    private async Task<AtResult?> SafeAsync(string cmd, CancellationToken? ct, TimeSpan? timeout = null)
    {
        try
        {
            var r = await _engine.ExecuteAsync(cmd, timeout, ct ?? CancellationToken.None)
                .ConfigureAwait(false);
            return r.Succeeded ? r : null;
        }
        catch
        {
            return null;
        }
    }
}
