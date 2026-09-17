using CellPort.Core.At;
using CellPort.Core.Models;
using CellPort.Core.Transport;

namespace CellPort.Core.Services;

/// <summary>
/// 模块管理器：负责发现设备、建立 AT 通道、装配各项服务、处理主动上报分发。
/// 这是上层界面与硬件之间的唯一入口。
/// </summary>
public sealed class ModemManager : IAsyncDisposable
{
    private AtEngine? _engine;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    /// <summary>
    /// 串口探测结果的进程内缓存。热插拔后 COM 号会漂移，但通常仍落在少数几个口上，
    /// 因此把「上次成功的口」放在列表首位优先尝试，能大幅缩短连接耗时。
    /// 该缓存还会由宿主（App 层）持久化到磁盘，实现跨进程复用。
    /// </summary>
    private readonly List<string> _portHints = [];

    /// <summary>
    /// 由宿主注入的「记住上次成功串口」回调，用于把结果持久化到配置文件。
    /// 让 Core 层保持对 App 层的零依赖。
    /// </summary>
    public Action<string>? RememberPort { get; set; }

    /// <summary>
    /// 注入历史成功端口（最新的在前），供连接时优先尝试。
    /// 由宿主在启动时从配置读取后调用。
    /// </summary>
    public void PrimePortHints(IEnumerable<string?> hints)
    {
        foreach (var h in hints)
        {
            if (string.IsNullOrWhiteSpace(h))
            {
                continue;
            }

            _portHints.RemoveAll(p => string.Equals(p, h, StringComparison.OrdinalIgnoreCase));
            _portHints.Add(h);
        }
    }

    /// <summary>设备状态变化。</summary>
    public event EventHandler<DeviceStatus>? StatusUpdated;

    /// <summary>连接状态变化。</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>收到的任意主动上报（用于日志/调试）。</summary>
    public event EventHandler<string>? UnsolicitedLine;

    /// <summary>连接过程中的阶段性诊断信息（供诊断工具/界面显示）。</summary>
    public event EventHandler<string>? TraceMessage;

    private void Trace(string message) => TraceMessage?.Invoke(this, message);

    /// <summary>当前是否已连接。</summary>
    public bool IsConnected => _engine?.Channel.IsOpen == true;

    /// <summary>当前状态快照。</summary>
    public DeviceStatus Status { get; private set; } = new();

    /// <summary>短信服务。</summary>
    public SmsService? Sms { get; private set; }

    /// <summary>通话服务。</summary>
    public CallService? Calls { get; private set; }

    /// <summary>设备信息服务。</summary>
    public DeviceInfoService? Info { get; private set; }

    /// <summary>当前 AT 通道描述。</summary>
    public string ChannelDescription => _engine?.Channel.Description ?? "未连接";

    /// <summary>
    /// 当前 AT 引擎。供诊断工具直接构造通道级客户端（如 LogicalChannelEs10）；
    /// 未连接时为 null。
    /// </summary>
    public AtEngine? Engine => _engine;

