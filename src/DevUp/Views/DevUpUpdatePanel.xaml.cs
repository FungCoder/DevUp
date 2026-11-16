using System.Windows;
using System.Windows.Controls;
using DevUp.Core;

namespace DevUp.Views;

public partial class DevUpUpdatePanel : UserControl
{
    private bool _ready;

    public DevUpUpdatePanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AutoCheck.IsChecked = Ctx.Settings.AutoCheckDevUpUpdates;
            _ready = true;
            Ctx.Updates.Changed += Updates_Changed;
            Ctx.CleanupRules.Changed += Updates_Changed;
            Ctx.State.PropertyChanged += State_Changed;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _ready = false;
            Ctx.Updates.Changed -= Updates_Changed;
            Ctx.CleanupRules.Changed -= Updates_Changed;
            Ctx.State.PropertyChanged -= State_Changed;
        };
    }

    private void Updates_Changed(object? sender, EventArgs args) => Refresh();
    private void State_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Models.ScanState.IsBusy)) Refresh();
    }

    private void Refresh()
    {
        VersionText.Text = $"程序 {typeof(DevUpUpdatePanel).Assembly.GetName().Version!.ToString(3)} · " +
            $"规则 {Ctx.CleanupRules.Current.Version} · 扩展 {Ctx.CleanupRules.Rules.Count} 项";
        UpdateStatus.Text = Ctx.Updates.Status;
        RuleStatus.Text = Ctx.Updates.Available?.RuleNote ?? "程序与规则分别更新，离线继续使用现有版本";
        CheckButton.IsEnabled = !Ctx.State.IsBusy && !Ctx.Updates.IsChecking;
        ProgramButton.IsEnabled = !Ctx.State.IsBusy && Ctx.Updates.HasProgramUpdate;
        RulesButton.IsEnabled = !Ctx.State.IsBusy && Ctx.Updates.Available?.RulesAvailable == true;
        AutoCheck.IsEnabled = !Ctx.State.IsBusy;
    }

    private async void Check_Click(object sender, RoutedEventArgs args)
    {
        await Ctx.RunAsync("正在检查 DevUp 与规则更新…", () =>
        {
            ProgressDialog.Run("检查更新", "连接 GitHub 正式 Release…", async (_, ct) => await Ctx.Updates.CheckAsync(ct));
            return Task.CompletedTask;
        });
    }

    private async void Rules_Click(object sender, RoutedEventArgs args)
    {
        await Ctx.RunAsync("正在更新清理规则…", () =>
        {
            var done = ProgressDialog.Run("更新清理规则", "下载并校验规则数据包…",
                async (progress, ct) => await Ctx.Updates.InstallRulesAsync(progress, ct));
            if (done) Ui.Success("规则已更新", "无需重启程序，请重新扫描垃圾文件后再清理。");
            return Task.CompletedTask;
        });
    }

    private async void Program_Click(object sender, RoutedEventArgs args)
    {
        if (Ctx.State.IsBusy || !Ctx.Updates.HasProgramUpdate ||
            !Ui.Confirm("更新 DevUp", "下载并校验新版后将退出并重启 DevUp。旧版会保留一份以便恢复。\n\n是否继续？")) return;
        string? downloaded = null;
        var update = Ctx.Updates.Available!;
        string? job = null;
        await Ctx.RunAsync("正在下载新版 DevUp…", () =>
        {
            job = UpdateInstaller.CreateJobDirectory();
            ProgressDialog.Run("更新 DevUp", "下载并校验新版程序…", async (progress, ct) =>
                downloaded = await Ctx.Updates.DownloadProgramAsync(job, progress, ct));
            return Task.CompletedTask;
        });
        if (downloaded is null)
        {
            if (job is not null) UpdateInstaller.DiscardJob(job);
            return;
        }
        var started = false;
        await Ctx.RunAsync("正在准备更新并重启…", async () =>
        {
            await UpdateInstaller.StartAsync(downloaded, update.Program!.Digest, update.Version);
            started = true;
        });
        if (started) Application.Current.Shutdown();
        else if (job is not null) UpdateInstaller.DiscardJob(job);
    }

    private void Release_Click(object sender, RoutedEventArgs args)
        => Ui.OpenUrl(Ctx.Updates.Available?.Page.AbsoluteUri ?? "https://github.com/FungCoder/DevUp/releases/latest");

    private void AutoCheck_Changed(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        try
        {
            Ctx.Settings.AutoCheckDevUpUpdates = AutoCheck.IsChecked == true;
            Ctx.Settings.Save();
        }
        catch (Exception ex) { Ui.Error("无法保存更新设置", ex.Message); }
    }
}
