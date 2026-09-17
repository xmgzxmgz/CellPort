using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>
/// AT 控制台：直接向模块发送指令并查看响应。
/// </summary>
public partial class ConsolePage : UserControl, IModuleAware
{
    private ModemManager? _modem;
    private readonly StringBuilder _log = new();
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    private static readonly string[] QuickCommands =
    [
        "AT", "ATI", "AT+CGMM", "AT+CGMR", "AT+CGSN", "AT+CPIN?", "AT+CSQ",
        "AT+COPS?", "AT+CREG?", "AT+QNWINFO", "AT+CGATT?", "AT+QCCID", "AT+CIMI",
        "AT+CNUM", "AT+CMGF?", "AT+CPMS?", "AT+QCFG=\"usbnet\"", "AT+QCFG=\"usbcfg\"",
        "AT+CFUN?", "AT+QENG=\"servingcell\"",
    ];

    public ConsolePage()
    {
        InitializeComponent();
        BuildQuickButtons();
        Append("info", "AT 控制台就绪。输入指令后回车执行。");
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
    }

    public void OnConnectionChanged(bool connected) => Dispatcher.Invoke(() =>
        RunButton.IsEnabled = connected);

    private void Attach()
    {
        if (_modem is not null)
        {
            _modem.UnsolicitedLine += OnUnsolicited;
        }
    }

    private void Detach()
    {
        if (_modem is not null)
        {
            _modem.UnsolicitedLine -= OnUnsolicited;
        }
    }

    private void OnUnsolicited(object? sender, string line)
    {
        Dispatcher.Invoke(() => Append("event", $"⬅ {line}"));
    }

    private void BuildQuickButtons()
    {
        foreach (var cmd in QuickCommands)
        {
            var btn = new Button
            {
                Content = cmd,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11,
                Height = 24,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 0, 8, 0),
                Style = (Style)FindResource("GhostButton"),
            };
            btn.Click += (_, _) =>
            {
                CommandBox.Text = cmd;
                CommandBox.Focus();
            };
            QuickPanel.Children.Add(btn);
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await ExecuteAsync();

    private async void Command_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ExecuteAsync();
            return;
        }

        // 上下键翻阅历史
        if (e.Key == Key.Up && _history.Count > 0)
        {
            e.Handled = true;
            _historyIndex = Math.Max(0, _historyIndex - 1);
            CommandBox.Text = _history[_historyIndex];
            CommandBox.CaretIndex = CommandBox.Text.Length;
        }
        else if (e.Key == Key.Down && _history.Count > 0)
        {
            e.Handled = true;
            _historyIndex = Math.Min(_history.Count - 1, _historyIndex + 1);
            CommandBox.Text = _history[_historyIndex];
            CommandBox.CaretIndex = CommandBox.Text.Length;
        }
    }

    private async Task ExecuteAsync()
    {
        var cmd = CommandBox.Text.Trim();
        if (string.IsNullOrEmpty(cmd))
        {
            return;
        }

        if (_modem is null || !_modem.IsConnected)
        {
            Append("error", "模块未连接。");
            return;
        }

        _history.Add(cmd);
        _historyIndex = _history.Count;
        CommandBox.Clear();

        Append("send", $"➡ {cmd}");

        try
        {
            var result = await _modem.ExecuteAtAsync(cmd);
            foreach (var line in result.Lines)
            {
                Append("recv", $"⬅ {line}");
            }
            Append(result.Succeeded ? "ok" : "error",
                result.Succeeded
                    ? $"OK  ({result.Duration.TotalMilliseconds:F0} ms)"
                    : $"FAILED  ({result.Duration.TotalMilliseconds:F0} ms)");
        }
        catch (Exception ex)
        {
            Append("error", ex.Message);
        }
    }

    /// <summary>
    /// 追加一行带颜色标记的日志。
    /// </summary>
    private void Append(string kind, string text)
    {
        var prefix = kind switch
        {
            "send" => "$ ",
            "recv" => "  ",
            "ok" => "  ",
            "error" => "  ",
            "event" => "  ",
            _ => "  ",
        };

        _log.AppendLine(prefix + text);

        // 限制日志长度，避免长时间运行占用过高
        if (_log.Length > 200_000)
        {
            _log.Remove(0, 100_000);
        }

        OutputText.Text = _log.ToString();

        OutputText.Foreground = (Brush)FindResource("TextBrush");
        Dispatcher.BeginInvoke(() => OutputScroller.ScrollToEnd());
    }
}
