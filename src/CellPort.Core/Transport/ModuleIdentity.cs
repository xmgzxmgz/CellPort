namespace CellPort.Core.Transport;

/// <summary>
/// DJI 第一代 4G 模块（Baiwang QDC507 / Quectel EG25-G）的 USB 标识常量。
/// </summary>
public static class ModuleIdentity
{
    /// <summary>大疆定制 Vendor ID。</summary>
    public const int DjiVendorId = 0x2CA3;

    /// <summary>大疆定制 Product ID。</summary>
    public const int DjiProductId = 0x4006;

    /// <summary>移远标准 Vendor ID（模块被改写 USB 身份后使用）。</summary>
    public const int QuectelVendorId = 0x2C7C;

    /// <summary>移远标准 Product ID（EC20/EC25/EG25 系列）。</summary>
    public const int QuectelProductId = 0x0125;

    /// <summary>已知的候选 (VID, PID) 组合，按优先级排列。</summary>
    public static readonly (int Vid, int Pid)[] KnownIds =
    [
        (DjiVendorId, DjiProductId),
        (QuectelVendorId, QuectelProductId),
    ];

    /// <summary>AT 指令通道所在的接口号（MI_02）。</summary>
    public const int AtInterfaceNumber = 2;

    /// <summary>备用 AT 通道接口号（MI_03）。</summary>
    public const int AtInterfaceNumberAlt = 3;

    /// <summary>数据网卡接口号（MI_04，RMNET/ECM）。</summary>
    public const int DataInterfaceNumber = 4;
}
