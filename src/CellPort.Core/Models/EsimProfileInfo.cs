namespace CellPort.Core.Models;

/// <summary>
/// ES10c GetProfilesInfo 返回的单个 Profile 信息。
/// 字段语义参照 GSMA SGP.22 与 lpac es10c.c 的解析。
/// </summary>
public sealed class EsimProfileInfo
{
    /// <summary>ICCID（BCD 反序还原后的数字串）。</summary>
    public string? Iccid { get; set; }

    /// <summary>ISD-P AID（hex 字符串）。</summary>
    public string? IsdpAid { get; set; }

    /// <summary>Profile 名称（tag 92）。</summary>
    public string? ProfileName { get; set; }

    /// <summary>昵称（tag 90，用户可改）。</summary>
    public string? Nickname { get; set; }

    /// <summary>服务提供商名称（tag 91）。</summary>
    public string? ServiceProvider { get; set; }

    /// <summary>状态：0 = 未启用，1 = 已启用（tag 9F70）。</summary>
    public int? State { get; set; }

    /// <summary>类别：0 = 测试，1 = 预置（provisioning），2 = 商用（tag 95）。</summary>
    public int? ProfileClass { get; set; }

    public bool Enabled => State == 1;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Nickname) ? Nickname :
        !string.IsNullOrWhiteSpace(ProfileName) ? ProfileName :
        !string.IsNullOrWhiteSpace(Iccid) ? $"ICCID {Iccid}" : "未命名 Profile";

    public string StateText => State switch
    {
        1 => "已启用",
        0 => "未启用",
        _ => "状态未知",
    };

    public string ClassText => ProfileClass switch
    {
        0 => "测试",
        1 => "预置",
        2 => "商用",
        _ => "",
    };
}
