using System.Text;

namespace CellPort.Core.Services;

/// <summary>
/// 轻量按天滚动文件日志：%LOCALAPPDATA%\CellPort\logs\cellport-yyyyMMdd.log，保留 7 天。
/// 未 Initialize() 时全部静默 no-op；任何写入失败都吞掉 —— 日志绝不能反过来影响主流程。
/// </summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static string? _logDir;
    private static bool _initialized;

    /// <summary>日志目录（初始化后可读；未初始化为空串）。</summary>
    public static string LogDirectory => _logDir ?? string.Empty;

    /// <summary>创建日志目录并清理超过 7 天的旧日志。进程内只需调用一次。</summary>
    public static void Initialize()
    {
        lock (Lock)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                _logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CellPort",
                    "logs");
                System.IO.Directory.CreateDirectory(_logDir);
                Cleanup(_logDir, 7);
            }
            catch
            {
                // 目录不可用（无权限 / 磁盘故障）时放弃文件日志
                _logDir = null;
            }

            _initialized = true;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}\n{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");

    public static void Write(string level, string message)
    {
        if (!_initialized || _logDir is null)
        {
            return;
        }

        try
        {
            lock (Lock)
            {
                var path = Path.Combine(_logDir, $"cellport-{DateTime.Now:yyyyMMdd}.log");
                var line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message.ReplaceLineEndings(" | ")}";
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败静默
        }
    }

    private static void Cleanup(string dir, int keepDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in System.IO.Directory.GetFiles(dir, "cellport-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
        }
        catch
        {
            // 清理失败不影响使用
        }
    }
}
