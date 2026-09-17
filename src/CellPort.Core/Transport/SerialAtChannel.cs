using System.IO.Ports;

namespace CellPort.Core.Transport;

/// <summary>
/// 基于 Windows 串口的 AT 通道。
/// 需要设备已绑定 Quectel AT 驱动并生成 COM 口（例如 "Quectel USB AT Port"）。
/// </summary>
public sealed class SerialAtChannel : IAtChannel
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _port;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public SerialAtChannel(string portName, int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public string Description => $"串口 {_portName} @ {_baudRate}";

    public AtChannelKind Kind => AtChannelKind.SerialPort;

    public bool IsOpen => _port?.IsOpen == true;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (IsOpen)
        {
            return;
        }

        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            // 底层同步读的超时。ReadAsync 依赖它让后台读线程尽快返回，
            // 因此设得较短（150ms）；真正的读超时由 ReadAsync 的 timeout 参数控制。
            ReadTimeout = 150,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true,
        };

        // SerialPort.Open() 是同步阻塞调用，且不受 CancellationToken 影响。
        // 实测某些端口（如模块暴露但不可用的诊断口）在内核态会等待数十秒到数分钟
        // （典型错误 121 ERROR_SEM_TIMEOUT）。这里放到独立线程执行，并叠加超时，
        // 使不可用端口快速失败，不拖住整个连接流程。
        var openTask = Task.Run(() =>
        {
            port.Open();
            return port;
        }, CancellationToken.None);

        var completed = await Task.WhenAny(openTask, Task.Delay(OpenTimeout, ct))
            .ConfigureAwait(false);

        if (completed != openTask)
        {
            // 超时或被取消：放弃该端口。后台线程可能仍在阻塞，
            // 因此不做 Dispose，避免在其运行时释放句柄导致未定义行为；
            // 交由 GC 与 OS 在底层调用返回后回收。
            throw new TimeoutException($"打开串口 {_portName} 超时。");
        }

        // 让异常向上冒泡（端口被占用 / 不存在 / 访问被拒等）。
        port = await openTask.ConfigureAwait(false);

        try
        {
            // 清掉上电残留数据，避免第一条 AT 读到旧响应。
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch
        {
            // 部分虚拟串口不支持 Discard，忽略。
        }

        _port = port;
    }

    /// <summary>打开端口的超时上限。超过即认为该端口不可用。</summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(3);

    public Task CloseAsync()
    {
        try
        {
            if (_port?.IsOpen == true)
            {
                _port.Close();
            }
        }
        finally
        {
            _port?.Dispose();
            _port = null;
        }
        return Task.CompletedTask;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var port = _port ?? throw new InvalidOperationException("串口未打开。");

        // 与 ReadAsync 争用同一把锁：SerialPort 非线程安全，读写必须互斥。
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    port.Write(data.Span.ToArray(), 0, data.Length);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"向串口 {_portName} 写入超时。");
                }
                catch (InvalidOperationException)
                {
                    throw new IOException($"串口 {_portName} 已关闭。");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>写入超时上限。</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 读取数据。
    /// 
    /// 实现要点（均为实测踩坑后确定）：
    ///   1. SerialPort 不是线程安全的 —— Read/Write 必须串行化，否则并发访问
    ///      会导致读回 0 字节（表现为「AT 无响应」）。因此全程持有 _ioLock。
    ///   2. SerialPort.BaseStream.ReadAsync 无法被 CancellationToken 可靠中断，
    ///      底层 ReadFile 处于等待态时取消不会立即返回，会造成永久挂起。
    ///      因此改用「BytesToRead 轮询 + 同步 Read」的方式：轮询到有数据才读，
    ///      轮询本身受 timeout 约束，保证按时返回。
    /// </summary>
    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
    {
        var port = _port ?? throw new InvalidOperationException("串口未打开。");
        if (ct.IsCancellationRequested)
        {
            return 0;
        }

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 在后台线程轮询，避免阻塞调用线程。
            return await Task.Run(() =>
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return 0;
                    }

                    try
                    {
                        if (port.BytesToRead > 0)
                        {
                            var want = Math.Min(port.BytesToRead, buffer.Length);
                            var tmp = new byte[want];
                            var n = port.Read(tmp, 0, want);
                            if (n > 0)
                            {
                                tmp.AsSpan(0, n).CopyTo(buffer.Span);
                                return n;
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        return 0; // 端口已关闭
                    }
                    catch (IOException)
                    {
                        return 0; // 设备已拔出
                    }

                    // 短等待后重试，兼顾响应速度与 CPU 占用。
                    Thread.Sleep(10);
                }

                return 0; // 超时无数据
            }).ConfigureAwait(false);
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

    /// <summary>
    /// 枚举可能属于蜂窝模块的串口。
    /// 
    /// 注意：SerialPort.GetPortNames() 在某些环境下会长时间阻塞
    /// （例如存在异常设备节点时），因此这里放在独立线程并加超时保护，
    /// 避免拖死调用方（尤其是界面线程）。
    /// </summary>
    public static IReadOnlyList<SerialPortCandidate> EnumerateCandidates(
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);

        string[] names;
        try
        {
            var task = Task.Run(() => SerialPort.GetPortNames());
            if (!task.Wait(effectiveTimeout))
            {
                // 超时：认为本次枚举不可用，返回空集合而不是卡住。
                return [];
            }
            names = task.Result;
        }
        catch
        {
            return [];
        }

        return [.. names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(NaturalSort)
            .Select(n => new SerialPortCandidate(n, null))];
    }

    /// <summary>
    /// 尝试打开端口并在失败时快速返回，用于在真正连接前筛掉不可用端口。
    /// 返回 true 表示端口可打开（不代表它是 AT 口）。
    /// </summary>
    public static bool CanOpen(string portName, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? OpenTimeout;

        var task = Task.Run(() =>
        {
            SerialPort? p = null;
            try
            {
                p = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 100,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = true,
                };
                p.Open();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    if (p?.IsOpen == true)
                    {
                        p.Close();
                    }
                    p?.Dispose();
                }
                catch
                {
                    // 忽略关闭异常
                }
            }
        });

        try
        {
            return task.Wait(effectiveTimeout) && task.Result;
        }
        catch
        {
            return false;
        }
    }

    private static string NaturalSort(string s) =>
        s.PadLeft(10, '0');
}

/// <summary>候选串口。</summary>
/// <param name="PortName">端口名，如 COM5。</param>
/// <param name="Description">设备描述，若可获取。</param>
public sealed record SerialPortCandidate(string PortName, string? Description);
