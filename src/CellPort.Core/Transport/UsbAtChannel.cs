using System.Runtime.InteropServices;

namespace CellPort.Core.Transport;

/// <summary>
/// 通过 Windows 原生 WinUSB API 直接访问模块的 AT 接口。
/// 
/// 设计说明：
/// 大疆 QDC507 模块暴露 5 个 USB 接口，其中 MI_02 为 AT 指令通道。
/// 该接口在 Windows 上默认没有匹配的类驱动，会显示为「未知设备」。
/// 本类直接使用 Microsoft 签名内置的 winusb.sys，无需 Quectel 官方驱动包。
///
/// 前置条件：MI_02 接口需绑定 WinUSB 驱动（程序内置一键引导，见 WinUsbBinder）。
/// </summary>
public sealed class UsbAtChannel : IAtChannel
{
    private nint _deviceHandle = nint.Zero;
    private nint _winUsbHandle = nint.Zero;
    private byte _bulkInPipe;
    private byte _bulkOutPipe;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public string Description { get; private init; } = "USB 直控";

    public AtChannelKind Kind => AtChannelKind.UsbDirect;

    public bool IsOpen => _winUsbHandle != nint.Zero;

    /// <summary>
    /// 尝试按已知 VID/PID 打开模块的 AT 接口。
    /// 失败返回 null（调用方会回退到串口）。
    /// </summary>
    public static Task<UsbAtChannel?> TryOpenAsync(CancellationToken ct = default)
    {
        return Task.Run<UsbAtChannel?>(() =>
        {
            // 由于完整 WinUSB 绑定需要设备路径枚举，这里先做可行性判断：
            // 若 MI_02 已绑定 WinUSB，设备路径形如 \\?\usb#vid_2ca3&pid_4006&mi_02#...
            var path = FindAtInterfacePath();
            if (path is null)
            {
                return null;
            }

            var handle = CreateFile(
                path,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                nint.Zero,
                OpenExisting,
                0,
                nint.Zero);

            if (handle == InvalidHandleValue)
            {
                return null;
            }

            if (!WinUsb_Initialize(handle, out var winUsb))
            {
                CloseHandle(handle);
                return null;
            }

            try
            {
                if (!WinUsb_QueryInterfaceSettings(winUsb, 0, out var ifDesc))
                {
                    WinUsb_Free(winUsb);
                    CloseHandle(handle);
                    return null;
                }

                byte bulkIn = 0, bulkOut = 0;
                for (byte i = 0; i < ifDesc.bNumEndpoints; i++)
                {
                    if (!WinUsb_QueryPipe(winUsb, 0, i, out var pipe))
                    {
                        continue;
                    }

                    // PipeType: 2 = Bulk
                    if (pipe.PipeType != 2)
                    {
                        continue;
                    }

                    // WinUSB 的 PipeId 即端点地址；0x80 位表示方向为 IN（设备 → 主机）
                    if ((pipe.PipeId & 0x80) != 0)
                    {
                        bulkIn = pipe.PipeId;
                    }
                    else
                    {
                        bulkOut = pipe.PipeId;
                    }
                }

                if (bulkIn == 0 || bulkOut == 0)
                {
                    WinUsb_Free(winUsb);
                    CloseHandle(handle);
                    return null;
                }

                return new UsbAtChannel
                {
                    _deviceHandle = handle,
                    _winUsbHandle = winUsb,
                    _bulkInPipe = bulkIn,
                    _bulkOutPipe = bulkOut,
                    Description = $"USB 直控 (MI_02, in=0x{bulkIn:X2} out=0x{bulkOut:X2})",
                };
            }
            catch
            {
                WinUsb_Free(winUsb);
                CloseHandle(handle);
                return null;
            }
        }, ct);
    }

