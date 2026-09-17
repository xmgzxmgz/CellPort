using Xunit;
using CellPort.Core.At;

namespace CellPort.Core.Tests;

/// <summary>GSM 7-bit / UCS2 编解码与 BCD 号码编码的标准向量与往返测试。</summary>
public class SmsCodecTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("hello hello")]
    [InlineData("The quick brown fox jumps over the lazy dog 0123456789")]
    [InlineData("OTP 123456 is your code.")]
    public void Gsm7Bit_RoundTrip(string text)
    {
        var encoded = SmsCodec.EncodeGsm7Bit(text);
        var decoded = SmsCodec.DecodeGsm7Bit(encoded, text.Length);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void Gsm7Bit_HelloHello_StandardVector()
    {
        // 3GPP TS 23.038 官方示例："hellohello"（10 字符，无空格）→ 9 字节
        Assert.Equal(
            "E8329BFD4697D9EC37",
            Convert.ToHexString(SmsCodec.EncodeGsm7Bit("hellohello")));
    }

    [Fact]
    public void Gsm7Bit_Ab12_StandardVector()
    {
        // 手算：A=0x41 B=0x42 1=0x31 2=0x32 → 打包 41 61 4C 06
        Assert.Equal(
            new byte[] { 0x41, 0x61, 0x4C, 0x06 },
            SmsCodec.EncodeGsm7Bit("AB12"));
    }

    [Fact]
    public void RequiresUcs2_DetectsNonAscii()
    {
        Assert.False(SmsCodec.RequiresUcs2("Hello 123"));
        Assert.True(SmsCodec.RequiresUcs2("你好"));
        Assert.True(SmsCodec.RequiresUcs2("café"));
    }

    [Fact]
    public void EncodeForText_Chinese_UsesUcs2()
    {
        var (hex, dcs) = SmsCodec.EncodeForText("你好");
        Assert.Equal("08", dcs);
        Assert.Equal("4F60597D", hex);
        Assert.Equal("你好", SmsCodec.DecodeUcs2(hex));
    }

    [Fact]
    public void EncodeForText_Ascii_UsesGsm7()
    {
        var (hex, dcs) = SmsCodec.EncodeForText("AB12");
        Assert.Equal("00", dcs);
        Assert.Equal("41614C06", hex);
    }

    [Theory]
    [InlineData("13800138000", false, "3108108300F0")]
    [InlineData("+8613800138000", true, "89168300310800")]
    public void EncodeNumberBcd_SwapsNibbles(string number, bool international, string expected)
    {
        // 半字节交换 + 奇数位补 F；international 会补 '9' 前缀（TOA 由 PDU 头单独携带）。
        var actual = SmsCodec.EncodeNumberBcd(number, international);
        Assert.Equal(expected, actual);
    }
}
