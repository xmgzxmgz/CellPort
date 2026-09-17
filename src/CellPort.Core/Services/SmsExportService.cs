using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CellPort.Core.At;

namespace CellPort.Core.Services;

/// <summary>
/// 短信导出：CSV（Excel 友好，UTF-8 BOM）与 JSON。
/// </summary>
public static class SmsExportService
{
    /// <summary>导出为 CSV。字段：时间 / 方向 / 号码 / 验证码 / 正文。</summary>
    public static void ExportCsv(IReadOnlyList<SmsMessage> messages, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,方向,号码,验证码,正文");
        foreach (var m in messages)
        {
            sb.AppendLine(string.Join(
                ",",
                Csv(m.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty),
                Csv(m.Direction == SmsDirection.Incoming ? "收" : "发"),
                Csv(m.Number),
                Csv(m.VerificationCode ?? string.Empty),
                Csv(m.Body)));
        }

        // UTF-8 BOM：让 Excel 直接双击打开不乱码
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>导出为 JSON（数组，字段 timestamp / direction / number / code / body）。</summary>
    public static void ExportJson(IReadOnlyList<SmsMessage> messages, string path)
    {
        var rows = messages.Select(m => new
        {
            timestamp = m.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss"),
            direction = m.Direction == SmsDirection.Incoming ? "in" : "out",
            number = m.Number,
            code = m.VerificationCode,
            body = m.Body,
        });

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义为 \uXXXX
        };
        File.WriteAllText(path, JsonSerializer.Serialize(rows, options), new UTF8Encoding(false));
    }

    /// <summary>RFC 4180 CSV 字段转义：含逗号 / 引号 / 换行时加引号，内部引号翻倍。</summary>
    private static string Csv(string s)
    {
        var escaped = s.Replace("\"", "\"\"");
        var needQuote = escaped.Contains(',') || escaped.Contains('"') ||
                        escaped.Contains('\n') || escaped.Contains('\r');
        return needQuote ? $"\"{escaped}\"" : escaped;
    }
}