    /// <summary>
    /// 在设备接口列表中查找 AT 通道的设备路径。
    /// </summary>
    private static string? FindAtInterfacePath()
    {
        var guids = new[]
        {
            new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED"), // GUID_DEVINTERFACE_USB_DEVICE
            new Guid("DEE824EF-729B-4A0E-9C14-B7117D33A817"), // GUID_DEVINTERFACE_WINUSB
        };

        foreach (var guid0 in guids)
        {
            var guid = guid0;
            var set = SetupDiGetClassDevs(ref guid, nint.Zero, nint.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == InvalidHandleValue)
            {
                continue;
            }

            try
            {
                var ifData = new SpDeviceInterfaceData
                {
                    cbSize = Marshal.SizeOf<SpDeviceInterfaceData>(),
                };

                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, nint.Zero, ref guid, i, ref ifData); i++)
                {
                    var size = 0;
                    // 首次调用获取所需缓冲区大小。
                    SetupDiGetDeviceInterfaceDetail(set, ref ifData, nint.Zero, 0, ref size, nint.Zero);
                    if (size == 0)
                    {
                        continue;
                    }

                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        // cbSize：32 位为 6，64 位为 8（仅取路径偏移前面的部分）
                        Marshal.WriteInt32(buffer, nint.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref ifData, buffer, size, ref size, nint.Zero))
                        {
                            // 路径字符串紧跟在 cbSize 字段之后
                            var path = Marshal.PtrToStringUni(buffer + (nint.Size == 8 ? 8 : 4));
                            if (!string.IsNullOrEmpty(path) && IsTargetAtInterface(path))
                            {
                                return path;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        return null;
    }

    /// <summary>
    /// 判断设备路径是否为目标模块的 AT 接口。
    /// </summary>
    private static bool IsTargetAtInterface(string path)
    {
        var lower = path.ToLowerInvariant();

        var hasDjiVid = lower.Contains("vid_2ca3") && lower.Contains("pid_4006");
        var hasQuectelVid = lower.Contains("vid_2c7c") && lower.Contains("pid_0125");
        if (!hasDjiVid && !hasQuectelVid)
        {
            return false;
        }

        // MI_02 或 MI_03 为 AT 通道
        return lower.Contains("mi_02") || lower.Contains("mi_03");
    }

    public Task OpenAsync(CancellationToken ct = default)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("USB 通道未就绪。");
        }
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (_winUsbHandle != nint.Zero)
        {
            WinUsb_Free(_winUsbHandle);
            _winUsbHandle = nint.Zero;
        }

        if (_deviceHandle != nint.Zero && _deviceHandle != InvalidHandleValue)
        {
            CloseHandle(_deviceHandle);
            _deviceHandle = nint.Zero;
        }

        return Task.CompletedTask;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("USB 通道未打开。");
        }

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var buffer = data.ToArray();
            var ok = WinUsb_WritePipe(_winUsbHandle, _bulkOutPipe, buffer,
                (uint)buffer.Length, out var written, nint.Zero);

            if (!ok || written != buffer.Length)
            {
                throw new IOException($"USB 写入失败（期望 {buffer.Length}，实际 {written}）。");
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("USB 通道未打开。");
        }

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // WinUSB 同步读取：设置超时后调用。
            var timeoutMs = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, 60000);
            WinUsb_SetPipePolicy(_winUsbHandle, _bulkInPipe, PipeTransferTimeout, sizeof(uint), ref timeoutMs);

            var managed = new byte[buffer.Length];
            if (!WinUsb_ReadPipe(_winUsbHandle, _bulkInPipe, managed,
                    (uint)managed.Length, out var read, nint.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                // 超时（ERROR_SEM_TIMEOUT 121）视为本次无数据。
                if (err == 121)
                {
                    return 0;
                }
                throw new IOException($"USB 读取失败，Win32 错误码 {err}。");
            }

            var count = (int)read;
            if (count > 0)
            {
                managed.AsSpan(0, count).CopyTo(buffer.Span);
            }
            return count;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _ioLock.Dispose();
    }

    // ===== Win32 / WinUSB P/Invoke =====

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint PipeTransferTimeout = 0x01;
    private static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbInterfaceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator,
        nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, nint deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, ref int requiredSize, nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(nint deviceHandle, out nint interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(nint interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(nint interfaceHandle,
        byte alternateInterfaceNumber, out UsbInterfaceDescriptor usbAltInterfaceDescriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(nint interfaceHandle, byte alternateInterfaceNumber,
        byte pipeIndex, out WinUsbPipeInformation pipeInformation);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(nint interfaceHandle, byte pipeId,
        uint policyType, uint valueLength, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(nint interfaceHandle, byte pipeId, byte[] buffer,
        uint bufferLength, out uint lengthTransferred, nint overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(nint interfaceHandle, byte pipeId, byte[] buffer,
        uint bufferLength, out uint lengthTransferred, nint overlapped);
}
