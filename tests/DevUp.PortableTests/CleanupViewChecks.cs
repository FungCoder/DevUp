using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;
using DevUp.Views;

internal static class CleanupViewChecks
{
    public static void Run() => UiTestHost.Run(RunOnUiThreadAsync, "清理界面检查失败");

    private static async Task RunOnUiThreadAsync()
    {
        using var fixture = new CleanupChecks.Fixture();
        fixture.FileAt("Temp/old.tmp", 16000);
        fixture.FileAt("Temp/recent.tmp", 10, 0);
        fixture.FileAt("Local/Microsoft/Edge/User Data/Default/Cache/Cache_Data/data_0", 320000);
        fixture.FileAt("Local/Google/Chrome/User Data/Default/Cache/Cache_Data/data_0", 640000);
        fixture.FileAt("Local/D3DSCache/shader.bin", 128000);
        fixture.FileAt("Roaming/Code/Cache/data_0", 96000);
        var variables = new Dictionary<string, string>
        {
            ["TEMP"] = Path.Combine(fixture.Root, "Temp"),
            ["WINDIR"] = Path.Combine(fixture.Root, "Windows"),
            ["LOCALAPPDATA"] = Path.Combine(fixture.Root, "Local"),
            ["APPDATA"] = Path.Combine(fixture.Root, "Roaming"),
            ["PROGRAMDATA"] = Path.Combine(fixture.Root, "ProgramData"),
            ["USERPROFILE"] = Path.Combine(fixture.Root, "User")
        };
        using var rulesStream = typeof(CleanupService).Assembly.GetManifestResourceStream("cleaning-rules.ini")!;
        using var reader = new StreamReader(rulesStream);
        var service = new CleanupService(reader.ReadToEnd(), variables, () => fixture.Protected, _ => false);
        var app = UiTestHost.CreateApplication();
        var view = new CleanupView(service);
        var window = new Window
        {
            Content = new Border
            {
                Padding = new Thickness(28, 22, 28, 0), Child = view,
                Background = (Brush)app.FindResource("BgBrush")
            },
            Width = 1148, Height = 660, Left = -10000, Top = -10000,
            Background = (Brush)app.FindResource("BgBrush"),
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            ShowInTaskbar = false, ShowActivated = false
        };
        app.MainWindow = window;
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        Button Button(string name) => (Button)view.FindName(name);
        void ConfirmNext(bool accept)
        {
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = app.Windows.OfType<MessageDialog>().Single();
                ((Button)dialog.FindName(accept ? "BtnOk" : "BtnCancel"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBaseClick));
            }));
        }
        async Task WaitForIdle()
        {
            var timer = Stopwatch.StartNew();
            while (view.IsRunning || Ctx.State.IsBusy)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("界面任务未结束");
                await Task.Delay(10);
            }
            await Task.Delay(250);
        }
        void SaveImage(string name) => UiTestHost.SaveImage(window, name);

        try
        {
            var shell = new DevUp.MainWindow();
            ((System.Windows.Controls.Primitives.ToggleButton)shell.FindName("NavStartup")).IsChecked = true;
            Check(((ContentControl)shell.FindName("PageHost")).Content is StartupView,
                "自启动管理导航没有接入主窗口");
            ((System.Windows.Controls.Primitives.ToggleButton)shell.FindName("NavCleanup")).IsChecked = true;
            Check(((TextBlock)shell.FindName("PageTitle")).Text == "垃圾清理" &&
                ((ContentControl)shell.FindName("PageHost")).Content is CleanupView, "垃圾清理导航没有接入主窗口");
            Ctx.State.IsBusy = true;
            Check(!((Button)shell.FindName("BtnElevate")).IsEnabled &&
                !((System.Windows.Controls.Primitives.ToggleButton)shell.FindName("NavSoftware")).IsEnabled,
                "任务期间仍可重启提权或切换到其他操作");
            Ctx.State.IsBusy = false;
            shell.Close();
            window.Show();
            await Task.Yield();
            Check(!Button("CleanButton").IsEnabled, "未扫描时允许清理");
            Check(((DataGrid)view.FindName("CategoryGrid")).Items.Count == 0 &&
                ((TextBlock)view.FindName("EmptyText")).Visibility == Visibility.Visible,
                "未扫描时提前显示了默认清理项目");
            Button("ScanButton").RaiseEvent(new RoutedEventArgs(ButtonBaseClick));
            Check(view.IsRunning && Ctx.State.IsBusy && !Button("ScanButton").IsEnabled,
                "扫描期间没有统一锁定任务状态");
            Check(Button("CancelButton").Visibility == Visibility.Visible, "扫描没有取消入口");
            if (SystemParameters.ClientAreaAnimation)
                Check(((RotateTransform)view.FindName("OrbitRotation")).HasAnimatedProperties, "扫描动画没有启动");
            view.Cancel();
            await WaitForIdle();
            Check(!Button("CleanButton").IsEnabled &&
                ((TextBlock)view.FindName("ActivityText")).Text.Contains("取消"), "扫描取消仍保留了可清理状态");
            Button("ScanButton").RaiseEvent(new RoutedEventArgs(ButtonBaseClick));
            await WaitForIdle();
            Check(Button("CleanButton").IsEnabled, "扫描完成后无法清理");
            var grid = (DataGrid)view.FindName("CategoryGrid");
            var rows = grid.Items.Cast<CleanupRow>().ToArray();
            Check(rows.Sum(row => row.Files.Count) == 5 && rows.All(row => row.Files.Count > 0) &&
                rows.Count(row => row.Selected) == rows.Length,
                "界面扫描结果或默认选择错误");
            grid.SelectedItem = rows.Single(row => row.Name == "Microsoft Edge 缓存");
            Check(((DataGrid)view.FindName("FileGrid")).Items.Count == 1, "分类文件明细未更新");
            SaveImage("cleanup-ui.png");
            window.Width = 818;
            window.Height = 520;
            await Task.Delay(150);
            window.UpdateLayout();
            Check(grid.ActualHeight > 100 && ((DataGrid)view.FindName("FileGrid")).ActualHeight > 50,
                $"最小窗口尺寸下列表被挤出页面：分类 {grid.ActualHeight}，明细 {((DataGrid)view.FindName("FileGrid")).ActualHeight}");
            var fileGrid = (DataGrid)view.FindName("FileGrid");
            Check(fileGrid.Columns.Sum(column => column.ActualWidth) <= fileGrid.ActualWidth + 1,
                "窄窗口下文件表格列超出可用宽度");
            SaveImage("cleanup-ui-small.png");
            window.Width = 1148;
            window.Height = 660;
            foreach (var row in rows) row.Selected = false;
            Check(!Button("CleanButton").IsEnabled, "取消所有选择后仍允许清理");
            foreach (var row in rows.Where(row => row.Files.Count > 0)) row.Selected = true;
            ConfirmNext(false);
            Button("CleanButton").RaiseEvent(new RoutedEventArgs(ButtonBaseClick));
            Check(!view.IsRunning && Directory.GetFiles(Path.Combine(fixture.Root, "Temp")).Length == 2,
                "取消删除确认后仍启动了清理");
            ConfirmNext(true);
            Button("CleanButton").RaiseEvent(new RoutedEventArgs(ButtonBaseClick));
            Check(view.IsRunning && !Button("CleanButton").IsEnabled, "清理期间未锁定操作");
            await WaitForIdle();
            Check(((TextBlock)view.FindName("CountText")).Text.Contains("已删除 5") &&
                ((TextBlock)view.FindName("ActivityText")).Text == "清理完成", "界面没有显示真实清理结果");
            Check(!Button("CleanButton").IsEnabled && Button("ScanButton").IsEnabled,
                "清理完成后没有要求重新扫描");
            Check(!((RotateTransform)view.FindName("OrbitRotation")).HasAnimatedProperties &&
                ((ProgressBar)view.FindName("WorkProgress")).Value == 100, "任务完成后动画未停止或进度错误");
            Check(File.Exists(Path.Combine(fixture.Root, "Temp/recent.tmp")), "UI 清理误删了新临时文件");
            SaveImage("cleanup-ui-complete.png");
            Button("ScanButton").RaiseEvent(new RoutedEventArgs(ButtonBaseClick));
            await WaitForIdle();
            Check(grid.Items.Count == 0 && !Button("CleanButton").IsEnabled &&
                ((TextBlock)view.FindName("EmptyText")).Visibility == Visibility.Visible,
                "没有可清理文件时仍显示分类或允许清理");
            var updates = new DevUpUpdatePanel();
            var updateHost = new StackPanel();
            updateHost.Children.Add(updates);
            ((Border)window.Content).Child = updateHost;
            window.Height = 332;
            await Task.Delay(100);
            Check(((TextBlock)updates.FindName("VersionText")).Text.Contains("规则") &&
                ((Button)updates.FindName("CheckButton")).IsEnabled &&
                !((Button)updates.FindName("ProgramButton")).IsEnabled,
                "统一更新卡片的初始信息或按钮状态错误");
            SaveImage("devup-updates-ui.png");
            Check(updates.FindName("RulesButton") is null &&
                ((TextBlock)updates.FindName("RuleStatus")).Text.Contains("内嵌"),
                "单文件更新卡片仍有独立规则下载入口");
            var startup = new StartupView();
            ((Border)window.Content).Child = startup;
            window.Height = 660;
            await Task.Delay(100);
            Check(((DataGrid)startup.FindName("EntryGrid")).Items.Count == 0 &&
                !((Button)startup.FindName("DisableButton")).IsEnabled,
                "自启页未扫描时允许修改配置");
            ((Button)startup.FindName("ScanButton")).RaiseEvent(new RoutedEventArgs(ButtonBaseClick));
            var startupDeadline = Stopwatch.StartNew();
            while (Ctx.State.IsBusy)
            {
                if (startupDeadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("自启界面扫描超时");
                await Task.Delay(20);
            }
            Check(((TextBlock)startup.FindName("SummaryText")).Text.Contains("个入口"),
                "自启扫描没有显示实际统计");
            SaveImage("startup-ui.png");
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
        Console.WriteLine($"清理界面检查通过：{checks} 项；已生成常规、窄窗口与完成状态截图。");
    }

    private static RoutedEvent ButtonBaseClick => System.Windows.Controls.Primitives.ButtonBase.ClickEvent;
}
