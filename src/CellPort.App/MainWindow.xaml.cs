using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CellPort.App.Views;
using CellPort.Core.Services;

namespace CellPort.App;

/// <summary>
/// 主窗口：侧栏导航 + 页面切换 + 连接状态管理。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ModemManager _modem = new();
    private readonly Dictionary<string, UserControl> _pages = [];
    private readonly DispatcherTimer _refreshTimer = new();
    private string _currentPage = "Sms";

    /// <summary>
    /// 本次进程内是否已成功连接过。
    /// 用于把「开机自动连接失败」与「连接掉线/被断开」两种状态区分开，
    /// 避免前者误报为「未发现模块」。
    /// </summary>
    private bool _everConnected;

    public MainWindow()
    {
        InitializeComponent();

        // 把上次成功的 AT 口喂给 Core，让启动时优先尝试该口（秒连），
        // 同时注册回写回调，把新的成功端口持久化。
        _modem.PrimePortHints([CellPort.App.Views.AppSettings.Current.LastGoodSerialPort]);
        _modem.RememberPort = port =>
        {
            var s = CellPort.App.Views.AppSettings.Current;
            s.LastGoodSerialPort = port;
            s.LastConnectedAt = DateTime.Now;
            s.Save();
        };

        // 连接状态与状态刷新
        _modem.ConnectionChanged += OnConnectionChanged;
        _modem.StatusUpdated += OnStatusUpdated;
        _modem.TraceMessage += OnTraceMessage;

        // 周期性刷新模块状态
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_modem.IsConnected)
            {
                await _modem.RefreshStatusAsync();
            }
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>全局共享的模块管理器。</summary>
    public ModemManager Modem => _modem;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowPage("Sms");

        // 先处理 --page，避免自动连接期间的页面切换把它覆盖掉
        ApplyStartupPageFromArgs();

        if (CellPort.App.Views.AppSettings.Current.AutoConnect)
        {
            await TryAutoConnectAsync();
        }

        _refreshTimer.Start();
    }

    /// <summary>
    /// 支持 <c>CellPort.exe --page esim</c> 直接打开指定页面，便于排查与演示。
    /// 取值：sms / calls / device / esim / console / settings（大小写不敏感）。
    /// </summary>
    private void ApplyStartupPageFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--page", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tag = args[i + 1] switch
            {
                var s when s.Equals("sms", StringComparison.OrdinalIgnoreCase) => "Sms",
                var s when s.Equals("calls", StringComparison.OrdinalIgnoreCase) => "Calls",
                var s when s.Equals("device", StringComparison.OrdinalIgnoreCase) => "Device",
                var s when s.Equals("esim", StringComparison.OrdinalIgnoreCase) => "Esim",
                var s when s.Equals("console", StringComparison.OrdinalIgnoreCase) => "Console",
                var s when s.Equals("settings", StringComparison.OrdinalIgnoreCase) => "Settings",
                _ => null,
            };

            if (tag is null)
            {
                return;
            }

            // 直接切换页面，并只把目标项设为选中（同组 RadioButton 互斥，
            // 其余项会自动取消；不要遍历赋值，否则最后一次赋值会覆盖结果）。
            ShowPage(tag);
            foreach (var nav in new[] { NavSms, NavCalls, NavDevice, NavEsim, NavConsole, NavSettings })
            {
                if (nav.Tag as string == tag)
                {
                    nav.IsChecked = true;
                    break;
                }
            }

            return;
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        await _modem.DisposeAsync();
    }

    /// <summary>
    /// 启动时自动尝试连接一次。
    /// 
    /// 注意：失败时的提示文案必须谨慎 —— 用户看到的「未发现模块」若与事实不符，
    /// 会把人引向「检查硬件/装驱动」的错误方向。这里改为给出可操作的建议，
    /// 并把详细的连接过程日志写下来，便于排查。
    /// </summary>
    private async Task TryAutoConnectAsync()
    {
        SetConnecting(true);
        try
        {
            var ok = await _modem.ConnectAsync();
            if (!ok)
            {
                UpdateConnectionUi(false, "未自动连接");
                StatusHint.Text = "自动连接未成功，请点击左下角「连接模块」重试。";
            }
        }
        catch (Exception ex)
        {
            UpdateConnectionUi(false, "连接失败");
            StatusHint.Text = $"连接异常：{ex.Message}";
        }
        finally
        {
            SetConnecting(false);
            FlushConnectLog();
        }
    }

    /// <summary>本次连接过程的日志缓冲。</summary>
    private readonly System.Text.StringBuilder _connectLog = new();

    private void OnTraceMessage(object? sender, string message)
    {
        lock (_connectLog)
        {
            _connectLog.AppendLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        }
    }

    private void FlushConnectLog()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CellPort");
            System.IO.Directory.CreateDirectory(dir);
            lock (_connectLog)
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "last-connect.log"),
                    _connectLog.ToString());
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_modem.IsConnected)
        {
            await _modem.DisconnectAsync();
            UpdateConnectionUi(false, "未连接");
            StatusHint.Text = "已断开。点击「连接模块」可重新连接。";
            return;
        }

        SetConnecting(true);
        try
        {
            var ok = await _modem.ConnectAsync();
            if (!ok)
            {
                var scan = Core.Transport.ModuleScanner.Scan(bypassCache: true);

                var result = MessageBox.Show(
                    "未能连接到模块。\n\n" +
                    (scan.Found
                        ? "模块已被系统识别，但 AT 通道未能建立。\n" +
                          "常见原因：AT 口被其它程序（Quectel 工具 / 串口调试助手）占用。\n\n"
                        : "系统未识别到模块。\n" +
                          "请检查：USB 线是否支持数据传输、驱动是否已安装、SIM 卡是否插好。\n\n") +
                    "是否查看详细的设备诊断信息？",
                    "连接失败",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    ShowDiagnostics();
                }
                UpdateConnectionUi(false, "未连接");
                StatusHint.Text = "未连接。请确认模块状态后重试。";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接时发生错误：\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetConnecting(false);
            FlushConnectLog();
        }
    }

    private void ShowDiagnostics()
    {
        try
        {
            var scan = Core.Transport.ModuleScanner.Scan();
            MessageBox.Show(scan.Describe(), "设备诊断",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"诊断失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetConnecting(bool connecting)
    {
        ConnectButton.IsEnabled = !connecting;
        ConnectButton.Content = connecting ? "连接中…" : 
            (_modem.IsConnected ? "断开连接" : "连接模块");
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                _everConnected = true;
            }

            UpdateConnectionUi(connected, connected ? _modem.ChannelDescription : "未连接");
            ConnectButton.Content = connected ? "断开连接" : "连接模块";

            StatusHint.Text = connected
                ? "已连接。串口号会被记住，下次启动可直接复用。"
                : (_everConnected
                    ? "连接已断开。点击「连接模块」重新连接。"
                    : "点击下方按钮连接模块。");

            // 通知各页面连接状态变化
            foreach (var page in _pages.Values)
            {
                if (page is IModuleAware aware)
                {
                    aware.OnConnectionChanged(connected);
                }
            }
        });
    }

    private void OnStatusUpdated(object? sender, Core.Models.DeviceStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            SignalText.Text = status.SignalText;

            OperatorText.Text = string.IsNullOrWhiteSpace(status.Operator)
                ? "--"
                : status.Operator;

            PageSubtitle.Text = _modem.IsConnected
                ? $"{status.Model} · {(status.SimReady ? "SIM 就绪" : "SIM 未就绪")}"
                : "未连接模块";
        });
    }

    private void UpdateConnectionUi(bool connected, string text)
    {
        ConnDot.Fill = (Brush)FindResource(connected ? "SuccessBrush" : "DangerBrush");
        ConnText.Text = text;

        if (!connected)
        {
            SignalText.Text = "--";
            OperatorText.Text = "--";
        }

        BrandSubtitle.Text = connected ? "已连接" : "QDC507 管理器";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        _currentPage = tag;

        PageTitle.Text = tag switch
        {
            "Sms" => "短信",
            "Calls" => "通话",
            "Device" => "设备",
            "Esim" => "eSIM",
            "Console" => "AT 控制台",
            "Settings" => "设置",
            _ => tag,
        };

        if (!_pages.TryGetValue(tag, out var page))
        {
            page = CreatePage(tag);
            _pages[tag] = page;
        }

        PageHost.Content = page;

        if (page is IModuleAware aware)
        {
            aware.OnActivated(_modem);
        }
    }

    private UserControl CreatePage(string tag) => tag switch
    {
        "Sms" => new SmsPage(),
        "Calls" => new CallsPage(),
        "Device" => new DevicePage(),
        "Esim" => new EsimPage(),
        "Console" => new ConsolePage(),
        "Settings" => new SettingsPage(),
        _ => new SmsPage(),
    };
}

/// <summary>
/// 页面可实现此接口以感知模块连接状态与激活事件。
/// </summary>
public interface IModuleAware
{
    /// <summary>页面被激活时调用。</summary>
    void OnActivated(ModemManager modem);

    /// <summary>连接状态变化时调用。</summary>
    void OnConnectionChanged(bool connected);
}
