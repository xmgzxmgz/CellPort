using System.Windows;
using System.Windows.Input;

namespace CellPort.App.Dialogs;

/// <summary>
/// 单行文本输入对话框（WPF 无自带 InputBox）。
/// 用法：设置 Prompt / InputText，ShowDialog() 后读取 InputText（DialogResult == true 时有效）。
/// </summary>
public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>提示文本。</summary>
    public string Prompt
    {
        set => PromptText.Text = value;
    }

    /// <summary>输入内容（ShowDialog 返回 true 时读取）。</summary>
    public string InputText
    {
        get => InputBox.Text;
        set => InputBox.Text = value;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
