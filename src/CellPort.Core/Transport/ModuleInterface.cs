namespace CellPort.Core.Transport;

/// <summary>
/// 描述一个被发现的模块 USB 接口。
/// </summary>
public sealed class ModuleInterface
{
    /// <summary>USB Vendor ID。</summary>
    public required int VendorId { get; init; }

    /// <summary>USB Product ID。</summary>
    public required int ProductId { get; init; }

    /// <summary>接口号（对应 Windows 硬件 ID 中的 MI_xx）。</summary>
    public required int InterfaceNumber { get; init; }

    /// <summary>接口的备用设置号。</summary>
    public required int AlternateSetting { get; init; }

    /// <summary>USB 总线号。</summary>
    public required int BusNumber { get; init; }

    /// <summary>USB 设备地址。</summary>
    public required int DeviceAddress { get; init; }

    /// <summary>生产者字符串（若可读）。</summary>
    public string? Manufacturer { get; init; }

    /// <summary>产品字符串（若可读）。</summary>
    public string? Product { get; init; }

    /// <summary>序列号（若可读）。</summary>
    public string? SerialNumber { get; init; }

    /// <summary>该接口在当前系统中的稳定标识，用于跨次识别同一模块。</summary>
    public string StableKey => $"{VendorId:X4}:{ProductId:X4}:{BusNumber}:{DeviceAddress}";

    /// <summary>是否为 AT 指令通道。</summary>
    public bool IsAtChannel =>
        InterfaceNumber is ModuleIdentity.AtInterfaceNumber or ModuleIdentity.AtInterfaceNumberAlt;

    /// <summary>是否为数据网卡接口。</summary>
    public bool IsDataChannel => InterfaceNumber == ModuleIdentity.DataInterfaceNumber;

    public override string ToString() =>
        $"MI_{InterfaceNumber:D2} bus{BusNumber} addr{DeviceAddress} " +
        $"[{VendorId:X4}:{ProductId:X4}] {Product ?? "(unknown)"}";
}
