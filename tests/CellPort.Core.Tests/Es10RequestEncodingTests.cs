using Xunit;
using CellPort.Core.Services;

namespace CellPort.Core.Tests;

/// <summary>
/// ES10c 请求报文编码的逐字节验证（与 lpac es10c.c 对齐）。
/// 标准 ISD-P AID：A0000005591010FFFFFFFF8900000100（16 字节）。
/// </summary>
public class Es10RequestEncodingTests
{
    private const string Aid = "A0000005591010FFFFFFFF8900000100";

    private static readonly byte[] AidBytes =
    [
        0xA0, 0x00, 0x00, 0x05, 0x59, 0x10, 0x10, 0xFF,
        0xFF, 0xFF, 0xFF, 0x89, 0x00, 0x00, 0x01, 0x00,
    ];

    [Fact]
    public void EnableProfile_MatchesLpacEncoding()
    {
        // BF31 <len> A0 <len> 4F 10 <aid16> 81 01 00
        var req = LogicalChannelEs10.BuildProfileStateRequest(enable: true, Aid);

        Assert.Equal(0xBF, req[0]);
        Assert.Equal(0x31, req[1]);
        Assert.Equal(0x17, req[2]);   // 总内容 = A0 TLV(2+21) = 23
        Assert.Equal(0xA0, req[3]);
        Assert.Equal(0x15, req[4]);   // A0 内容 = 4F TLV(18) + 3 = 21
        Assert.Equal(0x4F, req[5]);   // tag ISD-P AID
        Assert.Equal(0x10, req[6]);   // AID 长度 16
        Assert.Equal(AidBytes, req[7..23]);
        Assert.Equal(new byte[] { 0x81, 0x01, 0x00 }, req[23..]); // refreshFlag = FALSE
        Assert.Equal(26, req.Length);
    }

    [Fact]
    public void DisableProfile_UsesBf32()
    {
        var req = LogicalChannelEs10.BuildProfileStateRequest(enable: false, Aid);
        Assert.Equal(0x32, req[1]);
        // 其余结构与 enable 完全一致
        var en = LogicalChannelEs10.BuildProfileStateRequest(enable: true, Aid);
        for (var i = 0; i < req.Length; i++)
        {
            if (i == 1)
            {
                continue;
            }

            Assert.Equal(en[i], req[i]);
        }
    }

    [Fact]
    public void DeleteProfile_NoA0Wrapper_NoRefreshFlag()
    {
        // BF33 <len> 4F 10 <aid16> —— lpac delete 以 refreshFlag=0 走「无包装」分支
        var req = LogicalChannelEs10.BuildDeleteProfileRequest(Aid);

        Assert.Equal(0xBF, req[0]);
        Assert.Equal(0x33, req[1]);
        Assert.Equal(0x12, req[2]);   // 内容 = 4F TLV = 2 + 16 = 18
        Assert.Equal(0x4F, req[3]);
        Assert.Equal(0x10, req[4]);
        Assert.Equal(AidBytes, req[5..]);
        Assert.Equal(21, req.Length); // 无 81 01 00 尾巴
    }

    [Fact]
    public void DeleteProfile_ByIccid_UsesTag5A()
    {
        // 非 32 字符 id 按 ICCID 处理：BF33 <len> 5A 0A <bcd10>
        var req = LogicalChannelEs10.BuildDeleteProfileRequest("89860112345678901234");

        Assert.Equal(0xBF, req[0]);
        Assert.Equal(0x33, req[1]);
        Assert.Equal(12, req[2]);
        Assert.Equal(0x5A, req[3]);
        Assert.Equal(0x0A, req[4]);
        Assert.Equal(10, req.Length - 5);
    }

    [Fact]
    public void SetNickname_MatchesLpacEncoding()
    {
        // BF29 <len> 5A 0A <iccid bcd 10> 90 08 "CellPort"
        var req = LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", "CellPort");

        Assert.Equal(0xBF, req[0]);
        Assert.Equal(0x29, req[1]);
        Assert.Equal(14 + 8, req[2]);   // 5A TLV(12) + 90 TLV(2+8)
        Assert.Equal(0x5A, req[3]);
        Assert.Equal(0x0A, req[4]);
        Assert.Equal(25, req.Length);   // 5(头) + 10(ICCID BCD) + 2(90+len) + 8(昵称)
        // ICCID BCD：低 nibble 在前，19 位补 F
        Assert.Equal(
            new byte[] { 0x98, 0x68, 0x10, 0x21, 0x43, 0x65, 0x87, 0x09, 0x21, 0xF3 },
            req[5..15]);
        Assert.Equal(0x90, req[15]);  // nickname tag
        Assert.Equal(0x08, req[16]);  // nickname 长度
        Assert.Equal("CellPort", System.Text.Encoding.ASCII.GetString(req[17..]));
    }

    [Fact]
    public void SetNickname_EmptyNickname_EncodesLen0()
    {
        // 90 00 —— SGP.22 ProfileNickname SIZE(0..64)，空即清除
        var req = LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", string.Empty);

        Assert.Equal(0x90, req[15]);
        Assert.Equal(0x00, req[16]);
        Assert.Equal(17, req.Length);
        Assert.Equal(14, req[2]);
    }

    [Fact]
    public void SetNickname_RejectsNonAsciiAndLongNames()
    {
        Assert.Throws<ArgumentException>(
            () => LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", "中文"));
        Assert.Throws<ArgumentException>(
            () => LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", new string('a', 65)));
        Assert.Throws<ArgumentException>(
            () => LogicalChannelEs10.BuildNicknameRequest("12345", "ok")); // ICCID 太短
    }

    [Fact]
    public void IccidBcd_RoundTripWithIccidFromBcd()
    {
        const string iccid = "8986011234567890123";
        var bcd = LogicalChannelEs10.IccidToBcd(iccid);
        Assert.Equal(iccid, LogicalChannelEs10.IccidFromBcd(bcd));
    }

    [Fact]
    public void IccidBcd_TwentyDigits_NoPadding()
    {
        const string iccid = "89860112345678901234";
        var bcd = LogicalChannelEs10.IccidToBcd(iccid);
        Assert.Equal(10, bcd.Length);
        Assert.Equal(0x43, bcd[9]); // 末字节低 nibble 在前："3"低 + "4"高
        Assert.Equal(iccid, LogicalChannelEs10.IccidFromBcd(bcd));
    }
}
