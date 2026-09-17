namespace CellPort.Core.Models;

/// <summary>模块与 SIM 的当前状态快照。</summary>
public sealed class DeviceStatus
{
    /// <summary>是否已连上模块。</summary>
    public bool Connected { get; set; }

    /// <summary>模块型号描述。</summary>
    public string Model { get; set; } = "EG25-G (QDC507)";

    /// <summary>固件版本。</summary>
    public string? FirmwareRevision { get; set; }

    /// <summary>模块 IMEI。</summary>
    public string? Imei { get; set; }

    /// <summary>SIM 卡 ICCID。</summary>
    public string? Iccid { get; set; }

    /// <summary>SIM 卡 IMSI。</summary>
    public string? Imsi { get; set; }

    /// <summary>本机号码（若从 SIM 或通讯录读到）。</summary>
    public string? OwnNumber { get; set; }

    /// <summary>运营商名称。</summary>
    public string? Operator { get; set; }

    /// <summary>网络制式（LTE / UMTS 等）。</summary>
    public string? NetworkMode { get; set; }

    /// <summary>信号强度原始值（0-31，99 表示未知）。</summary>
    public int? SignalRssi { get; set; }

    /// <summary>信号强度的 dBm 表示。</summary>
    public int? SignalDbm =>
        SignalRssi is >= 0 and <= 31 ? -113 + (2 * SignalRssi.Value) : null;

    /// <summary>信号百分比（0-100）。</summary>
    public int? SignalPercent =>
        SignalRssi is >= 0 and <= 31 ? (int)Math.Round(SignalRssi.Value / 31.0 * 100) : null;

    /// <summary>SIM 是否就绪。</summary>
    public bool SimReady { get; set; }

    /// <summary>注册状态描述。</summary>
    public string? RegistrationStatus { get; set; }

    /// <summary>是否已附着数据网络。</summary>
    public bool? DataAttached { get; set; }

    /// <summary>模块当前 USB 模式描述。</summary>
    public string? UsbMode { get; set; }

    /// <summary>最后更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string SignalText =>
        SignalRssi is >= 0 and <= 31
            ? $"{SignalPercent}%  ({SignalDbm} dBm)"
            : "未知";
}
