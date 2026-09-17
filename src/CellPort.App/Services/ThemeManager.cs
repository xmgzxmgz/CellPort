using System.Windows;
using Microsoft.Win32;

namespace CellPort.App.Services;

/// <summary>主题类型。</summary>
public enum AppTheme
{
    /// <summary>跟随系统。</summary>
    System,
    /// <summary>深色。</summary>
    Dark,
    /// <summary>浅色。</summary>
    Light,
}

/// <summary>
/// 主题切换：替换应用级资源字典中的颜色定义。
/// </summary>
public static class ThemeManager
{
    private const string ThemeDictKey = "ThemeOverride";

    private static readonly Uri LightUri =
        new("pack://application:,,,/Themes/Theme.Light.xaml", UriKind.Absolute);

    /// <summary>
    /// 应用主题。
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var effective = theme == AppTheme.System ? DetectSystemTheme() : theme;

        // 移除已有的覆盖字典
        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Contains("BgColor") && !ReferenceEquals(d, app.Resources.MergedDictionaries[0]));
        if (existing is not null)
        {
            app.Resources.MergedDictionaries.Remove(existing);
        }

        if (effective == AppTheme.Light)
        {
            var dict = new ResourceDictionary { Source = LightUri };
            app.Resources.MergedDictionaries.Add(dict);
        }

        // 更新所有 DynamicResource 引用
        foreach (Window window in app.Windows)
        {
            window.InvalidateVisual();
        }
    }

    /// <summary>
    /// 读取系统主题偏好。
    /// </summary>
    public static AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i)
            {
                return i == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch
        {
            // 读取失败按深色处理
        }
        return AppTheme.Dark;
    }
}
