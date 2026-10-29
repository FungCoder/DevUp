using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DevUp.Core;
using DevUp.Views;

internal static class DialogChecks
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    public static void Run(bool preview = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnUiThread(preview); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("弹窗回归失败", failure);
    }

    private static void RunOnUiThread(bool preview)
    {
        var app = new DevUp.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var content = new Button { Content = "测试页面" };
        var owner = new Window
        {
            Content = content, Width = 300, Height = 200,
            Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false
        };
        app.MainWindow = owner;
        var errors = new List<string>();
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) errors.Add(message);
        }
        void CheckOwner()
        {
            Check(IsWindowEnabled(new WindowInteropHelper(owner).Handle), "主窗口被 Win32 禁用，无法拖动");
            Check(!content.IsEnabled, "弹窗期间页面操作未锁定");
            owner.Close();
            Check(owner.IsVisible, "任务期间允许主窗口直接退出");
            owner.WindowState = WindowState.Minimized;
            Check(owner.WindowState == WindowState.Minimized, "主窗口无法最小化");
            owner.WindowState = WindowState.Normal;
        }
        void ClickMessage(string? buttonName)
        {
            owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                CheckOwner();
                var message = app.Windows.OfType<MessageDialog>().Single();
                if (buttonName is null) message.Close();
                else ((Button)message.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
        }

        try
        {
            owner.Show();
            if (preview)
            {
                owner.Title = "DevUp 主窗口拖动验证";
                owner.Width = 850;
                owner.Height = 550;
                owner.Left = 120;
                owner.Top = 120;
                content.Content = "请拖动主窗口标题栏；进度窗将在 45 秒后自动关闭";
                ProgressDialog.Run("拖动验证", "主窗口标题栏应可拖动和最小化", async (progress, token) =>
                {
                    progress.Report(new ProgressInfo { Status = "可拖动主窗口标题栏或本提示窗", Percent = -1 });
                    await Task.Delay(TimeSpan.FromSeconds(45), token);
                });
                return;
            }
            var outer = new Window { Owner = owner, Content = new Button(), ShowInTaskbar = false };
            outer.Loaded += (_, _) =>
            {
                CheckOwner();
                var inner = new Window { Owner = outer, Content = new Button(), ShowInTaskbar = false };
                inner.Loaded += (_, _) =>
                {
                    Check(!((UIElement)outer.Content).IsEnabled, "嵌套提示未锁定外层弹窗");
                    inner.Close();
                };
                MovableDialog.Show(inner);
                Check(!content.IsEnabled && ((UIElement)outer.Content).IsEnabled,
                    "嵌套提示关闭后页面或外层弹窗状态错误");
                outer.Close();
            };
            MovableDialog.Show(outer);
            Check(content.IsEnabled, "关闭弹窗后页面未恢复");

            content.IsEnabled = false;
            var disabled = new Window { Owner = owner, ShowInTaskbar = false };
            disabled.Loaded += (_, _) => disabled.Close();
            MovableDialog.Show(disabled);
            Check(!content.IsEnabled, "弹窗擅自启用了原本禁用的页面");
            content.IsEnabled = true;

            ClickMessage("BtnOk");
            Check(MessageDialog.Show("测试", "确定", DialogKind.Question, true), "确认结果丢失");
            ClickMessage("BtnCancel");
            Check(!MessageDialog.Show("测试", "取消", DialogKind.Question, true), "取消结果错误");
            ClickMessage("BtnExtra");
            Check(MessageDialog.ShowChoice("测试", "选择", DialogKind.Question, "重试", "下载")
                == DialogChoice.Extra, "次按钮结果丢失");
            ClickMessage(null);
            Check(!MessageDialog.Show("测试", "关闭", DialogKind.Info, false), "关闭被当成确认");

            Check(ProgressDialog.Run("测试", "完成", async (_, _) =>
            {
                await Task.Yield();
                CheckOwner();
            }), "成功任务返回失败");
            Check(!ProgressDialog.Run("测试", "取消", async (_, token) =>
            {
                await Task.Yield();
                app.Windows.OfType<ProgressDialog>().Single().Close();
                await Task.Delay(Timeout.Infinite, token);
            }), "取消任务返回成功");
            try
            {
                ProgressDialog.Run("测试", "失败", (_, _) => Task.FromException(new IOException("测试异常")));
                Check(false, "任务异常未传递");
            }
            catch (IOException ex) { Check(ex.Message == "测试异常", "任务异常被改写"); }
            Check(content.IsEnabled, "任务结束后页面未恢复");
        }
        finally
        {
            owner.Close();
            app.Shutdown();
        }

        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        Console.WriteLine($"弹窗与主窗口交互检查通过：{checks} 项。");
    }
}
