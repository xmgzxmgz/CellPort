using System.Text;

namespace CellPort.Core.At;

/// <summary>
/// GSM 7-bit 与 UCS2 编解码工具，以及短信 PDU 的构造。用于 TEXT 模式的十六进制正文生成。
/// </summary>
public static class SmsCodec
{
    /// <summary>
    /// 判断文本是否包含 GSM 7-bit 字符集之外的字符（例如中文）。
    /// </summary>
    public static bool RequiresUcs2(string text) =>
        text.Any(c => c > 0x7F);

    /// <summary>
    /// 把文本编码为 AT 指令可用的十六进制串。
    /// 中文走 UCS2（UTF-16BE），纯 ASCII 走 GSM 7-bit。
    /// </summary>
    public static (string Hex, string Dcs) EncodeForText(string text, bool forceUcs2 = false)
    {
        if (forceUcs2 || RequiresUcs2(text))
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            return (Convert.ToHexString(bytes), "08");
        }

        var gsm = EncodeGsm7Bit(text);
        return (Convert.ToHexString(gsm), "00");
    }

    /// <summary>
    /// GSM 03.38 的 7-bit 打包编码（用于纯 ASCII 短信的紧凑传输）。
    /// </summary>
    public static byte[] EncodeGsm7Bit(string text)
    {
        var septets = new List<byte>(text.Length);
        foreach (var c in text)
        {
            septets.Add(Gsm7Lookup.TryGetValue(c, out var v) ? v : (byte)'?');
        }

        // 7-bit 打包：每 8 个 septet 压成 7 字节，最后不足时补位。
        var output = new List<byte>((septets.Count * 7 + 7) / 8);
        byte accumulator = 0;
        int bits = 0;

        foreach (var s in septets)
        {
            accumulator |= (byte)(s << bits);
            bits += 7;
            if (bits >= 8)
            {
                output.Add(accumulator);
                accumulator = (byte)(s >> (7 - (bits - 8)));
                bits -= 8;
            }
        }

        if (bits > 0)
        {
            output.Add(accumulator);
        }

        return [.. output];
    }

    /// <summary>
    /// 解码 GSM 7-bit 打包数据为文本。
    /// </summary>
    public static string DecodeGsm7Bit(ReadOnlySpan<byte> data, int septetCount = -1)
    {
        var count = septetCount > 0 ? septetCount : data.Length * 8 / 7;
        var sb = new StringBuilder(count);
        byte accumulator = 0;
        int bits = 0;
        int index = 0;

        for (int i = 0; i < count; i++)
        {
            while (bits < 7)
            {
                if (index >= data.Length)
                {
                    break;
                }
                accumulator |= (byte)(data[index++] << bits);
                bits += 8;
            }

            if (bits < 7)
            {
                break;
            }

            var septet = (byte)(accumulator & 0x7F);
            accumulator >>= 7;
            bits -= 7;

            sb.Append(ReverseGsm7Lookup.TryGetValue(septet, out var ch) ? ch : '?');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 解码 UCS2 十六进制正文。
    /// </summary>
    public static string DecodeUcs2(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        return Encoding.BigEndianUnicode.GetString(bytes);
    }

    /// <summary>
    /// 把号码格式化为 PDU 的 BCD 半字节表示。
    /// </summary>
    public static string EncodeNumberBcd(string number, bool international)
    {
        var digits = new StringBuilder();
        if (international)
        {
            digits.Append('9'); // 国际格式前缀
        }
        digits.Append(number.Replace("+", string.Empty));

        var s = digits.ToString();
        if (s.Length % 2 != 0)
        {
            s += "F";
        }

        var swapped = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i += 2)
        {
            swapped.Append(s[i + 1]).Append(s[i]);
        }
        return swapped.ToString();
    }

    private static readonly Dictionary<char, byte> Gsm7Lookup = BuildGsm7Table();
    private static readonly Dictionary<byte, char> ReverseGsm7Lookup =
        Gsm7Lookup.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    private static Dictionary<char, byte> BuildGsm7Table()
    {
        const string basic =
            "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ ÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
            "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
        var table = new Dictionary<char, byte>();
        for (int i = 0; i < basic.Length; i++)
        {
            table[basic[i]] = (byte)i;
        }
        return table;
    }
}
