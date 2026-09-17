using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CellPort.Core.At;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>
/// 短信页：左侧会话列表 + 右侧对话流 + 底部发送区。
/// </summary>
public partial class SmsPage : UserControl, IModuleAware
{
    private ModemManager? _modem;
    private readonly Dictionary<string, List<SmsMessage>> _threads = [];
    private string? _activeNumber;

    public SmsPage()
    {
        InitializeComponent();
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
        Reload();
    }

    public void OnConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            SendButton.IsEnabled = connected;
            if (connected)
            {
                Reload();
            }
        });
    }

    private void Attach()
    {
        if (_modem?.Sms is not null)
        {
            _modem.Sms.MessageReceived += OnMessageReceived;
        }
    }

    private void Detach()
    {
        if (_modem?.Sms is not null)
        {
            _modem.Sms.MessageReceived -= OnMessageReceived;
        }
    }

    private void OnMessageReceived(object? sender, SmsMessage msg)
    {
        Dispatcher.Invoke(() =>
        {
            Reload();
            // 若正在查看该会话，追加到对话流
            if (_activeNumber is not null && SameNumber(_activeNumber, msg.Number))
            {
                AppendBubble(msg);
                MessageScroller.ScrollToEnd();
            }
        });
    }

    private void Reload()
    {
        _threads.Clear();

        if (_modem?.Sms is null)
        {
            ThreadList.ItemsSource = null;
            return;
        }

        foreach (var msg in _modem.Sms.Inbox)
        {
            var key = NormalizeForThread(msg.Number);
            if (!_threads.TryGetValue(key, out var list))
            {
                list = [];
                _threads[key] = list;
            }
            list.Add(msg);
        }

        var items = _threads
            .Select(kv =>
            {
                var last = kv.Value.OrderByDescending(m => m.Timestamp ?? DateTime.MinValue).First();
                return new ThreadItem
                {
                    Number = last.Number,
                    Key = kv.Key,
                    Preview = last.Body,
                    Time = last.Timestamp,
                    Count = kv.Value.Count,
                    HasCode = kv.Value.Any(m => m.VerificationCode is not null),
                };
            })
            .OrderByDescending(t => t.Time ?? DateTime.MinValue)
            .ToList();

        ThreadList.ItemsSource = items;

        if (items.Count > 0 && _activeNumber is null)
        {
            ThreadList.SelectedIndex = 0;
        }
    }

    private void Thread_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThreadList.SelectedItem is not ThreadItem item)
        {
            return;
        }

        _activeNumber = item.Key;
        DetailNumber.Text = item.Number;
        DetailMeta.Text = $"{item.Count} 条消息";
        NumberBox.Text = item.Number;

        MessagePanel.Children.Clear();
        if (_threads.TryGetValue(item.Key, out var list))
        {
            foreach (var msg in list.OrderBy(m => m.Timestamp ?? DateTime.MinValue))
            {
                AppendBubble(msg);
            }
        }

        Dispatcher.BeginInvoke(() => MessageScroller.ScrollToEnd());
    }

    /// <summary>
    /// 追加一条消息气泡。
    /// </summary>
    private void AppendBubble(SmsMessage msg)
    {
        var outgoing = msg.Direction == SmsDirection.Outgoing;

        var bubble = new Border
        {
            Background = (Brush)FindResource(outgoing ? "BubbleOutBrush" : "BubbleInBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = 420,
            Margin = new Thickness(0, 3, 0, 3),
            HorizontalAlignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        var stack = new StackPanel();

        // 验证码高亮
        if (!outgoing && msg.VerificationCode is not null)
        {
            var codeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6),
            };
            codeRow.Children.Add(new TextBlock
            {
                Text = msg.VerificationCode,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var copyBtn = new Button
            {
                Content = "复制",
                Margin = new Thickness(10, 0, 0, 0),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 11,
                Style = (Style)FindResource("GhostButton"),
            };
            var code = msg.VerificationCode;
            copyBtn.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(code);
                }
                catch
                {
                    // 剪贴板被占用时忽略
                }
            };
            codeRow.Children.Add(copyBtn);
            stack.Children.Add(codeRow);
        }

        stack.Children.Add(new TextBlock
        {
            Text = msg.Body,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = outgoing
                ? Brushes.White
                : (Brush)FindResource("TextBrush"),
        });

        if (msg.Timestamp.HasValue)
        {
            stack.Children.Add(new TextBlock
            {
                Text = msg.Timestamp.Value.ToString("MM-dd HH:mm"),
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = outgoing
                    ? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                    : (Brush)FindResource("TextDimBrush"),
            });
        }

        bubble.Child = stack;
        MessagePanel.Children.Add(bubble);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await SendAsync();
    }

    private async void Body_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter 发送
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var number = NumberBox.Text.Trim();
        var body = BodyBox.Text.Trim();

        if (string.IsNullOrEmpty(number))
        {
            MessageBox.Show("请填写目标号码。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrEmpty(body))
        {
            return;
        }

        if (_modem?.Sms is null)
        {
            MessageBox.Show("模块未连接。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SendButton.IsEnabled = false;
        SendButton.Content = "发送中…";

        try
        {
            var ok = await _modem.Sms.SendAsync(number, body);
            if (ok)
            {
                BodyBox.Clear();
                Reload();
            }
            else
            {
                MessageBox.Show("发送失败。请检查信号与余额，或查看 AT 控制台日志。",
                    "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"发送异常：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SendButton.Content = "发送";
            SendButton.IsEnabled = _modem?.IsConnected == true;
        }
    }

    private void NewSms_Click(object sender, RoutedEventArgs e)
    {
        _activeNumber = null;
        ThreadList.SelectedItem = null;
        DetailNumber.Text = "新短信";
        DetailMeta.Text = "填写号码后发送";
        NumberBox.Clear();
        BodyBox.Clear();
        MessagePanel.Children.Clear();
        NumberBox.Focus();
    }

    private void Call_Click(object sender, RoutedEventArgs e)
    {
        var number = NumberBox.Text.Trim();
        if (string.IsNullOrEmpty(number) || _modem?.Calls is null)
        {
            return;
        }

        _ = _modem.Calls.DialAsync(number);
        MessageBox.Show($"正在拨打 {number}…\n\n提示：语音通话需模块固件与运营商支持 VoLTE，" +
                        "部分环境下可能无法建立音频通路。",
            "拨号", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string NormalizeForThread(string number) =>
        new([.. number.Where(char.IsDigit)]);

    private static bool SameNumber(string a, string b) =>
        NormalizeForThread(a) == NormalizeForThread(b);
}

/// <summary>会话列表项。</summary>
public sealed class ThreadItem
{
    /// <summary>用于分组的号码键。</summary>
    public required string Key { get; init; }

    /// <summary>展示用号码。</summary>
    public required string Number { get; init; }

    /// <summary>最后一条消息预览。</summary>
    public required string Preview { get; init; }

    /// <summary>最后一条消息时间。</summary>
    public DateTime? Time { get; init; }

    /// <summary>消息条数。</summary>
    public int Count { get; init; }

    /// <summary>是否含验证码。</summary>
    public bool HasCode { get; init; }

    public string TimeText => Time?.ToString("MM-dd HH:mm") ?? "";
}
