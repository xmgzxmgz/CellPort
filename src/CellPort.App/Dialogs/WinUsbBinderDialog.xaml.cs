using System.Diagnostics;
using System.Windows;
using CellPort.Core.Transport;

namespace CellPort.App.Dialogs;

/// <summary>
/// WinUSB 驱动绑定引导对话框。
/// </summary>
public partial class WinUsbBinderDialog : Window
{
    public WinUsbBinderDialog()
    {
        InitializeComponent();
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

    private void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var scan = ModuleScanner.Scan();
            var text = scan.Describe();

            // 补充：串口信息
            var ports = SerialAtChannel.EnumerateCandidates();
            text += "\n\n可用串口：\n";
            text += ports.Count == 0
                ? "  （无）—— 说明尚未安装 Quectel 驱动"
                : string.Join("\n", ports.Select(p => "  " + p.PortName));

            MessageBox.Show(text, "设备诊断",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"诊断失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
