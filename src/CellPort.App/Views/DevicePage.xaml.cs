using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CellPort.Core.Models;
using CellPort.Core.Services;
using CellPort.Core.Transport;

namespace CellPort.App.Views;

/// <summary>
/// 设备页：模块信息、USB 接口列表、驱动引导。
/// </summary>
public partial class DevicePage : UserControl, IModuleAware
{
    private ModemManager? _modem;

    public DevicePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void OnActivated(ModemManager modem)
    {
        _modem = modem;
        Refresh();
        RefreshPorts();
    }

    public void OnConnectionChanged(bool connected) => Dispatcher.Invoke(() =>
    {
        Refresh();
        UpdateChannelStatus();
    });

    private void Refresh()
    {
        BuildInfoGrid(_modem?.Status);
        ScanUsb();
        UpdateChannelStatus();
    }

    private void UpdateChannelStatus()
    {
        if (_modem is null)
        {
            ChannelStatus.Text = "尚未连接";
            return;
        }

        ChannelStatus.Text = _modem.IsConnected
            ? $"当前通道：{_modem.ChannelDescription}"
            : "尚未连接";
    }

    /// <summary>
    /// 刷新可选串口列表。枚举放在后台线程，避免 UI 卡顿。
    /// </summary>
    private async void RefreshPorts()
    {
        var previous = PortCombo.SelectedItem as string;

        PortCombo.Items.Clear();
        PortCombo.Items.Add("（自动探测）");

        try
        {
            var ports = await Task.Run(() => SerialAtChannel.EnumerateCandidates());
            foreach (var p in ports)
            {
                PortCombo.Items.Add(p.PortName);
            }
        }
        catch
        {
            // 枚举失败时仅保留「自动探测」选项
        }

        // 尽量保持用户此前的选择
        if (previous is not null && PortCombo.Items.Contains(previous))
        {
            PortCombo.SelectedItem = previous;
        }
        else
        {
            PortCombo.SelectedIndex = 0;
        }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_modem is null)
        {
            return;
        }

        var selected = PortCombo.SelectedItem as string;
        var port = selected is null or "（自动探测）" ? null : selected;

        ReconnectButton.IsEnabled = false;
        ReconnectButton.Content = "连接中…";
        ChannelStatus.Text = port is null ? "正在自动探测串口…" : $"正在连接 {port}…";

        try
        {
            var ok = await _modem.ConnectAsync(port);
            ChannelStatus.Text = ok
                ? $"当前通道：{_modem.ChannelDescription}"
                : (port is null
                    ? "连接失败：未能自动找到 AT 串口，请手动选择后重试。"
                    : $"连接失败：{port} 无法建立 AT 通信。");
        }
        catch (Exception ex)
        {
            ChannelStatus.Text = $"连接异常：{ex.Message}";
        }
        finally
        {
            ReconnectButton.IsEnabled = true;
            ReconnectButton.Content = "重新连接";
            Refresh();
        }
    }

    private void BuildInfoGrid(DeviceStatus? s)
    {
        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();

        var rows = new (string Label, string Value)[]
        {
            ("型号", s?.Model ?? "--"),
            ("固件版本", s?.FirmwareRevision ?? "--"),
            ("IMEI", s?.Imei ?? "--"),
            ("ICCID", s?.Iccid ?? "--"),
            ("IMSI", s?.Imsi ?? "--"),
            ("本机号码", s?.OwnNumber ?? "--"),
            ("运营商", s?.Operator ?? "--"),
            ("网络制式", s?.NetworkMode ?? "--"),
            ("信号强度", s?.SignalText ?? "--"),
            ("注册状态", s?.RegistrationStatus ?? "--"),
            ("SIM 状态", s is null ? "--" : (s.SimReady ? "就绪" : "未就绪")),
            ("数据附着", s?.DataAttached switch { true => "已附着", false => "未附着", null => "--" }),
        };

        var rowCount = (rows.Length + 1) / 2;
        for (int i = 0; i < rowCount; i++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < rows.Length; i++)
        {
            var col = i % 2;
            var row = i / 2;

            var panel = new StackPanel { Margin = new Thickness(0, 4, 12, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = rows[i].Label,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextDimBrush"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = rows[i].Value,
                FontSize = 13,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBrush"),
            });

            Grid.SetColumn(panel, col);
            Grid.SetRow(panel, row);
            InfoGrid.Children.Add(panel);
        }
    }

    private void ScanUsb()
    {
        InterfacePanel.Children.Clear();

        try
        {
            var scan = ModuleScanner.Scan();

            UsbSummary.Text = scan.Found
                ? $"发现 {scan.Interfaces.Count} 个接口 · VID:PID = {scan.VendorId:X4}:{scan.ProductId:X4}" +
                  (scan.IsQuectelIdentity ? "（移远标准身份）" : "（大疆定制身份）")
                : "未发现模块接口。请确认模块已插入，且 USB 线支持数据传输。";

            foreach (var itf in scan.Interfaces.OrderBy(x => x.InterfaceNumber))
            {
                var role = itf.InterfaceNumber switch
                {
                    0 => "DIAG（诊断）",
                    1 => "NMEA（GNSS）",
                    2 => "AT（指令通道）",
                    3 => "AT（备用）",
                    4 => "RMNET/ECM（数据网卡）",
                    _ => "未知用途",
                };

                var isKey = itf.IsAtChannel;

                var row = new Border
                {
                    Background = (Brush)FindResource(isKey ? "AccentSoftBrush" : "CardHoverBrush"),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 3, 0, 3),
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var miText = new TextBlock
                {
                    Text = $"MI_{itf.InterfaceNumber:D2}",
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource(isKey ? "AccentBrush" : "TextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(miText, 0);
                grid.Children.Add(miText);

                var roleText = new TextBlock
                {
                    Text = role,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(roleText, 1);
                grid.Children.Add(roleText);

                if (isKey)
                {
                    var badge = new Border
                    {
                        Background = (Brush)FindResource("AccentBrush"),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    badge.Child = new TextBlock
                    {
                        Text = "关键",
                        FontSize = 10,
                        Foreground = Brushes.White,
                    };
                    Grid.SetColumn(badge, 2);
                    grid.Children.Add(badge);
                }

                row.Child = grid;
                InterfacePanel.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            UsbSummary.Text = $"扫描失败：{ex.Message}";
        }
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        _ = _modem?.RefreshStatusAsync();
    }

    private void OpenDevMgr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开设备管理器：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenBinder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Dialogs.WinUsbBinderDialog { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
}
