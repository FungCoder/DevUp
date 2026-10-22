using System.Windows;
using System.Windows.Controls;
using DevUp.Core;

namespace DevUp.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private async void BtnScanDevices_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunAsync("正在扫描设备与驱动仓库…", async () =>
        {
            await Scanner.ScanDevicesAsync();
            await Scanner.ScanDriverPackagesAsync();
            await Scanner.ScanSoftwareAsync();
        });

    /// <summary>点“需要处理的设备”卡片：跳到设备页并只看异常设备。</summary>
    private void ProblemCard_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateToDevices(problemOnly: true);

    /// <summary>点“可更新的软件”卡片：跳到软件页并只显示可更新项。</summary>
    private void SoftwareCard_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateToSoftware(onlyUpdates: true);

    /// <summary>点“可更新的驱动”卡片：跳到驱动与设备页。</summary>
    private void DriverCard_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateToDevices(problemOnly: false);

    private async void BtnScanDriverUpdates_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunAsync("正在查询 Windows Update 驱动…", async () =>
        {
            await Scanner.ScanDriverUpdatesAsync(false);
            Ui.Info("驱动更新", $"发现 {Ctx.State.DriverUpdates.Count} 个可用驱动更新。");
        });

    private async void BtnScanSoftwareUpdates_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunAsync("正在通过 winget 检查软件更新…", async () =>
        {
            await Scanner.ScanSoftwareUpdatesAsync();
            Ui.Info("软件更新", $"发现 {Ctx.State.Summary.SoftwareUpdates} 个可用软件更新。");
        });

    private async void BtnBackup_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunAsync("正在导出全部驱动包…", async () =>
        {
            var backup = await Ctx.Backups.BackupAllAsync();
            Ctx.State.Backups.Clear();
            foreach (var b in Ctx.Backups.ListBackups()) Ctx.State.Backups.Add(b);
            Ui.Info("备份完成", $"{backup.Name}\n{backup.PackageCount} 个驱动包 · {backup.SizeText}");
        });
}
