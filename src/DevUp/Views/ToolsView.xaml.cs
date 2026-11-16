using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;
using Microsoft.Win32;

namespace DevUp.Views;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdatePaths();
            RefreshBackups();
            LoadStartupMode();
            LoadSoftwareNetwork();
        };
    }

    private bool _startupModeReady;

    internal void ShowUpdates()
        => Dispatcher.BeginInvoke(new Action(() => DevUpUpdates.BringIntoView()));

    private void LoadSoftwareNetwork()
    {
        foreach (ComboBoxItem item in CmbSoftwareProxy.Items)
            if (item.Tag as string == Ctx.Settings.SoftwareProxyMode) CmbSoftwareProxy.SelectedItem = item;
        SoftwareProxyAddress.Text = Ctx.Settings.SoftwareProxyAddress;
    }

    private void CmbSoftwareProxy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SoftwareProxyAddress is not null)
            SoftwareProxyAddress.IsEnabled = (CmbSoftwareProxy.SelectedItem as ComboBoxItem)?.Tag as string == SoftwareNetwork.CustomMode;
    }

    private AppSettings ReadSoftwareNetwork()
    {
        var mode = SoftwareNetwork.ValidateMode((CmbSoftwareProxy.SelectedItem as ComboBoxItem)?.Tag as string ?? "");
        var address = mode == SoftwareNetwork.CustomMode
            ? SoftwareNetwork.ValidateProxy(SoftwareProxyAddress.Text).AbsoluteUri : "";
        return new AppSettings { SoftwareProxyMode = mode, SoftwareProxyAddress = address };
    }

    private void BtnSaveSoftwareNetwork_Click(object sender, RoutedEventArgs e)
    {
        var oldMode = Ctx.Settings.SoftwareProxyMode;
        var oldAddress = Ctx.Settings.SoftwareProxyAddress;
        try
        {
            var settings = ReadSoftwareNetwork();
            Ctx.Settings.SoftwareProxyMode = settings.SoftwareProxyMode;
            Ctx.Settings.SoftwareProxyAddress = settings.SoftwareProxyAddress;
            Ctx.Settings.Save();
            SoftwareNetworkResult.Text = "网络设置已保存，对后续软件检查、更新和安装包下载生效。";
        }
        catch (Exception ex)
        {
            Ctx.Settings.SoftwareProxyMode = oldMode;
            Ctx.Settings.SoftwareProxyAddress = oldAddress;
            Ui.Warn("无法保存网络设置", ex.Message);
        }
    }

    private void BtnTestSoftwareNetwork_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSoftwareNetwork();
            var url = SoftwareTestUrl.Text.Trim();
            var result = "";
            var done = ProgressDialog.Run("正在测试软件网络", "检查所填地址的响应", async (progress, ct) =>
            {
                progress.Report(new ProgressInfo { Percent = -1, Status = "正在连接测试地址…" });
                await SoftwareNetwork.PrepareWingetAsync(settings, ct);
                result = await SoftwareNetwork.TestAsync(settings, url, ct);
            });
            SoftwareNetworkResult.Text = done ? result : "连接测试已取消。";
        }
        catch (OperationCanceledException) { SoftwareNetworkResult.Text = "连接测试已取消。"; }
        catch (Exception ex)
        {
            SoftwareNetworkResult.Text = "连接测试未完成：" + ex.Message;
            Ui.Log("软件网络测试失败：" + ex.Message, "WARN");
        }
    }

    private void LoadStartupMode()
    {
        var mode = (Ctx.Settings.StartupMode ?? AppSettings.ModeAsk).Trim().ToLowerInvariant();
        foreach (var item in CmbStartup.Items)
        {
            if (item is ComboBoxItem c && (c.Tag as string) == mode)
            {
                CmbStartup.SelectedItem = c;
                break;
            }
        }
        UpdateStartupHint(mode);
        _startupModeReady = true;
    }

    private void CmbStartup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_startupModeReady) return;

        var mode = (CmbStartup.SelectedItem as ComboBoxItem)?.Tag as string ?? AppSettings.ModeAsk;
        Ctx.Settings.StartupMode = mode;
        Ctx.Settings.Save();
        UpdateStartupHint(mode);
        Ui.Log("启动方式已设为：" + mode switch
        {
            AppSettings.ModeAdmin => "直接以管理员身份启动",
            AppSettings.ModeNormal => "以普通权限启动",
            _ => "每次询问"
        });
    }

    private void UpdateStartupHint(string mode)
    {
        StartupModeHint.Text = mode switch
        {
            AppSettings.ModeAdmin => "下次启动会直接弹出 UAC 确认；若取消，则自动按普通权限运行。",
            AppSettings.ModeNormal => "下次启动不再询问；驱动安装等系统级操作仍会单独请求提权。",
            _ => "下次启动会先显示权限选择窗口。"
        };
    }

    private void UpdatePaths()
    {
        BackupPathText.Text = Ctx.Settings.BackupRoot;
        DownloadPathText.Text = Ctx.Settings.DownloadRoot;
    }

    private void RefreshBackups()
    {
        Ctx.State.Backups.Clear();
        foreach (var b in Ctx.Backups.ListBackups()) Ctx.State.Backups.Add(b);

        var count = Ctx.State.Backups.Count;
        BackupCountText.Text = count > 0 ? $"共 {count} 个备份" : "暂无备份";
        BackupEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnBackupAll_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunAsync("正在导出全部驱动包…", async () =>
        {
            var backup = await Ctx.Backups.BackupAllAsync();
            RefreshBackups();
            Ui.Info("备份完成", $"{backup.Name}\n{backup.PackageCount} 个驱动包 · {backup.SizeText}");
        });

    private async void BtnRestorePoint_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunAsync("正在创建系统还原点…", async () =>
        {
            await Ctx.Backups.CreateRestorePointAsync("DevUp 手动还原点");
            Ui.Info("完成", "已创建系统还原点。");
        });

    private async void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackupItem backup) return;

        if (!Ui.Confirm("还原驱动", $"将把备份《{backup.Name}》中的驱动包重新安装到系统。\n建议先重启后再执行。"))
            return;

        await Ctx.RunAsync("正在还原驱动…", async () =>
        {
            var message = await Ctx.Backups.RestoreAsync(backup.Path);
            Ui.Info("还原结果", message);
            await Scanner.ScanDevicesAsync();
        });
    }

    private void BtnExportJson_Click(object sender, RoutedEventArgs e) => Export(json: true);

    private void BtnExportHtml_Click(object sender, RoutedEventArgs e) => Export(json: false);

    private void Export(bool json)
    {
        try
        {
            // 注意：SaveFileDialog.Filter 必须用 "|" 分隔"说明|模式"，
            // 写成 "JSON 文件 (*.json)" 会在赋值时直接抛 ArgumentException。
            var dialog = new SaveFileDialog
            {
                Title = "导出报告",
                FileName = $"DevUp报告_{DateTime.Now:yyyyMMdd_HHmm}.{(json ? "json" : "html")}",
                Filter = json
                    ? "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                    : "HTML 文件 (*.html)|*.html|所有文件 (*.*)|*.*",
                AddExtension = true,
                InitialDirectory = Ctx.Settings.BackupRoot,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true) return;

            if (json) ReportService.ExportJson(Ctx.State, dialog.FileName);
            else ReportService.ExportHtml(Ctx.State, dialog.FileName);
            Ui.Info("导出完成", dialog.FileName);
        }
        catch (Exception ex)
        {
            Ui.Error("导出失败", ex.Message);
        }
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e) => Ui.OpenInExplorer(Ui.LogDir);

    private void BtnOpenBackup_Click(object sender, RoutedEventArgs e)
    {
        var dir = AppSettings.EnsureDir(Ctx.Settings.BackupRoot);
        Ui.OpenInExplorer(dir);
    }

    private void BtnOpenDownload_Click(object sender, RoutedEventArgs e)
    {
        var dir = AppSettings.EnsureDir(Ctx.Settings.DownloadRoot);
        Ui.OpenInExplorer(dir);
    }

    private void BtnPickBackup_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("选择驱动备份目录", Ctx.Settings.BackupRoot);
        if (picked is null) return;

        Ctx.Settings.BackupRoot = AppSettings.EnsureDir(picked);
        Ctx.Settings.Save();
        UpdatePaths();
        RefreshBackups();
        Ui.Log("备份目录已改为 " + picked);
    }

    private void BtnPickDownload_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("选择下载目录", Ctx.Settings.DownloadRoot);
        if (picked is null) return;

        Ctx.Settings.DownloadRoot = AppSettings.EnsureDir(picked);
        Ctx.Settings.Save();
        UpdatePaths();
        Ui.Log("下载目录已改为 " + picked);
    }

    /// <summary>目录选择对话框（.NET 8 原生 OpenFolderDialog）。</summary>
    private static string? PickFolder(string title, string current)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        try
        {
            var start = Ctx.Settings.BackupRoot;
            if (!string.IsNullOrWhiteSpace(current)) start = current;
            if (Directory.Exists(start)) dialog.InitialDirectory = start;
        }
        catch { }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
