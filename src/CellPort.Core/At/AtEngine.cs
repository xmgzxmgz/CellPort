using System.Text;
using CellPort.Core.Transport;

namespace CellPort.Core.At;

/// <summary>
/// 一次 AT 指令调用的结果。
/// </summary>
/// <param name="Command">发出的指令。</param>
/// <param name="Succeeded">是否以 OK 结束。</param>
/// <param name="Lines">响应中的信息行（不含回显、不含 OK/ERROR 终结行）。</param>
/// <param name="RawText">完整原始响应文本。</param>
/// <param name="Duration">耗时。</param>
public sealed record AtResult(
    string Command,
    bool Succeeded,
    IReadOnlyList<string> Lines,
    string RawText,
    TimeSpan Duration)
{
    /// <summary>取第一行，没有则返回 null。</summary>
    public string? FirstLine => Lines.Count > 0 ? Lines[0] : null;

    /// <summary>按前缀取行。</summary>
    public IEnumerable<string> LinesWithPrefix(string prefix) =>
        Lines.Where(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        $"{Command} => {(Succeeded ? "OK" : "FAIL")} ({Lines.Count} lines, {Duration.TotalMilliseconds:F0}ms)";
}

/// <summary>
/// AT 指令执行引擎。负责写入、逐行读取、识别 OK/ERROR 终结符。
/// </summary>
public sealed class AtEngine : IAsyncDisposable
{
    /// <summary>默认单条指令超时。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>发送短信等长耗时操作的超时。</summary>
    public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(60);

    private readonly IAtChannel _channel;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly StringBuilder _pending = new();
    private readonly List<string> _unsolicitedQueue = [];
    private readonly object _unsolicitedLock = new();

    /// <summary>收到未经请求的主动上报（如 +CMTI）时触发。</summary>
    public event EventHandler<string>? UnsolicitedReport;

    /// <summary>底层 IO 诊断信息（原始字节），仅在排障时订阅。</summary>
    public event EventHandler<string>? IoTrace;

    public AtEngine(IAtChannel channel)
    {
        _channel = channel;
    }

    /// <summary>底层通道。</summary>
    public IAtChannel Channel => _channel;

    /// <summary>
    /// 执行一条 AT 指令。
    /// </summary>
    /// <param name="command">指令正文，不含结尾的 CR。为空表示只发送一个 AT 探测。</param>
    /// <param name="timeout">超时。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<AtResult> ExecuteAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var full = string.IsNullOrWhiteSpace(command) ? "AT" : command.Trim();

        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        try
        {
            try
            {
                await _channel.WriteAsync(Encoding.ASCII.GetBytes(full + "\r"), ct).ConfigureAwait(false);
                IoTrace?.Invoke(this, $">>> {full}\\r");
            }
            catch (Exception ex)
            {
                IoTrace?.Invoke(this, $"!!! write failed: {ex.GetType().Name} {ex.Message}");
                throw;
            }

            return await ReadUntilFinalAsync(full, effectiveTimeout, started, ct).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<AtResult> ReadUntilFinalAsync(
        string command,
        TimeSpan timeout,
        DateTime started,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        var deadline = started + timeout;
        var lines = new List<string>();
        var lastText = string.Empty;
        var echoConsumed = false;

        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var slice = TimeSpan.FromMilliseconds(Math.Min(500, remaining.TotalMilliseconds));
            int read;
            try
            {
                read = await _channel.ReadAsync(buffer, slice, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IoTrace?.Invoke(this, $"!!! read failed: {ex.GetType().Name} {ex.Message}");
                break;
            }

            if (read > 0)
            {
                lastText = Encoding.UTF8.GetString(buffer, 0, read);
                IoTrace?.Invoke(this, $"<<< {lastText.Replace("\r", "\\r").Replace("\n", "\\n")}");
                _pending.Append(lastText);

                // 逐行提取已完成的行。
                // 注意：ClassifyLine 的返回值语义是「该行是否已被消化、无需计入信息行」，
                // 终结行（OK / ERROR）同样返回 true。因此必须先判断 isFinal，
                // 否则会漏掉 OK 导致误判为「无响应」。
                while (TryTakeLine(out var line))
                {
                    // 模块默认开启命令回显（ATE1），首行通常是原样返回的指令。
                    // 若不剔除，单值响应（如 AT+CGMM → "QDC507"）会被回显污染成
                    // "AT+CGMM"。这里对「第一条与所发指令相同」的行做一次丢弃。
                    if (!echoConsumed &&
                        line.Equals(command, StringComparison.OrdinalIgnoreCase))
                    {
                        echoConsumed = true;
                        continue;
                    }

                    var handled = ClassifyLine(line, command, out var isFinal, out var isOk);

                    if (isFinal)
                    {
                        return new AtResult(command, isOk, lines,
                            string.Join("\n", lines), DateTime.UtcNow - started);
                    }

                    if (!handled)
                    {
                        lines.Add(line);
                    }
                }
            }
        }

        // 超时：把还未成行的残留也当作结果返回，便于排障。
        if (_pending.Length > 0)
        {
            var tail = _pending.ToString().Trim();
            if (tail.Length > 0)
            {
                lines.Add(tail);
            }
            _pending.Clear();
        }

        return new AtResult(command, false, lines, string.Join("\n", lines), DateTime.UtcNow - started);
    }

    /// <summary>
    /// 从缓冲区取出一个完整行（以 CR 或 LF 结尾）。
    /// </summary>
    private bool TryTakeLine(out string line)
    {
        var text = _pending.ToString();
        var idx = text.IndexOf('\n');
        if (idx < 0)
        {
            // 有些固件只用 CR 结尾。
            idx = text.IndexOf('\r');
            if (idx < 0)
            {
                line = string.Empty;
                return false;
            }
        }

        line = text[..idx].Trim('\r', '\n', ' ');
        _pending.Remove(0, idx + 1);
        return true;
    }

    /// <summary>
    /// 判断一行是否为终结行。返回 true 表示该行不再作为信息行收集。
    /// </summary>
    /// <param name="line">待判断的行。</param>
    /// <param name="command">当前正在执行的指令（用于区分「应答」与「主动上报」）。</param>
    /// <param name="isFinal">是否为终结行（OK / ERROR）。</param>
    /// <param name="isOk">终结行是否为成功。</param>
    private bool ClassifyLine(string line, string command, out bool isFinal, out bool isOk)
    {
        isFinal = false;
        isOk = false;

        if (line.Length == 0)
        {
            return true; // 空行丢弃
        }

        if (line.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            isFinal = true;
            isOk = true;
            return true;
        }

        if (line.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("+CMS ERROR", StringComparison.OrdinalIgnoreCase))
        {
            isFinal = true;
            isOk = false;
            return true;
        }

        // 主动上报：不属于本次指令的响应，转交订阅者。
        // 
        // 注意：+CREG/+CGREG/+CEREG/+CIEV 这类前缀既可能是主动上报，
        // 也可能正是本次查询的应答（如 AT+CREG? → +CREG: 0,5）。
        // 判据：若当前指令的正文里出现了该前缀对应的关键字，则视为应答数据。
        if (IsUnsolicited(line) && !IsReplyToCurrentCommand(line, command))
        {
            lock (_unsolicitedLock)
            {
                _unsolicitedQueue.Add(line);
            }
            UnsolicitedReport?.Invoke(this, line);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断某行是否属于当前指令的应答（而非主动上报）。
    /// 例如执行 AT+CREG? 时，+CREG: 0,5 是应答；空闲时收到 +CREG: 0,5 才是上报。
    /// </summary>
    private static bool IsReplyToCurrentCommand(string line, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        // 从响应行中取出前缀，例如 "+CREG: 0,5" -> "+CREG"；
        // 若该前缀（忽略大小写）出现在指令正文中，说明这是本次查询的应答。
        var colon = line.IndexOf(':');
        var prefix = colon > 0 ? line[..colon].Trim() : line.Trim();
        if (prefix.Length == 0)
        {
            return false;
        }

        return command.Contains(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsolicited(string line)
    {
        ReadOnlySpan<string> prefixes =
        [
            "+CMTI:", "+CMT:", "+CLIP:", "+CDS:", "+CUSD:",
            "+CREG:", "+CGREG:", "+CEREG:", "+QIND:",
            "+QLTS:", "+QUSIM:", "RING", "NO CARRIER", "+CIEV:",
        ];

        foreach (var p in prefixes)
        {
            if (line.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>取走排队中的主动上报。</summary>
    public IReadOnlyList<string> DrainUnsolicited()
    {
        lock (_unsolicitedLock)
        {
            var copy = _unsolicitedQueue.ToList();
            _unsolicitedQueue.Clear();
            return copy;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        _commandLock.Dispose();
    }
}