    /// <summary>
    /// 连接模块。按 UsbDirect → SerialPort 的顺序尝试。
    /// </summary>
    /// <param name="preferredSerialPort">指定串口（用户手动选择时使用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    public async Task<bool> ConnectAsync(string? preferredSerialPort = null, CancellationToken ct = default)
    {
        // 连接过程串行化：界面开机自动连接与用户手动点击可能重叠，
        // 而进入连接流程的第一步就是 DisconnectAsync，
        // 会把另一个流程刚打开的通道释放掉，表现为「莫名其妙的连接失败」。
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ConnectCoreAsync(preferredSerialPort, ct).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private async Task<bool> ConnectCoreAsync(string? preferredSerialPort, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);

        IAtChannel? channel = null;

        // 1) 优先尝试 USB 直控（WinUSB）
        //    若 MI_02 尚未绑定 WinUSB，会在枚举阶段就返回 null（很快）。
        //    这里额外加超时，防止个别驱动状态下 SetupAPI 调用异常拖慢连接。
        Trace("[1/5] 尝试 USB 直控（WinUSB）…");
        try
        {
            var usbTask = UsbAtChannel.TryOpenAsync(ct);
            var done = await Task.WhenAny(usbTask, Task.Delay(TimeSpan.FromSeconds(4), ct))
                .ConfigureAwait(false);
            if (done == usbTask)
            {
                var usb = await usbTask.ConfigureAwait(false);
                if (usb is not null)
                {
                    channel = usb;
                    Trace("      USB 直控可用。");
                }
                else
                {
                    Trace("      USB 直控不可用（MI_02 未绑定 WinUSB），转串口。");
                }
            }
            else
            {
                Trace("      USB 探测超时，转串口。");
            }
        }
        catch (Exception ex)
        {
            Trace($"      USB 探测异常：{ex.GetType().Name}，转串口。");
        }

        // 2) 回退到串口
        if (channel is null)
        {
            string? port;
            if (preferredSerialPort is not null)
            {
                port = preferredSerialPort;
                Trace($"[2/5] 使用指定串口：{port}");
            }
            else
            {
                Trace("[2/5] 自动探测串口…");
                var detectTask = AutoDetectSerialAsync(_portHints, ct);
                var done2 = await Task.WhenAny(detectTask, Task.Delay(TimeSpan.FromSeconds(20), ct))
                    .ConfigureAwait(false);
                port = done2 == detectTask
                    ? await detectTask.ConfigureAwait(false)
                    : null;
                Trace(port is null ? "      未找到 AT 串口。" : $"      找到 AT 串口：{port}");
            }

            if (port is not null)
            {
                channel = new SerialAtChannel(port);

                // 记住成功端口：进程内提到列表首位，并通知宿主持久化。
                _portHints.RemoveAll(p =>
                    string.Equals(p, port, StringComparison.OrdinalIgnoreCase));
                _portHints.Insert(0, port);
                if (_portHints.Count > 4)
                {
                    _portHints.RemoveRange(4, _portHints.Count - 4);
                }

                try
                {
                    RememberPort?.Invoke(port);
                }
                catch
                {
                    // 持久化失败不影响连接
                }
            }
        }

        if (channel is null)
        {
            Trace("[X] 无可用通道。");
            return false;
        }

        Trace($"[3/5] 打开通道：{channel.Description}");
        try
        {
            await channel.OpenAsync(ct).ConfigureAwait(false);
            Trace("      通道已打开。");
        }
        catch (Exception ex)
        {
            Trace($"      打开失败：{ex.GetType().Name} {ex.Message}");
            await channel.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        var engine = new AtEngine(channel);
        engine.IoTrace += (_, msg) => Trace($"      IO {msg}");

        // 3) 探测 AT 通道是否真的可用
        Trace("[4/5] 发送 AT 探测…");
        var probe = await engine.ExecuteAsync("AT", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            Trace("      AT 无响应。");
            await engine.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        Trace("      AT 响应正常。");

        _engine = engine;

        Sms = new SmsService(engine);
        Calls = new CallService(engine);
        Info = new DeviceInfoService(engine);

        await Sms.InitializeAsync(ct).ConfigureAwait(false);
        await Calls.InitializeAsync(ct).ConfigureAwait(false);

        // 4) 启动主动上报泵
        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token));

        ConnectionChanged?.Invoke(this, true);

        // 5) 首次状态读取
        await RefreshStatusAsync(ct).ConfigureAwait(false);

        // 6) 同步已存短信
        try
        {
            await Sms.SyncStoredAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 同步失败不影响连接
        }

        return true;
    }

    /// <summary>断开连接。</summary>
    public async Task DisconnectAsync()
    {
        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
            _pumpCts = null;
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }

        Sms = null;
        Calls = null;
        Info = null;
        Status = new DeviceStatus();

