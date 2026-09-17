using Xunit;
using CellPort.Core.At;
using CellPort.Core.Services;

namespace CellPort.Core.Tests;

public class SmsExportTests
{
    private static SmsMessage Make(
        string number, string body, DateTime ts,
        SmsDirection dir = SmsDirection.Incoming, string? code = null) =>
        new()
        {
            Number = number,
            Body = body,
            Timestamp = ts,
            Direction = dir,
            Status = SmsStatus.Read,
            VerificationCode = code,
        };

    [Fact]
    public void Csv_EscapesCommasQuotesAndNewlines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cellport-test-{Guid.NewGuid():N}.csv");
        try
        {
            var messages = new List<SmsMessage>
            {
                Make("+8613800138000", "他说：\"你好\"\n第二行", new DateTime(2026, 9, 17, 12, 30, 0)),
                Make("10086", "余额,含逗号", new DateTime(2026, 9, 17, 13, 0, 0), SmsDirection.Outgoing, "123456"),
            };

            SmsExportService.ExportCsv(messages, path);
            var text = File.ReadAllText(path);

            Assert.StartsWith("时间,方向,号码,验证码,正文", text);
            Assert.Contains("\"他说：\"\"你好\"\"\n第二行\"", text); // 引号翻倍 + 整体加引号
            Assert.Contains("收", text);
            Assert.Contains("发", text);
            Assert.Contains("123456", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Json_ProducesReadableChineseAndFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cellport-test-{Guid.NewGuid():N}.json");
        try
        {
            var messages = new List<SmsMessage>
            {
                Make("10010", "验证码 654321", new DateTime(2026, 9, 17, 8, 0, 0), code: "654321"),
            };

            SmsExportService.ExportJson(messages, path);
            var text = File.ReadAllText(path);

            Assert.Contains("\"number\": \"10010\"", text);
            Assert.Contains("\"code\": \"654321\"", text);
            Assert.Contains("验证码 654321", text);       // 中文不转义
            Assert.Contains("\"direction\": \"in\"", text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
