using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CellPort.Core.Models;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>
/// eSIM 页：读取 eUICC 的 EID 与 Profile 列表。
///
/// 读取通道（与 CellDock / lpac 对齐）：
///   优先走 3GPP 逻辑通道指令 AT+CCHO / AT+CGLA —— CCHO 由模块固件内部完成
///   ISD-R 选择，避开本固件 AT+CSIM 透传的约 8 字节长度上限；
///   仅在固件不支持逻辑通道指令时回退 CSIM 诊断。
///
///   探测结论区分「模块通道问题」与「卡片非 eUICC」，避免误导。
/// </summary>
public partial class EsimPage : UserControl, IModuleAware
{
    private ModemManager? _modem;

    public EsimPage()
    {
        InitializeComponent();
    }

    public void OnActivated(ModemManager modem) => _modem = modem;

    public void OnConnectionChanged(bool connected) => Dispatcher.Invoke(() =>
    {
        RefreshButton.IsEnabled = connected;
        if (!connected)
        {
            ProfilePanel.Children.Clear();
            ProfileHint.Text = "模块未连接。请先在左下角连接模块。";
        }
    });

    private bool _busy;

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_modem is null || !_modem.IsConnected)
        {
            ProfileHint.Text = "模块未连接。";
            return;
        }

        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "读取中…";
        ProfilePanel.Children.Clear();
        ProfileHint.Text = "正在打开 eUICC 逻辑通道…";

        try
        {
            IccidText.Text = _modem.Status.Iccid ?? "--";

            var result = _modem.Info is null
                ? new DeviceInfoService.EuiccProbeResult(EuiccCapability.Unknown, null, [], null)
                : await _modem.Info.ProbeEuiccAsync();

            switch (result.Capability)
            {
                case EuiccCapability.Ready:
                    RenderReady(result);
                    break;

                case EuiccCapability.ApduLengthLimited:
                    ShowModuleLimit();
                    break;

                case EuiccCapability.IsdrNotFound:
                    ShowCardNotEuicc(result.Detail);
                    break;

                default:
                    ShowChannelIssue();
                    break;
            }

            // 无论结论如何，都把卡片本身的真实信息展示出来
            AddCardInfo();
        }
        catch (Exception ex)
        {
            ProfileHint.Text = $"读取失败：{ex.Message}";
        }
        finally
        {
            RefreshButton.Content = "读取";
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>渲染 Ready 结论：EID 说明 + Profile 卡片（带启用 / 停用按钮）。</summary>
    private void RenderReady(DeviceInfoService.EuiccProbeResult result)
    {
        if (string.IsNullOrEmpty(result.Eid))
        {
            EidText.Text = "此卡未提供（ISD-R 不支持读取）";
            ProfilePanel.Children.Add(MakeCard(
                "EID 无法读取（仅影响显示，不影响 Profile 管理）",
                "已成功打开 ISD-R 逻辑通道并读取 Profile，但该卡的 ISD-R 对两种 EID 读取方式"
                + "（SGP.22 ES10c GetEuiccData BF3E / SGP.02 GET DATA 9F7F）均返回错误。\n\n"
                + "这常见于 9eSIM 等 SGP.02 早期规范的可写卡；CellDock 在此类卡上同样读不到 EID。",
                null));
        }
        else
        {
            EidText.Text = result.Eid;
        }

        ProfileHint.Text = result.Profiles.Count == 0
            ? $"已连接 eUICC（{result.Detail}），卡内暂无 Profile。"
            : $"已连接 eUICC（{result.Detail}），共 {result.Profiles.Count} 个 Profile。"
            + "点击「启用 / 停用」可切换卡片，写入后需重启模块生效。";

        foreach (var p in result.Profiles)
        {
            ProfilePanel.Children.Add(MakeCard(
                p.DisplayName,
                BuildProfileDetail(p),
                p.Enabled ? "已启用" : null,
                BuildProfileActions(p)));
        }

        if (result.Profiles.Count == 0)
        {
            ProfilePanel.Children.Add(MakeCard(
                "卡内暂无 Profile",
                "eUICC 工作正常，但尚未下载任何 Profile。\n"
                + "可通过 LPA（下载二维码）向该卡添加 eSIM Profile。",
                null));
        }
    }

    /// <summary>
    /// 启用 / 停用某个 Profile（ES10c EnableProfile BF31 / DisableProfile BF32，
    /// 与 CellDock / lpac 相同指令）。写入成功后引导重启模块使新卡生效。
    /// </summary>
    private async void ProfileToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: EsimProfileInfo p })
        {
            return;
        }

        if (_modem is null || !_modem.IsConnected || _modem.Info is null)
        {
            ProfileHint.Text = "模块未连接。";
            return;
        }

        if (_busy)
        {
            return;
        }

        var enable = !p.Enabled;
        var confirm = enable
            ? $"启用「{p.DisplayName}」？\n\n单激活卡会自动停用当前已启用的 Profile；"
            + "写入后需重启模块（约 40 秒）才能用新卡上网。"
            : $"停用「{p.DisplayName}」？\n\n停用后该 Profile 无法上网；"
            + "写入后需重启模块（约 40 秒）生效。";
        if (MessageBox.Show(confirm, enable ? "启用 Profile" : "停用 Profile",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _busy = true;
        RefreshButton.IsEnabled = false;
        ProfileHint.Text = $"正在{(enable ? "启用" : "停用")}「{p.DisplayName}」…";

        try
        {
            var es10 = _modem.Info.Es10;
            if (es10 is null)
            {
                MessageBox.Show("eUICC 通道未就绪，请先点「读取」完成探测。", "无法切换",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (es10.Channel <= 0)
            {
                await es10.OpenIsdrAsync();
            }

            var r = await es10.SetProfileStateAsync(p.IsdpAid!, enable);
            if (!r.Ok)
            {
                MessageBox.Show(
                    $"写入失败：{r.Message}（code={r.Code}）",
                    "切换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var restart = MessageBox.Show(
                "已写入 eUICC。需要重启模块（AT+CFUN=1,1，约 40 秒）新卡才会生效，"
                + "重启后会自动重连并刷新列表。\n\n立即重启吗？",
                "写入成功", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

            if (restart)
            {
                ProfileHint.Text = "模块重启中…";
                try
                {
                    await _modem.ExecuteAtAsync("AT+CFUN=1,1");
                }
                catch
                {
                    // 重启导致端口消失，属预期
                }

                try
                {
                    await _modem.DisconnectAsync();
                }
                catch
                {
                }

                var reconnected = false;
                for (var i = 1; i <= 15; i++)
                {
                    ProfileHint.Text = $"等待模块恢复（{i}/15）…";
                    await Task.Delay(4000);
                    try
                    {
                        reconnected = await _modem.ConnectAsync(null, CancellationToken.None);
                    }
                    catch
                    {
                    }

                    if (reconnected)
                    {
                        break;
                    }
                }
            }

            if (_modem.IsConnected && _modem.Info is not null)
            {
                // 重新探测并渲染切换后的真实状态
                await RefreshListAsync();
            }
            else
            {
                ProfileHint.Text = restart
                    ? "模块未能恢复连接。请检查模块供电后，点左下角「连接模块」重试。"
                    : "已写入，模块重启后生效。可手动重启模块或重新连接。";
            }
        }
        catch (Exception ex)
        {
            ProfileHint.Text = $"切换失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = _modem?.IsConnected == true;
        }
    }

    /// <summary>Profile 卡片的操作按钮：启用/停用（需有 ISD-P AID）、改名（需有 ICCID）、删除。</summary>
    private List<(string Label, RoutedEventHandler Handler, object? Tag)> BuildProfileActions(
        EsimProfileInfo p)
    {
        var actions = new List<(string, RoutedEventHandler, object?)>();
        if (p.IsdpAid is null)
        {
            return actions;
        }

        actions.Add((p.Enabled ? "停用" : "启用", ProfileToggle_Click, p));
        if (!string.IsNullOrWhiteSpace(p.Iccid))
        {
            actions.Add(("改名", Rename_Click, p));
        }
        actions.Add(("删除", Delete_Click, p));
        return actions;
    }

    /// <summary>
    /// 修改 Profile 昵称（ES10c SetNickname BF29，按 ICCID 定位）。
    /// 昵称只影响显示，不改变运行状态，无需重启模块。
    /// </summary>
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: EsimProfileInfo p } ||
            string.IsNullOrWhiteSpace(p.Iccid))
        {
            return;
        }

        if (!EnsureEuiccReady(out var es10))
        {
            return;
        }

        var dlg = new Dialogs.InputDialog
        {
            Owner = Window.GetWindow(this),
            Prompt = $"为「{p.DisplayName}」设置昵称（不超过 64 个英文字符，留空清除）：",
            InputText = p.Nickname ?? string.Empty,
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var nickname = dlg.InputText.Trim();
        if (_busy)
        {
            return;
        }

        _busy = true;
        RefreshButton.IsEnabled = false;
        ProfileHint.Text = "正在写入昵称…";
        try
        {
            var r = await es10.SetNicknameAsync(p.Iccid, nickname);
            if (!r.Ok)
            {
                MessageBox.Show(
                    $"写入失败：{r.Message}（code={r.Code}）",
                    "改名失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RefreshListAsync();
        }
        catch (Exception ex)
        {
            ProfileHint.Text = $"改名失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = _modem?.IsConnected == true;
        }
    }

    /// <summary>
    /// 删除 Profile（ES10c DeleteProfile BF33）。删除不可恢复，双重确认；
    /// 已启用的 Profile 多数卡会拒绝删除，需先停用。
    /// </summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: EsimProfileInfo p } ||
            string.IsNullOrWhiteSpace(p.IsdpAid))
        {
            return;
        }

        if (!EnsureEuiccReady(out var es10))
        {
            return;
        }

        var first = MessageBox.Show(
            $"确定要删除「{p.DisplayName}」吗？\n\n"
            + $"ICCID：{p.Iccid ?? "未知"}\n\n"
            + "删除会把该 Profile 从 eUICC 中移除（不可恢复）。"
            + "若以后还需要，需要重新用 LPA 下载（二维码 + 确认码）。",
            "删除 Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes)
        {
            return;
        }

        var second = MessageBox.Show(
            "再次确认：此操作不可撤销。\n"
            + "已启用的 Profile 通常会被卡拒绝删除 —— 建议先停用。\n\n"
            + "真的要删除吗？",
            "最终确认", MessageBoxButton.YesNo, MessageBoxImage.Stop);
        if (second != MessageBoxResult.Yes)
        {
            return;
        }

        if (_busy)
        {
            return;
        }

        _busy = true;
        RefreshButton.IsEnabled = false;
        ProfileHint.Text = $"正在删除「{p.DisplayName}」…";
        try
        {
            var r = await es10.DeleteProfileAsync(p.IsdpAid);
            if (!r.Ok)
            {
                var hint = p.Enabled
                    ? "\n\n该 Profile 处于启用状态，多数卡会拒绝删除 —— 请先停用。"
                    : string.Empty;
                MessageBox.Show(
                    $"删除失败：{r.Message}（code={r.Code}）{hint}",
                    "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show("该 Profile 已从卡内删除。", "删除成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshListAsync();
        }
        catch (Exception ex)
        {
            ProfileHint.Text = $"删除失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = _modem?.IsConnected == true;
        }
    }

    /// <summary>确认 eUICC 通道可用，不可用时提示并返回 false。</summary>
    private bool EnsureEuiccReady(out LogicalChannelEs10 es10)
    {
        es10 = null!;
        if (_modem is null || !_modem.IsConnected || _modem.Info is null)
        {
            ProfileHint.Text = "模块未连接。";
            return false;
        }

        var channel = _modem.Info.Es10;
        if (channel is null)
        {
            MessageBox.Show("eUICC 通道未就绪，请先点「读取」完成探测。", "无法操作",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        es10 = channel;
        return true;
    }

    /// <summary>重新探测 eUICC 并刷新列表（昵称 / 删除等无需重启模块的操作用）。</summary>
    private async Task RefreshListAsync()
    {
        if (_modem?.Info is null)
        {
            return;
        }

        var fresh = await _modem.Info.ProbeEuiccAsync();
        ProfilePanel.Children.Clear();
        if (fresh.Capability == EuiccCapability.Ready)
        {
            IccidText.Text = _modem.Status.Iccid ?? "--";
            RenderReady(fresh);
            AddCardInfo();
        }
        else
        {
            ProfileHint.Text = $"模块已连接，但 eUICC 探测结论变为 {fresh.Capability}，请点「读取」重试。";
        }
    }

    private static string BuildProfileDetail(EsimProfileInfo p)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(p.Iccid))
        {
            sb.Append("ICCID  ").AppendLine(p.Iccid);
        }
        if (!string.IsNullOrWhiteSpace(p.ServiceProvider))
        {
            sb.Append("运营商  ").AppendLine(p.ServiceProvider);
        }
        if (!string.IsNullOrWhiteSpace(p.IsdpAid))
        {
            sb.Append("ISD-P  ").AppendLine(p.IsdpAid);
        }
        sb.Append("状态    ").Append(p.StateText);
        if (!string.IsNullOrEmpty(p.ClassText))
        {
            sb.Append(" · ").Append(p.ClassText).Append("型");
        }
        return sb.ToString();
    }

    /// <summary>CSIM 有长度限制且固件不支持逻辑通道 —— 仅旧固件才会走到这里。</summary>
    private void ShowModuleLimit()
    {
        EidText.Text = "不可读取（通道限制）";

        ProfileHint.Text = "固件不支持逻辑通道指令，且 AT+CSIM 透传存在长度上限。";

        ProfilePanel.Children.Add(MakeCard(
            "结论：模块通道限制，与卡片无关",
            "本固件的 AT+CSIM 透传对单条 APDU 有约 8 字节的长度上限，"
            + "且不支持 3GPP 逻辑通道指令（AT+CCHO / AT+CGLA）。\n\n"
            + "正常情况下本程序会走 CCHO/CGLA 通道（与 CellDock 相同）读取 eUICC；"
            + "走到这里说明两条通道都不可用，建议升级模块固件。",
            null));

        ProfilePanel.Children.Add(MakeCard(
            "可行的替代方案",
            "· 升级模块固件（支持 CCHO/CGLA 的任意版本即可）\n"
            + "· 把卡插到手机或 5ber / eSIM.me 读卡器上，用厂商 App 管理 Profile",
            null));
    }

    /// <summary>逻辑通道正常打开，但卡内没有 ISD-R 应用。</summary>
    private void ShowCardNotEuicc(string? detail)
    {
        EidText.Text = "未检测到 eUICC 应用";

        ProfileHint.Text = "通道正常，但卡片中未找到 eUICC 应用（ISD-R）。";

        ProfilePanel.Children.Add(MakeCard(
            "卡内未找到 ISD-R 应用",
            (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n\n") +
            "AT+CCHO 未能为 ISD-R AID 打开逻辑通道，或打开后读不到 EID。\n"
            + "这通常意味着该卡是普通物理 SIM 卡，不含 eUICC 应用，因此没有 Profile 可读。",
            null));

        ProfilePanel.Children.Add(MakeCard(
            "若你确认这是 eUICC 卡（如 9eSIM）",
            "可到 AT 控制台手动验证，把结果反馈：\n"
            + "AT+CCHO=\"A0000005591010FFFFFFFF8900000100\"\n"
            + "预期返回 +CCHO: <通道号>；若返回 ERROR 或无响应，请截图反馈。",
            null));
    }

    /// <summary>AT 通道本身不可用。</summary>
    private void ShowChannelIssue()
    {
        EidText.Text = "无法探测（通道无响应）";

        ProfileHint.Text = "AT 通道无响应，无法读取卡片。";

        ProfilePanel.Children.Add(MakeCard(
            "模块未响应卡片指令",
            "可能原因：\n"
            + "· SIM 卡未就绪（请先在设备页确认 SIM 状态）\n"
            + "· 当前串口不是 AT 通道（模块的 AT 口会在 COM 之间漂移）\n"
            + "· 模块固件未开放卡片访问指令",
            null));
    }

    /// <summary>展示与 eUICC 无关、但确定可读的卡片信息。</summary>
    private void AddCardInfo()
    {
        if (_modem is null)
        {
            return;
        }

        var iccid = _modem.Status.Iccid;
        var imsi = _modem.Status.Imsi;
        if (string.IsNullOrWhiteSpace(iccid) && string.IsNullOrWhiteSpace(imsi))
        {
            return;
        }

        var detail = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(iccid))
        {
            detail.AppendLine($"ICCID  {iccid}");
            // 9eSIM 等可写卡常以固定前缀区分批次。
            var issuer = GuessIssuer(iccid);
            if (issuer is not null)
            {
                detail.AppendLine($"发卡方  {issuer}");
            }
        }

        if (!string.IsNullOrWhiteSpace(imsi))
        {
            detail.AppendLine($"IMSI   {imsi}");
        }

        ProfilePanel.Children.Add(MakeCard("卡片基础信息（不依赖 eUICC）", detail.ToString().TrimEnd(), null));
    }

    /// <summary>按 ICCID 前缀粗略判断发卡方，仅用于说明卡片类型。</summary>
    private static string? GuessIssuer(string iccid) => iccid.Length < 6 ? null : iccid[..6] switch
    {
        "898520" => "中国电信物联网卡段",
        "898523" => "中国电信",
        "898530" => "中国联通物联网卡段",
        "898601" => "中国移动",
        "898600" => "中国移动",
        _ => null,
    };

    private static string Summarize(string s, int max)
    {
        s = s.Replace("\r", string.Empty).Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static Border MakeCard(
        string title,
        string detail,
        string? badge,
        IReadOnlyList<(string Label, RoutedEventHandler Handler, object? Tag)>? actions = null)
    {
        var border = new Border
        {
            Background = (Brush)Application.Current.FindResource("CardHoverBrush"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 3, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (actions is { Count: > 0 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Foreground = (Brush)Application.Current.FindResource("TextDimBrush"),
        });

        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);

        var nextColumn = 1;

        if (!string.IsNullOrEmpty(badge))
        {
            var t = new TextBlock
            {
                Text = badge,
                FontSize = 11,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource("SuccessBrush"),
            };
            Grid.SetColumn(t, nextColumn);
            grid.Children.Add(t);
            nextColumn++;
        }

        if (actions is { Count: > 0 })
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < actions.Count; i++)
            {
                var (label, handler, tag) = actions[i];
                var btn = new Button
                {
                    Content = label,
                    Style = (Style)Application.Current.FindResource("GhostButton"),
                    MinWidth = 52,
                    Height = 26,
                    Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = tag,
                };
                btn.Click += handler;
                panel.Children.Add(btn);
            }

            Grid.SetColumn(panel, nextColumn);
            grid.Children.Add(panel);
        }

        border.Child = grid;
        return border;
    }
}
