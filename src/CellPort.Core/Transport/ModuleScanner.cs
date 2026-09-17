using System.Diagnostics;

namespace CellPort.Core.Transport;

/// <summary>
/// 模块发现结果。
/// </summary>
public sealed class ModuleDiscoveryResult
{
    /// <summary>是否找到了模块。</summary>
    public bool Found => Interfaces.Count > 0;

    /// <summary>模块暴露的全部接口。</summary>
    public required IReadOnlyList<ModuleInterface> Interfaces { get; init; }

    /// <summary>是否存在可用的 AT 通道。</summary>
    public bool HasAtChannel => Interfaces.Any(i => i.IsAtChannel);

    /// <summary>是否存在数据接口。</summary>
    public bool HasDataChannel => Interfaces.Any(i => i.IsDataChannel);

    /// <summary>接口的 VID。</summary>
    public int VendorId => Interfaces.Count > 0 ? Interfaces[0].VendorId : 0;

    /// <summary>接口的 PID。</summary>
    public int ProductId => Interfaces.Count > 0 ? Interfaces[0].ProductId : 0;

    /// <summary>USB 身份是否已被改写为移远标准 ID。</summary>
    public bool IsQuectelIdentity =>
        VendorId == ModuleIdentity.QuectelVendorId &&
        ProductId == ModuleIdentity.QuectelProductId;

    /// <summary>生成的诊断说明。</summary>
    public string Describe()
    {
        if (!Found)
        {
            return "未发现模块。请确认：\n" +
                   "  1. 模块已通过支持数据传输的 USB 线连接；\n" +
                   "  2. SIM 卡已插入；\n" +
                   "  3. 若设备管理器出现黄色感叹号，请先安装驱动（程序内置引导）。";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(IsQuectelIdentity
            ? "识别为移远标准身份 (2C7C:0125)。"
            : "识别为大疆定制身份 (2CA3:4006)。");
        sb.AppendLine($"接口数量：{Interfaces.Count}");
        sb.AppendLine(HasAtChannel ? "AT 通道：可用" : "AT 通道：未找到");
        sb.AppendLine(HasDataChannel ? "数据接口：存在" : "数据接口：未找到");
        sb.AppendLine();
        foreach (var i in Interfaces.OrderBy(x => x.InterfaceNumber))
        {
            sb.AppendLine("  " + i);
        }
        return sb.ToString();
    }
}

/// <summary>
/// 通过 WMI / PnP 查询枚举模块接口。
/// 该方式不要求设备已绑定 WinUSB 驱动，用于「发现」阶段。
/// </summary>
public static class ModuleScanner
{
    private static ModuleDiscoveryResult? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private static readonly object _lock = new();

    /// <summary>
    /// 扫描超时上限。WMI 首次查询（尤其是 Win32_PnPEntity 全量遍历）
    /// 在部分机器上需要 6~8 秒，过短的超时会误判为「未发现模块」，
    /// 因此这里给到 12 秒，并对成功结果做缓存以避免重复开销。
    /// </summary>
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 扫描当前系统中的模块接口。
    /// 
    /// 结果会做短期缓存（5 秒），避免界面频繁刷新时反复触发 WMI 查询。
    /// 整个扫描带超时保护，超时返回空结果而不阻塞调用方。
    /// 
    /// 注意：空结果不写入缓存 —— WMI 偶发超时不应导致后续 5 秒内持续报「未发现模块」。
    /// </summary>
    /// <param name="bypassCache">为 true 时强制重新扫描。</param>
    public static ModuleDiscoveryResult Scan(bool bypassCache = false)
    {
        lock (_lock)
        {
            if (!bypassCache &&
                _cached is not null &&
                DateTime.UtcNow - _cachedAt < CacheDuration)
            {
                return _cached;
            }

            var result = ScanCore();

            // 仅在真正发现结果时缓存，避免把「超时/异常」当成有效结果缓存住。
            if (result.Found)
            {
                _cached = result;
                _cachedAt = DateTime.UtcNow;
            }

            return result;
        }
    }

