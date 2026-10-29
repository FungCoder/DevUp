using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using DevUp.Core;
using DevUp.Views;

namespace DevUp;

public partial class MainWindow : Window
{
    private readonly DashboardView _dashboard = new();
    private readonly DriversView _drivers = new();
    private readonly SoftwareView _software = new();
    private readonly NpmView _npm = new();
    private readonly ToolsView _tools = new();

    public MainWindow()
    {
        InitializeComponent();

        DataContext = Ctx.State;
        PageHost.Content = _dashboard;
        SetHeader("概览", "系统与驱动健康状况一览");

        // 初始页：Checked 已在 XAML 中订阅（Nav_Checked），此处只负责选中对应导航项
        var startPage = App.StartupPage switch
        {
            "drivers" => NavDrivers,
            "software" => NavSoftware,
            "npm" => NavNpm,
            "tools" => NavTools,
            _ => NavDashboard
        };
        startPage.IsChecked = true;

        AdminBadge.Text = Native.IsAdministrator() ? "权限：管理员" : "权限：普通用户（部分操作会临时提权）";
        BtnElevate.Visibility = Native.IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
        BuildInfo.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.1.2");

        Ctx.State.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Models.ScanState.StatusText):
                    StatusText.Text = Ctx.State.StatusText;
                    break;
                case nameof(Models.ScanState.IsBusy):
                    BusyBar.Visibility = Ctx.State.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                    BtnRescan.IsEnabled = !Ctx.State.IsBusy;
                    BtnScanUpdates.IsEnabled = !Ctx.State.IsBusy;
                    break;
                case nameof(Models.ScanState.Summary):
                    ScanTimeText.Text = Ctx.State.Summary.ScannedAt == default
                        ? ""
                        : "上次扫描：" + Ctx.State.Summary.ScannedAt.ToString("HH:mm:ss");
                    break;
            }
        };

        Ui.Logged += (level, message) => Dispatcher.Invoke(() =>
        {
            Ctx.State.Logs.Insert(0, new Models.LogEntry { Level = level, Message = message });
            while (Ctx.State.Logs.Count > 300) Ctx.State.Logs.RemoveAt(Ctx.State.Logs.Count - 1);
        });

        Loaded += async (_, _) => await InitialScanAsync();
    }

    private async Task InitialScanAsync()
    {
        await Ctx.RunScanAsync("正在扫描本机信息…", async (progress, ct) =>
        {
            await Ctx.LoadSystemInfoAsync(ct);
            await Scanner.ScanOverviewAsync(progress, ct);
        }, TimeSpan.FromMinutes(8));
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost is null) return;
        if (sender is not ToggleButton tab) return;

        // 只允许一个导航项处于选中态
        foreach (var other in new[] { NavDashboard, NavDrivers, NavSoftware, NavNpm, NavTools })
            if (!ReferenceEquals(other, tab)) other.IsChecked = false;

        switch (tab.Name)
        {
            case "NavDashboard":
                PageHost.Content = _dashboard;
                SetHeader("概览", "系统与驱动健康状况一览");
                break;
            case "NavDrivers":
                PageHost.Content = _drivers;
                SetHeader("驱动与设备", "识别缺失/异常设备，并通过 Windows Update 安装官方驱动");
                break;
            case "NavSoftware":
                PageHost.Content = _software;
                SetHeader("软件更新", "基于官方 winget 源的精确版本比对与升级");
                break;
            case "NavNpm":
                PageHost.Content = _npm;
                SetHeader("Node.js 包", "管理 npm 全局安装的 AI 工具与命令行包");
                break;
            case "NavTools":
                PageHost.Content = _tools;
                SetHeader("备份与工具", "驱动备份 / 还原 / 还原点 / 报告导出");
                break;
        }

        if (SystemParameters.ClientAreaAnimation)
        {
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            PageHost.BeginAnimation(OpacityProperty, null);
            PageHost.Opacity = 1;
        }
    }

    /// <summary>从概览页跳转到设备页（可只显示异常设备）。</summary>
    public void NavigateToDevices(bool problemOnly)
    {
        NavDrivers.IsChecked = true;
        if (problemOnly) _drivers.ShowProblemDevices();
        else _drivers.ShowAllDevices();
    }

    /// <summary>从概览页跳转到软件页（可只显示可更新项）。</summary>
    public void NavigateToSoftware(bool onlyUpdates)
    {
        NavSoftware.IsChecked = true;
        _software.ShowOnlyUpdates(onlyUpdates);
    }

    private void SetHeader(string title, string subtitle)    {
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private void BtnBlog_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.fungj.com") { UseShellExecute = true }); }
        catch { }
    }

    private void BtnElevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath ?? "DevUp.exe";
            // StartElevated 返回 false 表示用户在 UAC 对话框点了“否”：此时不能退出主程序
            if (!Native.StartElevated(exe, "--elevated-main"))
            {
                Ui.Info("已取消", "未以管理员身份重新启动，仍以当前权限继续运行。");
                return;
            }

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Ui.Error("无法提权", ex.Message);
        }
    }

    private async void BtnRescan_Click(object sender, RoutedEventArgs e)
    {
        await Ctx.RunScanAsync("正在重新扫描…", async (progress, ct) =>
        {
            await Ctx.LoadSystemInfoAsync(ct);
            await Scanner.ScanOverviewAsync(progress, ct);
        }, TimeSpan.FromMinutes(8));
    }

    private async void BtnScanUpdates_Click(object sender, RoutedEventArgs e)
    {
        var completed = await Ctx.RunScanAsync("正在检查更新…", async (progress, ct) =>
        {
            progress.Report(new ProgressInfo { Status = "1/2 正在查询驱动更新", Detail = "Windows Update" });
            await Scanner.ScanDriverUpdatesAsync(false, ct);
            progress.Report(new ProgressInfo { Status = "2/2 正在检查软件更新", Detail = "winget 源" });
            await Scanner.ScanSoftwareUpdatesAsync(ct);
        }, TimeSpan.FromMinutes(5));
        if (!completed) return;

        var s = Ctx.State.Summary;
        Ui.Info("检查完成",
            $"驱动更新：{s.DriverUpdates} 个\n软件更新：{s.SoftwareUpdates} 个");
    }
}
