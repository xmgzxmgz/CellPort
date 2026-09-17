using System.Windows;
using System.Windows.Controls;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>
/// 通话页：拨号盘、接听/挂断、通话记录。
/// </summary>
public partial class CallsPage : UserControl, IModuleAware
{
    private ModemManager? _modem;
    private bool _muted;

    public CallsPage()
    {
        InitializeComponent();
        BuildKeypad();
    }

    public void OnActivated(ModemManager modem)
    {
        if (ReferenceEquals(_modem, modem))
        {
            return;
        }

        Detach();
        _modem = modem;
        Attach();
        ReloadHistory();
        UpdateState();
    }

    public void OnConnectionChanged(bool connected) => Dispatcher.Invoke(() =>
    {
        DialButton.IsEnabled = connected;
        UpdateState();
    });

    private void Attach()
    {
        if (_modem?.Calls is not null)
        {
            _modem.Calls.StateChanged += OnCallStateChanged;
            _modem.Calls.IncomingCall += OnIncomingCall;
        }
    }

    private void Detach()
    {
        if (_modem?.Calls is not null)
        {
            _modem.Calls.StateChanged -= OnCallStateChanged;
            _modem.Calls.IncomingCall -= OnIncomingCall;
        }
    }

    private void OnCallStateChanged(object? sender, CallState state)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateState();
            ReloadHistory();
        });
    }

    private void OnIncomingCall(object? sender, string number)
    {
        Dispatcher.Invoke(() =>
        {
            DialNumberBox.Text = number;
            CallStateText.Text = $"来电：{number}";
        });
    }

    private void UpdateState()
    {
        var state = _modem?.Calls?.State ?? CallState.Idle;
        CallStateText.Text = state switch
        {
            CallState.Idle => "空闲",
            CallState.Dialing => "拨号中…",
            CallState.Ringing => "响铃",
            CallState.Active => $"通话中{(string.IsNullOrEmpty(_modem?.Calls?.PeerNumber) ? "" : $" · {_modem!.Calls!.PeerNumber}")}",
            _ => "已结束",
        };

        HangupButton.IsEnabled = state is not CallState.Idle;
        AnswerButton.IsEnabled = state == CallState.Ringing;
    }

    /// <summary>
    /// 生成 0-9、*、# 的拨号按键。
    /// </summary>
    private void BuildKeypad()
    {
        var keys = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#" };
        foreach (var key in keys)
        {
            var btn = new Button
            {
                Content = key,
                Margin = new Thickness(2),
                FontSize = 15,
                Height = 36,
                MinWidth = 44,
                Style = (Style)FindResource("GhostButton"),
            };
            btn.Click += OnKeypadClick;
            KeypadGrid.Children.Add(btn);
        }
    }

    private async void OnKeypadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string digit })
        {
            return;
        }

        // 通话中按键走 DTMF；否则追加到号码框
        if (_modem?.Calls?.State == CallState.Active)
        {
            await _modem.Calls.SendDtmfAsync(digit[0]);
        }
        else
        {
            DialNumberBox.Text += digit;
        }
    }

    private async void Dial_Click(object sender, RoutedEventArgs e)
    {
        var number = DialNumberBox.Text.Trim();
        if (string.IsNullOrEmpty(number) || _modem?.Calls is null)
        {
            return;
        }

        var ok = await _modem.Calls.DialAsync(number);
        if (!ok)
        {
            MessageBox.Show("拨号失败。模块可能不支持语音通话，或未注册到网络。",
                "拨号失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateState();
    }

    private async void Hangup_Click(object sender, RoutedEventArgs e)
    {
        if (_modem?.Calls is null)
        {
            return;
        }
        await _modem.Calls.HangUpAsync();
        UpdateState();
        ReloadHistory();
    }

    private async void Answer_Click(object sender, RoutedEventArgs e)
    {
        if (_modem?.Calls is null)
        {
            return;
        }
        await _modem.Calls.AnswerAsync();
        UpdateState();
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_modem?.Calls is null)
        {
            return;
        }
        _muted = !_muted;
        await _modem.Calls.SetMuteAsync(_muted);
        MuteButton.Content = _muted ? "取消静音" : "静音";
    }

    private void ReloadHistory()
    {
        if (_modem?.Calls is null)
        {
            HistoryList.ItemsSource = null;
            return;
        }

        HistoryList.ItemsSource = _modem.Calls.History
            .Select(c => new CallRow
            {
                Number = c.Number,
                DirIcon = c.Direction == CallDirection.Outgoing ? "↗" : (c.Missed ? "✕" : "↙"),
                TimeText = c.Timestamp.ToString("MM-dd HH:mm"),
                DurationText = c.Duration.HasValue && c.Duration.Value.TotalSeconds > 0
                    ? FormatDuration(c.Duration.Value)
                    : "",
            })
            .ToList();
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes}:{d.Seconds:D2}";
}

/// <summary>通话记录展示行。</summary>
public sealed class CallRow
{
    /// <summary>号码。</summary>
    public required string Number { get; init; }

    /// <summary>方向图标。</summary>
    public required string DirIcon { get; init; }

    /// <summary>时间文本。</summary>
    public required string TimeText { get; init; }

    /// <summary>时长文本。</summary>
    public required string DurationText { get; init; }
}
