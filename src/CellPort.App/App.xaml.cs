using System.Windows;
using System.Windows.Threading;
using CellPort.App.Services;
using CellPort.App.Views;

namespace CellPort.App;

/// <summary>
/// 应用入口。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未处理异常兜底，避免直接崩溃退出
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show(
                $"发生未处理的错误：\n\n{ex?.Message}\n\n{ex?.StackTrace}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // 恢复上次主题设置
        var settings = AppSettings.Current;
        ThemeManager.Apply(settings.ThemeIndex switch
        {
            1 => AppTheme.Dark,
            2 => AppTheme.Light,
            _ => AppTheme.System,
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"界面线程发生错误：\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
