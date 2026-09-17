using System.Windows;
using System.Windows.Threading;
using CellPort.App.Services;
using CellPort.App.Views;
using CellPort.Core.Services;

namespace CellPort.App;

/// <summary>
/// 应用入口。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 文件日志（%LOCALAPPDATA%\CellPort\logs，保留 7 天）。
        // 初始化失败 / 写入失败均静默，绝不影响主流程。
        AppLog.Initialize();
        AppLog.Info($"App 启动（v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"}）");

        // 未处理异常兜底，避免直接崩溃退出
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            if (ex is not null)
            {
                AppLog.Error("未处理异常（非界面线程）", ex);
            }

            MessageBox.Show(
                $"发生未处理的错误：\n\n{ex?.Message}\n\n{ex?.StackTrace}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
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
        AppLog.Error("界面线程未处理异常", e.Exception);
        MessageBox.Show(
            $"界面线程发生错误：\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"App 退出（code={e.ApplicationExitCode}）");
        base.OnExit(e);
    }
}