        ConnectionChanged?.Invoke(this, false);
    }

    /// <summary>刷新状态。</summary>
    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        if (Info is null)
        {
            return;
        }
        try
        {
            Status = await Info.ReadStatusAsync(ct).ConfigureAwait(false);
            StatusUpdated?.Invoke(this, Status);
        }
        catch
        {
            // 刷新失败保留上次快照
        }
    }

    /// <summary>执行任意 AT 指令（供控制台使用）。</summary>
    public async Task<AtResult> ExecuteAtAsync(string command, CancellationToken ct = default)
    {
        if (_engine is null)
        {
            return new AtResult(command, false, [], "未连接模块。", TimeSpan.Zero);
        }
        return await _engine.ExecuteAsync(command, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 主动上报泵：周期性轮询 AtEngine 的上报队列并分发给各服务。
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(300, ct).ConfigureAwait(false);

                if (_engine is null)
                {
                    continue;
                }

                var lines = _engine.DrainUnsolicited();
                foreach (var line in lines)
                {
                    UnsolicitedLine?.Invoke(this, line);

                    // 先给通话服务（RING/CLIP 需要优先处理）
                    if (Calls?.HandleUnsolicited(line) == true)
                    {
                        continue;
                    }

                    // 再给短信服务
                    Sms?.FeedLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 泵不因单次异常退出
            }
        }
    }

    /// <summary>
    /// 自动探测可能承载 AT 通道的串口。
    /// 
    /// 实测环境（大疆一代 QDC507 + Quectel 驱动，本机 COM4~COM9）：
    ///   · COM4 = DM Port（诊断口）  → 可打开，但不回 AT
    ///   · COM5 = NMEA Port          → 可打开，但不回 AT
    ///   · COM6 = AT Port            → 回复 "AT\r\r\nOK\r\n"
    ///   · COM7 = 同一 AT 口的另一实例 → 同样回 OK（热插拔后会在这两个号之间漂移）
    ///   · COM8/COM9 = 蓝牙虚拟串口   → 与模块无关，应排除
    ///
    /// 因此策略为两段式：
    ///   1. 「已知端口」先行：把上次成功的口放在最前面，单个探测（约 50ms）即可命中，
    ///      这是启动秒连的关键路径；
    ///   2. 未命中时才做全量并行探测：对系统串口并行发 AT，谁先回 OK 用谁。
    ///      蓝牙口（BTHENUM / 描述含「蓝牙」「Bluetooth」）直接剔除，避免无谓等待。
    /// </summary>
    /// <param name="knownPorts">
    /// 历史成功端口，按优先级排列（最新的在前）。可为空。
    /// </param>
    private static async Task<string?> AutoDetectSerialAsync(
        IReadOnlyList<string> knownPorts, CancellationToken ct)
    {
        var candidates = SerialAtChannel.EnumerateCandidates();
        if (candidates.Count == 0)
        {
            return null;
        }

        // ---- 第 1 段：已知端口优先（秒连路径）----
        foreach (var known in knownPorts)
        {
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            var exists = candidates.Any(c =>
                string.Equals(c.PortName, known, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                continue;
            }

            var hit = await ProbePortAsync(known, ct).ConfigureAwait(false);
            if (hit is not null)
            {
                return hit;
            }
        }

        // ---- 第 2 段：全量并行探测 ----
        // 剔除明显无关的端口：蓝牙虚拟串口、以及没有对应模块 PnP 实例的可疑端口。
        var suspect = new HashSet<string>(knownPorts, StringComparer.OrdinalIgnoreCase);
        var candidatesToTry = candidates
            .Where(c => !IsBluetoothPort(c.PortName))
            .Where(c => !suspect.Contains(c.PortName))
            .ToList();

        if (candidatesToTry.Count == 0)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var tasks = candidatesToTry
            .Select(c => ProbePortAsync(c.PortName, cts.Token))
            .ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);

            try
            {
                var port = await finished.ConfigureAwait(false);
                if (port is not null)
                {
                    // 不取消其余任务：ProbePortAsync 内部用 await using 管理通道，
                    // 让它们自然结束后自行释放，避免提前取消导致串口句柄未归还
                    // （实测提前 Cancel 会让紧接着的真实连接报「资源被占用」）。
                    return port;
                }
            }
            catch
            {
                // 该端口探测失败，继续等下一个
            }
        }

        return null;
    }

    /// <summary>
    /// 判断某端口是否为蓝牙虚拟串口。这类端口由系统常驻，与模块无关，
    /// 对其发 AT 必然超时，属于纯粹的浪费。
    /// 判定依据：Win32_PnPEntity 的 DeviceID 以 BTHENUM 开头。
    /// 为避免每次连接都跑 WMI，结果带 30 秒缓存。
    /// </summary>
    private static bool IsBluetoothPort(string portName)
    {
        var map = GetPortFriendlyMap();
        return map.TryGetValue(portName, out var id) &&
               id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string>? _portMapCache;
    private static DateTime _portMapCachedAt = DateTime.MinValue;
    private static readonly object _portMapLock = new();

    /// <summary>
    /// 建立「COM 口名 → PnP DeviceID」映射，用于识别端口所属设备类型。
    /// 查询带超时与缓存；失败时返回空表（调用方按「非蓝牙」处理，不影响主流程）。
    /// </summary>
    private static Dictionary<string, string> GetPortFriendlyMap()
    {
        lock (_portMapLock)
        {
            if (_portMapCache is not null &&
                DateTime.UtcNow - _portMapCachedAt < TimeSpan.FromSeconds(30))
            {
                return _portMapCache;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var task = Task.Run(() =>
                {
                    var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                    foreach (var o in searcher.Get())
                    {
                        var name = o["Name"]?.ToString();
                        var id = o["DeviceID"]?.ToString();
                        var port = ExtractPortName(name);
                        if (port is not null && id is not null)
                        {
                            local[port] = id;
                        }
                    }
                    return local;
                });

                if (task.Wait(TimeSpan.FromSeconds(5)))
                {
                    map = task.Result;
                }
            }
            catch
            {
                // WMI 不可用时退化为空表
            }

            _portMapCache = map;
            _portMapCachedAt = DateTime.UtcNow;
            return map;
        }
    }

    /// <summary>从「Quectel USB AT Port (COM6)」这类友好名中取出 COM6。</summary>
    private static string? ExtractPortName(string? friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName))
        {
            return null;
        }

        var open = friendlyName.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
        {
            return null;
        }

        var close = friendlyName.IndexOf(')', open);
        if (close < 0)
        {
            return null;
        }

        return friendlyName.Substring(open + 1, close - open - 1).Trim();
    }

    /// <summary>
    /// 探测单个串口是否为 AT 通道。
    /// </summary>
    private static async Task<string?> ProbePortAsync(string portName, CancellationToken ct)
    {
        try
        {
            var ch = new SerialAtChannel(portName);
            await using (ch)
            {
                // OpenAsync 内部已带超时保护，不可用端口会快速抛出。
                await ch.OpenAsync(ct).ConfigureAwait(false);

                // 发送探测指令；部分固件需要两次往返才回 OK
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    await ch.WriteAsync("AT\r"u8.ToArray(), ct).ConfigureAwait(false);

                    var buf = new byte[256];
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var sb = new System.Text.StringBuilder();

                    while (sw.ElapsedMilliseconds < 800 && !ct.IsCancellationRequested)
                    {
                        var read = await ch.ReadAsync(buf, TimeSpan.FromMilliseconds(200), ct)
                            .ConfigureAwait(false);
                        if (read <= 0)
                        {
                            continue;
                        }
                        sb.Append(System.Text.Encoding.ASCII.GetString(buf, 0, read));

                        if (sb.ToString().Contains("OK", StringComparison.OrdinalIgnoreCase))
                        {
                            return portName;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 整体超时，交由调用方处理
        }
        catch
        {
            // 端口不可用（被占用、不存在、打开超时等），跳过
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _connectLock.Dispose();
    }
}
