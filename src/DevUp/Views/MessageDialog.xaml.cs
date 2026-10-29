using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DevUp.Core;

namespace DevUp.Views;

public enum DialogKind { Info, Warn, Error, Question, Success }

/// <summary>三选一弹窗的返回值（普通弹窗只用 Ok / Cancel）。</summary>
public enum DialogChoice { Cancel, Ok, Extra }

/// <summary>统一风格的应用内弹窗（替代系统 MessageBox）。</summary>
public partial class MessageDialog : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    public MessageDialog()
    {
        InitializeComponent();
        HeaderBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    public static bool Show(string title, string message, DialogKind kind, bool confirm)
    {
        var dlg = new MessageDialog();

        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, dlg))
        {
            dlg.Owner = owner;
        }
        else
        {
            // 启动早期（还没有可见主窗口）或主窗口已关闭时，居中到屏幕，避免 Owner 设置异常
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.Apply(kind, confirm);

        MovableDialog.Show(dlg);
        return dlg._choice == DialogChoice.Ok;
    }

    /// <summary>
    /// 三段式选择弹窗：主按钮（okText）/ 次按钮（extraText）/ 取消（cancelText）。
    /// 用于“升级失败后给出重试或手动下载”这类需要两个动作的场景。
    /// </summary>
    public static DialogChoice ShowChoice(string title, string message, DialogKind kind,
        string okText, string extraText, string cancelText = "关闭")
    {
        var dlg = new MessageDialog();

        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, dlg))
        {
            dlg.Owner = owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.Apply(kind, confirm: true);
        dlg.BtnOk.Content = okText;
        dlg.BtnExtra.Content = extraText;
        dlg.BtnExtra.Visibility = Visibility.Visible;
        dlg.BtnCancel.Content = cancelText;
        dlg.BtnCancel.Visibility = Visibility.Visible;

        MovableDialog.Show(dlg);
        return dlg._choice;
    }

    private void Apply(DialogKind kind, bool confirm)
    {
        var (glyph, color) = kind switch
        {
            DialogKind.Warn => ("!", (Color)ColorConverter.ConvertFromString("#D97706")),
            DialogKind.Error => ("\u2715", (Color)ColorConverter.ConvertFromString("#DC2626")),
            DialogKind.Question => ("?", (Color)ColorConverter.ConvertFromString("#3B82F6")),
            DialogKind.Success => ("\u2713", (Color)ColorConverter.ConvertFromString("#16A34A")),
            _ => ("i", (Color)ColorConverter.ConvertFromString("#3B82F6"))
        };

        IconGlyph.Text = glyph;
        IconBox.Background = new SolidColorBrush(color);

        if (confirm)
        {
            BtnCancel.Visibility = Visibility.Visible;
            BtnOk.Content = "确定";
        }
        BtnOk.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Ok;
        Close();
    }

    private void Extra_Click(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Extra;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _choice = DialogChoice.Cancel;
        Close();
    }
}