    private static ModuleDiscoveryResult ScanCore()
    {
        var list = new List<ModuleInterface>();

        try
        {
            // WMI 查询放到后台线程并加超时，避免拖死界面。
            var task = Task.Run(() => QueryAll(list));
            if (!task.Wait(ScanTimeout))
            {
                Debug.WriteLine("ModuleScanner: WMI 查询超时。");
                return new ModuleDiscoveryResult { Interfaces = [] };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ModuleScanner.Scan failed: {ex}");
            return new ModuleDiscoveryResult { Interfaces = [] };
        }

        // 按接口号去重（复合设备可能在同一接口上报多个实例）。
        var deduped = list
            .GroupBy(x => (x.VendorId, x.ProductId, x.BusNumber, x.DeviceAddress, x.InterfaceNumber))
            .Select(g => g.First())
            .OrderBy(x => x.InterfaceNumber)
            .ToList();

        return new ModuleDiscoveryResult { Interfaces = deduped };
    }

    private static void QueryAll(List<ModuleInterface> target)
    {
        foreach (var (vid, pid) in ModuleIdentity.KnownIds)
        {
            var deviceIdPattern = $"VID_{vid:X4}&PID_{pid:X4}";
            var instances = QueryPnPInstances(deviceIdPattern);

            foreach (var instanceId in instances)
            {
                var parsed = ParseInstanceId(instanceId, vid, pid);
                if (parsed is not null)
                {
                    target.Add(parsed);
                }
            }

            if (target.Count > 0)
            {
                // 优先使用命中的第一组身份，不再叠加另一组。
                return;
            }
        }
    }

    private static IEnumerable<string> QueryPnPInstances(string deviceIdPattern)
    {
        // 让 WMI 在服务端先按 VID/PID 过滤，避免把整机所有 PnP 设备都拉回来
        // （后者在设备较多的机器上需要数秒，是扫描慢的主因）。
        var likePattern = deviceIdPattern.Replace("\\", "\\\\");

        var query =
            "SELECT DeviceID FROM Win32_PnPEntity " +
            $"WHERE DeviceID LIKE '%{likePattern}%'";

        var results = new List<string>();
        using var searcher = new System.Management.ManagementObjectSearcher(query);
        foreach (var obj in searcher.Get())
        {
            var deviceId = obj["DeviceID"]?.ToString();
            if (string.IsNullOrEmpty(deviceId))
            {
                continue;
            }

            // 二次确认（LIKE 是大小写不敏感的，这里按 Ordinal 再核一遍）
            if (deviceId.Contains(deviceIdPattern, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(deviceId);
            }
        }

        return results;
    }

    /// <summary>
    /// 从 Windows 实例 ID 解析出接口信息。
    /// 形如：USB\VID_2CA3&amp;PID_4006&amp;MI_02\7&amp;2F1A3B4C&amp;0&amp;0002
    /// </summary>
    private static ModuleInterface? ParseInstanceId(string instanceId, int vid, int pid)
    {
        var parts = instanceId.Split('\\');
        if (parts.Length < 2)
        {
            return null;
        }

        var hardwareId = parts[1];
        var interfaceNumber = 0;
        var alternateSetting = 0;

        foreach (var token in hardwareId.Split('&'))
        {
            if (token.StartsWith("MI_", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(token.AsSpan(3), System.Globalization.NumberStyles.HexNumber, null, out var mi))
            {
                interfaceNumber = mi;
            }
            else if (token.StartsWith("AS_", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(token.AsSpan(3), System.Globalization.NumberStyles.HexNumber, null, out var asn))
            {
                alternateSetting = asn;
            }
        }

        return new ModuleInterface
        {
            VendorId = vid,
            ProductId = pid,
            InterfaceNumber = interfaceNumber,
            AlternateSetting = alternateSetting,
            BusNumber = 0,
            DeviceAddress = 0,
            Manufacturer = "Quectel",
            Product = "EG25-G (QDC507)",
        };
    }
}
