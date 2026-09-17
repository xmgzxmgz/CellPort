using System.Globalization;
using System.Text;

namespace CellPort.Core.At;

/// <summary>解码后的一条短信。</summary>
public sealed class SmsMessage
{
    /// <summary>模块存储索引，用于删除。</summary>
    public int Index { get; set; } = -1;

    /// <summary>对端号码。</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>正文。</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>服务中心时间戳。</summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>方向。</summary>
    public SmsDirection Direction { get; set; } = SmsDirection.Incoming;

    /// <summary>状态。</summary>
    public SmsStatus Status { get; set; } = SmsStatus.Unread;

    /// <summary>长短信参考号；多条同参考号的短信属于同一会话。</summary>
    public int? ConcatReference { get; set; }

    /// <summary>长短信总段数。</summary>
    public int? ConcatTotal { get; set; }

    /// <summary>长短信当前段号（从 1 开始）。</summary>
    public int? ConcatPart { get; set; }

    /// <summary>是否为长短信的一段。</summary>
    public bool IsConcatPart => ConcatTotal is > 1 && ConcatPart is >= 1;

    /// <summary>提取到的验证码（若有）。</summary>
    public string? VerificationCode { get; set; }

    public override string ToString() =>
        $"[{(Direction == SmsDirection.Incoming ? "IN" : "OUT")}] {Number}: {Truncate(Body, 40)}";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}

/// <summary>短信方向。</summary>
public enum SmsDirection
{
    /// <summary>收到。</summary>
    Incoming,
    /// <summary>发出。</summary>
    Outgoing,
}

/// <summary>短信状态。</summary>
public enum SmsStatus
{
    /// <summary>未读。</summary>
    Unread,
    /// <summary>已读。</summary>
    Read,
    /// <summary>未发送。</summary>
    Unsent,
    /// <summary>已发送。</summary>
    Sent,
}

/// <summary>
/// 短信 PDU 解析器。支持 DELIVER（接收）报文的完整解码，含 UCS2、GSM 7-bit 与长短信 UDH。
/// </summary>
public static class SmsPduParser
{
    /// <summary>
    /// 解析一条 +CMGL / +CMGR 返回的 PDU 字符串。
    /// </summary>
    /// <param name="pdu">十六进制 PDU（不含首尾引号）。</param>
    /// <returns>解析出的短信；失败返回 null。</returns>
    public static SmsMessage? Parse(string pdu)
    {
        try
        {
            var bytes = Convert.FromHexString(pdu.Trim().Trim('"'));
            var reader = new PduReader(bytes);

            // 第一字节：SMSC 地址长度（以字节计）
            var smscLen = reader.ReadByte();
            reader.Skip(smscLen);
            if (smscLen == 0)
            {
                reader.Skip(0);
            }
            // 注意：smscLen 已包含 type-of-address 字节，直接跳过即可。

            var firstOctet = reader.ReadByte();
            var hasUdh = (firstOctet & 0x40) != 0;         // TP-UDHI
            var isStatusReport = (firstOctet & 0x02) != 0; // TP-SRI（状态报告）

            // 对端地址
            var number = ReadAddress(reader, out _);

            if (isStatusReport)
            {
                return null; // 状态报告暂不展开
            }

            var pid = reader.ReadByte();
            var dcs = reader.ReadByte();

            // 时间戳（7 字节 BCD）
            var timestamp = ReadTimestamp(reader);

            var udLength = reader.ReadByte();
            var ud = reader.ReadRemaining();

            // UDH 处理
            int? concatRef = null, concatTotal = null, concatPart = null;
            if (hasUdh && ud.Length > 1)
            {
                var udhLen = ud[0];
                var udh = ud.AsSpan(1, Math.Min(udhLen, ud.Length - 1));
                ParseUdh(udh, ref concatRef, ref concatTotal, ref concatPart);
                ud = ud[(1 + udhLen)..];
            }

            var body = DecodeUserData(ud, dcs, udLength, hasUdh);

            return new SmsMessage
            {
                Number = number,
                Body = body,
                Timestamp = timestamp,
                Direction = SmsDirection.Incoming,
                Status = SmsStatus.Unread,
                ConcatReference = concatRef,
                ConcatTotal = concatTotal,
                ConcatPart = concatPart,
                VerificationCode = ExtractVerificationCode(body),
            };
        }
        catch
        {
            return null;
        }
    }

    private static void ParseUdh(
        ReadOnlySpan<byte> udh,
        ref int? concatRef,
        ref int? concatTotal,
        ref int? concatPart)
    {
        int i = 0;
        while (i + 1 < udh.Length)
        {
            var iei = udh[i];
            var ieiLen = udh[i + 1];
            var dataStart = i + 2;
            if (dataStart + ieiLen > udh.Length)
            {
                break;
            }

            // IEI 0x00：8-bit 参考号；IEI 0x08：16-bit 参考号
            if (iei == 0x00 && ieiLen == 3)
            {
                concatRef = udh[dataStart];
                concatTotal = udh[dataStart + 1];
                concatPart = udh[dataStart + 2];
            }
            else if (iei == 0x08 && ieiLen == 4)
            {
                concatRef = (udh[dataStart] << 8) | udh[dataStart + 1];
                concatTotal = udh[dataStart + 2];
                concatPart = udh[dataStart + 3];
            }

            i = dataStart + ieiLen;
        }
    }

