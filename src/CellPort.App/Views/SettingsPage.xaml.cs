using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CellPort.App.Services;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>
/// 设置页：外观、连接、短信转发。
/// </summary>
public partial class SettingsPage : UserControl, IModuleAware
{
    private ModemManager? _modem;

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
    }

    public void OnActivated(ModemManager modem) => _modem = modem;

    public void OnConnectionChanged(bool connected) { }

    private void LoadSettings()
    {
        var s = AppSettings.Current;
        ThemeBox.SelectedIndex = s.ThemeIndex;
        PrivacyBox.IsChecked = s.PrivacyMode;
        AutoConnectBox.IsChecked = s.AutoConnect;
        RefreshIntervalBox.Text = s.RefreshIntervalSeconds.ToString();
        BarkBox.Text = s.BarkUrl ?? "";
        FeishuBox.Text = s.FeishuWebhook ?? "";
        DingTalkBox.Text = s.DingTalkWebhook ?? "";
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var index = ThemeBox.SelectedIndex;
        var kind = index switch
        {
            1 => AppTheme.Dark,
            2 => AppTheme.Light,
            _ => AppTheme.System,
        };

        ThemeManager.Apply(kind);
        AppSettings.Current.ThemeIndex = index;
        AppSettings.Current.Save();
    }

    private void Privacy_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        AppSettings.Current.PrivacyMode = PrivacyBox.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        s.AutoConnect = AutoConnectBox.IsChecked == true;
        s.BarkUrl = NullIfEmpty(BarkBox.Text);
        s.FeishuWebhook = NullIfEmpty(FeishuBox.Text);
        s.DingTalkWebhook = NullIfEmpty(DingTalkBox.Text);

        if (int.TryParse(RefreshIntervalBox.Text, out var sec) && sec >= 1 && sec <= 3600)
        {
            s.RefreshIntervalSeconds = sec;
        }
        else
        {
            RefreshIntervalBox.Text = s.RefreshIntervalSeconds.ToString();
        }

        s.Save();
        MessageBox.Show("设置已保存。", "提示",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// 应用设置，持久化到用户目录下的 JSON 文件。
/// </summary>
public sealed class AppSettings
{
    private static AppSettings? _current;

    /// <summary>当前设置实例。</summary>
    public static AppSettings Current => _current ??= Load();

    /// <summary>主题索引：0 跟随系统，1 深色，2 浅色。</summary>
    public int ThemeIndex { get; set; }

    /// <summary>隐私模式。</summary>
    public bool PrivacyMode { get; set; }

    /// <summary>启动时自动连接。</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>状态刷新间隔（秒）。</summary>
    public int RefreshIntervalSeconds { get; set; } = 5;

    /// <summary>Bark 推送地址。</summary>
    public string? BarkUrl { get; set; }

    /// <summary>飞书机器人 Webhook。</summary>
    public string? FeishuWebhook { get; set; }

    /// <summary>钉钉机器人 Webhook。</summary>
    public string? DingTalkWebhook { get; set; }

    /// <summary>
    /// 上次成功连接的 AT 串口（如 "COM6"）。
    /// 持久化的意义：模块热插拔或驱动重装后 COM 号会漂移，但通常仍落在少数几个口上；
    /// 缓存后可让下次启动「秒连」，无需再走全量端口预筛。
    /// </summary>
    public string? LastGoodSerialPort { get; set; }

    /// <summary>上次成功连接的时间（本地时间，仅作展示）。</summary>
    public DateTime? LastConnectedAt { get; set; }

    private static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CellPort");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时回退到默认值
        }
        return new AppSettings();
    }

    /// <summary>写入磁盘。</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 写入失败不阻断界面
        }
    }
}
