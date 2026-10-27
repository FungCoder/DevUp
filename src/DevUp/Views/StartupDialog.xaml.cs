using System.Windows;
using System.Windows.Input;

namespace DevUp.Views;

/// <summary>
/// 启动前的权限选择窗口：让用户决定本次以管理员还是普通权限运行，
/// 并可记住选择（保存到程序目录下 DevUpData/data/settings.json 的 StartupMode）。
/// </summary>
public partial class StartupDialog : Window
{
    /// <summary>true = 以管理员权限启动；false = 普通权限。</summary>
    public bool RunAsAdmin { get; private set; } = true;

    /// <summary>用户是否勾选了“记住我的选择”。</summary>
    public bool Remember { get; private set; }

    public StartupDialog()
    {
        InitializeComponent();

        Root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };

        Loaded += (_, _) => RbAdmin.Focus();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        RunAsAdmin = RbAdmin.IsChecked == true;
        Remember = ChkRemember.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
