using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DevUp.Core;
using DevUp.Models;
using DevUp.Views;

internal static class SoftwareViewChecks
{
    public static void Run() => UiTestHost.Run(RunOnUiThreadAsync, "软件界面检查失败");

    private static async Task RunOnUiThreadAsync()
    {
        var app = UiTestHost.CreateApplication();
        var items = new[]
        {
            SoftwareIterationChecks.Software("示例编辑器", "Example.Editor", "Example Software"),
            SoftwareIterationChecks.Software("示例终端", "Example.Terminal", "Example Tools"),
            SoftwareIterationChecks.Software("已忽略更新", "Example.Ignored"),
            new SoftwareItem { DisplayName = "未匹配的本地软件", DisplayVersion = "1.0" }
        };
        items[2].Ignored = true;
        foreach (var item in items) Ctx.State.Software.Add(item);
        var view = new SoftwareView { DataContext = Ctx.State };
        var window = new Window
        {
            Content = new Border
            {
                Padding = new Thickness(20), Child = view,
                Background = (Brush)app.FindResource("BgBrush")
            },
            Width = 1120, Height = 740, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false,
            Background = (Brush)app.FindResource("BgBrush")
        };
        app.MainWindow = window;
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        T Find<T>(string name) => (T)view.FindName(name);
        int Shown() => CollectionViewSource.GetDefaultView(Ctx.State.Software).Cast<object>().Count();
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(Shown() == 3, "全部软件筛选没有隐藏忽略项目");
            Check(Ctx.State.DriverUpdateCheck.CountText == "—", "未检查驱动显示为零");
            var drivers = new DriversView { DataContext = Ctx.State };
            await Ctx.State.DriverUpdateCheck.RunAsync(() => Task.FromResult(0));
            Check(((TextBlock)drivers.FindName("EmptyTitle")).Text == "没有发现可用的驱动更新",
                "从其他入口检查驱动后，驱动页面仍显示尚未扫描");
            try { await Ctx.State.DriverUpdateCheck.RunAsync(() => throw new IOException("模拟故障")); }
            catch (IOException) { }
            Check(((TextBlock)drivers.FindName("EmptyTitle")).Text.Contains("检查失败") &&
                ((TextBlock)drivers.FindName("UpdateHint")).Text.Contains("上次结果"),
                "驱动检查失败后空状态仍显示没有更新");
            view.ShowOnlyUpdates(true);
            Find<TextBox>("SearchBox").Text = "EDITOR";
            Check(Shown() == 1, "软件界面包 ID 搜索无效");
            // 通过实际按钮事件验证选择范围，不触发安装或联网查询。
            var selectVisible = Descendants<Button>(view).Single(button => Equals(button.Content, "选择筛选结果"));
            selectVisible.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(items[0].IsSelected && !items[1].IsSelected && !items[2].IsSelected,
                "选择筛选结果包含了隐藏或忽略的软件");
            var softwareGrid = Find<DataGrid>("SoftwareGrid");
            softwareGrid.UpdateLayout();
            var firstRow = (DataGridRow)softwareGrid.ItemContainerGenerator.ContainerFromItem(items[0]);
            var checkbox = Descendants<CheckBox>(firstRow).Single();
            checkbox.IsChecked = false;
            checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!items[0].IsSelected && !Find<Button>("UpgradeSelectedButton").IsEnabled,
                "取消复选框没有同步模型与批量按钮");
            checkbox.IsChecked = true;
            checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Find<TextBox>("SearchBox").Text = "终端";
            Check(items[0].IsSelected && Find<TextBlock>("SelectionSummary").Text.Contains("1 项在当前筛选之外"),
                "筛选后丢失勾选或没有提示隐藏选择");
            Find<TextBox>("SearchBox").Text = "不存在的软件";
            Check(Find<TextBlock>("EmptyTitle").Text == "没有匹配的搜索结果", "搜索空状态误报没有更新");
            Find<TextBox>("SearchBox").Clear();
            Check(Find<Button>("UpgradeSelectedButton").IsEnabled, "有效勾选没有启用批量升级");
            Ctx.State.IsBusy = true;
            Check(!Find<Button>("UpgradeSelectedButton").IsEnabled, "忙碌时仍允许启动批量升级");
            Ctx.State.IsBusy = false;
            await Ctx.State.SoftwareInventoryCheck.RunAsync(() => Task.FromResult(items.Length));
            await Ctx.State.SoftwareUpdateCheck.RunAsync(() => Task.FromResult(2));
            var failed = new SoftwareBatchItem { DisplayName = "示例编辑器", PackageId = "Example.Editor" };
            failed.SetOutcome(SoftwareBatchOutcome.Failed, "下载超时：请测试连接或重试此项目。");
            var pending = new SoftwareBatchItem { DisplayName = "示例终端", PackageId = "Example.Terminal" };
            view.ShowBatchResults([failed, pending]);
            Check(!Find<Expander>("BatchResultsPanel").IsExpanded &&
                Find<Expander>("BatchResultsPanel").Header.ToString()!.Contains("失败 1"),
                "较小窗口没有折叠结果或没有显示失败计数");
            Find<Expander>("BatchResultsPanel").IsExpanded = true;
            Check(Find<Button>("RetryFailedButton").IsEnabled && Find<Button>("ContinueBatchButton").IsEnabled,
                "结果面板没有提供重试或继续入口");
            Ctx.State.IsBusy = true;
            Check(!Find<Button>("RetryFailedButton").IsEnabled && !Find<Button>("ContinueBatchButton").IsEnabled,
                "忙碌时结果操作没有锁定");
            Ctx.State.IsBusy = false;
            Ctx.State.SoftwareUpdateCheck.MarkStale();
            Check(Find<TextBlock>("BatchSummary").Text.Contains("重新检查"), "过期结果没有复核提示");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            UiTestHost.SaveImage(window, "software-iteration-wide.png");
            window.Width = 810;
            window.Height = 560;
            Find<Expander>("BatchResultsPanel").IsExpanded = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Check(Find<DataGrid>("SoftwareGrid").ActualHeight > 50 &&
                Find<Button>("UpgradeSelectedButton").ActualWidth > 0 &&
                Find<DataGrid>("SoftwareGrid").Columns[1].ActualWidth >= 150,
                "窄窗口下软件列表或批量按钮不可用");
            UiTestHost.SaveImage(window, "software-iteration-narrow.png");
            Console.WriteLine($"软件界面检查通过：{checks} 项；已生成宽/窄窗口截图，未安装真实软件。");
        }
        finally
        {
            window.Close();
            Ctx.State.Software.Clear();
            app.Shutdown();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
