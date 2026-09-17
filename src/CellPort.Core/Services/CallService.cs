using System.Text.RegularExpressions;
using CellPort.Core.At;

namespace CellPort.Core.Services;

/// <summary>通话记录条目。</summary>
public sealed class CallRecord
{
    /// <summary>对方号码。</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>通话方向。</summary>
    public CallDirection Direction { get; set; }

    /// <summary>开始时间。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>通话时长。</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>是否未接。</summary>
    public bool Missed => Direction == CallDirection.Incoming && Duration is null or { TotalSeconds: <= 0 };
}

/// <summary>通话方向。</summary>
public enum CallDirection
{
    /// <summary>呼出。</summary>
    Outgoing,
    /// <summary>呼入。</summary>
    Incoming,
}

/// <summary>通话状态。</summary>
public enum CallState
{
    /// <summary>空闲。</summary>
    Idle,
    /// <summary>拨号中。</summary>
    Dialing,
    /// <summary>响铃（呼入或回铃）。</summary>
    Ringing,
    /// <summary>通话中。</summary>
    Active,
    /// <summary>已挂断。</summary>
    Ended,
}

/// <summary>
/// 语音通话服务。通过 AT 指令控制拨号、接听、挂断，并解析 URC 通话状态。
/// </summary>
public sealed class CallService(AtEngine engine)
{
    private readonly AtEngine _engine = engine;
    private readonly List<CallRecord> _history = [];
    private readonly object _lock = new();

    private CallState _state = CallState.Idle;
    private string? _peerNumber;
    private DateTime? _callStartedAt;

    /// <summary>通话状态变化。</summary>
    public event EventHandler<CallState>? StateChanged;

    /// <summary>收到来电。</summary>
    public event EventHandler<string>? IncomingCall;

    /// <summary>当前状态。</summary>
    public CallState State => _state;

    /// <summary>当前对端号码。</summary>
    public string? PeerNumber => _peerNumber;

    /// <summary>通话记录。</summary>
    public IReadOnlyList<CallRecord> History
    {
        get
        {
            lock (_lock)
            {
                return [.. _history.OrderByDescending(c => c.Timestamp)];
            }
        }
    }

    /// <summary>初始化：开启来电显示与通话状态上报。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SafeAsync("AT+CLIP=1", ct).ConfigureAwait(false);   // 来电号码显示
        await SafeAsync("AT+CRC=1", ct).ConfigureAwait(false);    // 区分语音/数据呼叫
        await SafeAsync("AT+CMOD=0", ct).ConfigureAwait(false);   // 单模
    }

    /// <summary>拨号。</summary>
    public async Task<bool> DialAsync(string number, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return false;
        }

        var normalized = new string([.. number.Where(c => char.IsDigit(c) || c == '+' || c == '*'
            || c == '#')]);
        if (normalized.Length == 0)
        {
            return false;
        }

        SetState(CallState.Dialing);
        _peerNumber = normalized;

        // 语音呼叫需带 ";"
        var r = await SafeAsync($"ATD{normalized};", ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (r is null)
        {
            SetState(CallState.Idle);
            _peerNumber = null;
            return false;
        }

        _callStartedAt = DateTime.Now;
        return true;
    }

    /// <summary>接听来电。</summary>
    public async Task<bool> AnswerAsync(CancellationToken ct = default)
    {
        var r = await SafeAsync("ATA", ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (r is not null)
        {
            _callStartedAt = DateTime.Now;
            SetState(CallState.Active);
            return true;
        }
        return false;
    }

    /// <summary>挂断。</summary>
    public async Task<bool> HangUpAsync(CancellationToken ct = default)
    {
        var r = await SafeAsync("AT+CHUP", ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        RecordCurrentCall();
        SetState(CallState.Idle);
        _peerNumber = null;
        return r is not null;
    }

    /// <summary>发送 DTMF（通话中按键）。</summary>
    public async Task<bool> SendDtmfAsync(char digit, CancellationToken ct = default)
    {
        var r = await SafeAsync($"AT+VTS={digit}", ct).ConfigureAwait(false);
        return r is not null;
    }

    /// <summary>静音控制（部分固件支持）。</summary>
    public async Task<bool> SetMuteAsync(bool mute, CancellationToken ct = default)
    {
        var r = await SafeAsync($"AT+CMUT={(mute ? 1 : 0)}", ct).ConfigureAwait(false);
        return r is not null;
    }

    /// <summary>
    /// 处理来自模块的主动上报。返回 true 表示已消费该行。
    /// </summary>
    public bool HandleUnsolicited(string line)
    {
        // RING —— 来电振铃
        if (line.Equals("RING", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CallState.Ringing);
            return true;
        }

        // +CLIP: "+8613800138000",145,...
        if (line.StartsWith("+CLIP:", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(line, "\"([^\"]+)\"");
            if (m.Success)
            {
                _peerNumber = m.Groups[1].Value;
                SetState(CallState.Ringing);
                IncomingCall?.Invoke(this, _peerNumber);
            }
            return true;
        }

        // +CRC: 1,1  或  +CIEV 通话状态变化
        if (line.StartsWith("+CRC:", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CallState.Ringing);
            return true;
        }

        if (line.StartsWith("+CIEV:", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(line, @",\s*(\d+)\s*$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var callState))
            {
                SetState(callState switch
                {
                    0 => CallState.Idle,
                    1 => CallState.Active,
                    _ => _state,
                });
                if (callState == 0)
                {
                    RecordCurrentCall();
                    _peerNumber = null;
                }
            }
            return true;
        }

        // NO CARRIER —— 对方挂断
        if (line.StartsWith("NO CARRIER", StringComparison.OrdinalIgnoreCase))
        {
            RecordCurrentCall();
            SetState(CallState.Idle);
            _peerNumber = null;
            return true;
        }

        // BUSY / NO ANSWER / NO DIALTONE
        if (line.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("NO ANSWER", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("NO DIALTONE", StringComparison.OrdinalIgnoreCase))
        {
            RecordCurrentCall();
            SetState(CallState.Idle);
            _peerNumber = null;
            return true;
        }

        return false;
    }

    private void SetState(CallState state)
    {
        if (_state == state)
        {
            return;
        }
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void RecordCurrentCall()
    {
        if (string.IsNullOrEmpty(_peerNumber))
        {
            return;
        }

        var duration = _callStartedAt.HasValue
            ? DateTime.Now - _callStartedAt.Value
            : (TimeSpan?)null;

        lock (_lock)
        {
            _history.Add(new CallRecord
            {
                Number = _peerNumber,
                Direction = _state == CallState.Dialing ? CallDirection.Outgoing : CallDirection.Incoming,
                Timestamp = _callStartedAt ?? DateTime.Now,
                Duration = duration,
            });
        }

        _callStartedAt = null;
    }

    private async Task<AtResult?> SafeAsync(string cmd, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            var r = await _engine.ExecuteAsync(cmd, timeout, ct).ConfigureAwait(false);
            return r.Succeeded ? r : null;
        }
        catch
        {
            return null;
        }
    }
}
