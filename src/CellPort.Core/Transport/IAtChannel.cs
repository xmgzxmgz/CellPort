namespace CellPort.Core.Transport;

/// <summary>
/// 一个可以收发 AT 指令的通道。实现可能是 USB 直控（libusb）或串口（COM）。
/// </summary>
public interface IAtChannel : IAsyncDisposable
{
    /// <summary>通道描述，用于界面展示。</summary>
    string Description { get; }

    /// <summary>底层通道类型。</summary>
    AtChannelKind Kind { get; }

    /// <summary>通道是否处于打开状态。</summary>
    bool IsOpen { get; }

    /// <summary>打开通道。</summary>
    Task OpenAsync(CancellationToken ct = default);

    /// <summary>关闭通道。</summary>
    Task CloseAsync();

    /// <summary>写入一段原始字节。</summary>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>读取一段原始字节；无数据时返回空。</summary>
    Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>AT 通道类型。</summary>
public enum AtChannelKind
{
    /// <summary>通过 libusb/WinUSB 直接控制 USB 接口。</summary>
    UsbDirect,

    /// <summary>通过 Windows 串口（需要 Quectel 驱动）。</summary>
    SerialPort,
}