    private static string DecodeUserData(byte[] ud, byte dcs, int udLength, bool hasUdh)
    {
        var encoding = (dcs >> 2) & 0x03; // 00=7bit, 01=8bit, 10=UCS2

        if (encoding == 0x02)
        {
            // UCS2
            var count = Math.Min(ud.Length, udLength);
            if (count % 2 != 0)
            {
                count--;
            }
            return count <= 0 ? string.Empty : Encoding.BigEndianUnicode.GetString(ud, 0, count);
        }

        if (encoding == 0x01)
        {
            var count = Math.Min(ud.Length, udLength);
            return Encoding.Latin1.GetString(ud, 0, count);
        }

        // GSM 7-bit：UDL 对含 UDH 的报文中是「总 septet 数」，需扣除 UDH 占用的 septet。
        var udhSeptets = 0;
        if (hasUdh && ud.Length > 0)
        {
            udhSeptets = (ud[0] + 1) * 8 / 7 + (((ud[0] + 1) * 8 % 7) != 0 ? 1 : 0);
        }
        var septetCount = udLength - udhSeptets;
        return SmsCodec.DecodeGsm7Bit(ud, septetCount);
    }

    private static string ReadAddress(PduReader reader, out byte typeOfAddress)
    {
        var addrLen = reader.ReadByte();
        typeOfAddress = reader.ReadByte();
        var digitCount = addrLen - 1;
        if (digitCount <= 0)
        {
            return string.Empty;
        }

        var byteCount = (digitCount + 1) / 2;
        var raw = reader.ReadBytes(byteCount);

        var sb = new StringBuilder(digitCount);
        foreach (var b in raw)
        {
            sb.Append(NibbleToChar(b & 0x0F));
            sb.Append(NibbleToChar((b >> 4) & 0x0F));
        }

        var digits = sb.ToString().TrimEnd('F', 'f')[..Math.Min(digitCount, sb.Length)];

        // 0x91 = 国际格式
        var isInternational = (typeOfAddress & 0x70) == 0x10;
        return isInternational ? "+" + digits : digits;
    }

    private static char NibbleToChar(int n) =>
        n switch
        {
            >= 0 and <= 9 => (char)('0' + n),
            0x0A => '*',
            0x0B => '#',
            0x0C => 'a',
            0x0D => 'b',
            0x0E => 'c',
            _ => 'F',
        };

    private static DateTime? ReadTimestamp(PduReader reader)
    {
        try
        {
            var raw = reader.ReadBytes(7);
            int Dec(int b) => ((b & 0x0F) * 10) + ((b >> 4) & 0x0F);

            var year = Dec(raw[0]);
            var month = Dec(raw[1]);
            var day = Dec(raw[2]);
            var hour = Dec(raw[3]);
            var minute = Dec(raw[4]);
            var second = Dec(raw[5]);

            // 时区：15 分钟为单位，含符号位（补码形式）
            var tzRaw = Dec(raw[6]);
            var tzQuarters = tzRaw & 0x7F;
            var negative = (tzRaw & 0x80) != 0;
            var offsetMinutes = tzQuarters * 15 * (negative ? -1 : 1);

            var fullYear = year < 70 ? 2000 + year : 1900 + year;
            if (month is < 1 or > 12 || day is < 1 or > 31)
            {
                return null;
            }

            var local = new DateTime(fullYear, month, day, hour, minute,
                Math.Min(second, 59), DateTimeKind.Unspecified);
            var utc = local.AddMinutes(-offsetMinutes);
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从短信正文中提取验证码。
    /// </summary>
    public static string? ExtractVerificationCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // 优先匹配「验证码/校验码/动态码/code」附近的 4-8 位数字
        var keywordPattern =
            @"(?:验证码|校验码|动态码|确认码|短信码|verification\s*code|verify\s*code|code)\D{0,12}(\d{4,8})";
        var m = System.Text.RegularExpressions.Regex.Match(
            body, keywordPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        // 退化：独立出现的 4-6 位数字，且正文较短
        if (body.Length <= 120)
        {
            var m2 = System.Text.RegularExpressions.Regex.Match(body, @"(?<!\d)(\d{4,6})(?!\d)");
            if (m2.Success)
            {
                return m2.Groups[1].Value;
            }
        }

        return null;
    }
}

/// <summary>PDU 字节流读取器。</summary>
internal ref struct PduReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos = 0;

    public byte ReadByte()
    {
        if (_pos >= _data.Length)
        {
            throw new InvalidOperationException("PDU 数据不足。");
        }
        return _data[_pos++];
    }

    public byte[] ReadBytes(int count)
    {
        var n = Math.Min(count, _data.Length - _pos);
        var result = _data.Slice(_pos, n).ToArray();
        _pos += n;
        return result;
    }

    public byte[] ReadRemaining()
    {
        var result = _data[_pos..].ToArray();
        _pos = _data.Length;
        return result;
    }

    public void Skip(int count) => _pos = Math.Min(_pos + count, _data.Length);
}
