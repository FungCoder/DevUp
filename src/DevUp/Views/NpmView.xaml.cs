using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

namespace DevUp.Views;

/// <summary>npm 全局包（AI 命令行工具等）的检查、升级与卸载。</summary>
public partial class NpmView : UserControl
{
    public ObservableCollection<NpmPackageItem> Packages { get; } = new();

    private NpmEnvironment _env = new();
    private string _loadError = "";
    private bool _busy;
    private bool _loaded;
    private bool _waitingForBusy;

    public NpmView()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        NpmHint.Text = "";
    }

    private static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static string OrDash(string? text)
        => string.IsNullOrWhiteSpace(text) ? "—" : text!;

    // ---------------- 初次加载 ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;

        // 启动时的全量扫描还没结束：等它结束再读 npm，避免两个扫描互相抢状态栏
        if (Ctx.State.IsBusy)
        {
            if (!_waitingForBusy)
            {
                _waitingForBusy = true;
                Ctx.State.PropertyChanged += OnStatePropertyChanged;
            }
            EmptyTitle.Text = "正在等待初始扫描完成…";
            EmptyDesc.Text = "完成后会自动读取 npm 全局包";
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        await LoadAsync(checkUpdates: true);
    }

    private async void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Ctx.State.IsBusy) || Ctx.State.IsBusy) return;

        Ctx.State.PropertyChanged -= OnStatePropertyChanged;
        _waitingForBusy = false;
        if (!_loaded) await LoadAsync(checkUpdates: true);
    }

    private async Task LoadAsync(bool checkUpdates)
    {
        if (_busy) return;

        _busy = true;
        SetBusy(true);
        _loadError = "";

        // 检查期间先给出明确提示，避免空白页面让人以为卡住了
        if (!_env.Available)
        {
            if (SetupPanel is not null) SetupPanel.Visibility = Visibility.Collapsed;
            if (EmptyPanel is not null)
            {
                EmptyPanel.Visibility = Visibility.Visible;
                EmptyTitle.Text = "正在检查 Node.js 环境…";
                EmptyDesc.Text = "会检测 Node.js 与 npm 是否已经安装";
            }
        }

        try
        {
            Ctx.Npm.ResetProbe();

            var completed = await Ctx.RunScanAsync("正在读取 npm 全局包…", async (progress, ct) =>
            {
                progress.Report(new ProgressInfo { Status = "1/3 正在检查 Node.js 环境" });
                _env = await Ctx.Npm.ProbeAsync(ct);
                Packages.Clear();
                if (PackageGrid is not null) PackageGrid.SelectedItem = null;

                if (_env.Available)
                {
                    progress.Report(new ProgressInfo { Status = "2/3 正在读取全局包清单" });
                    var (list, error) = await Ctx.Npm.GetPackagesAsync(_env, ct);
                    foreach (var package in list) Packages.Add(package);
                    _loadError = error;

                    if (checkUpdates && Packages.Count > 0)
                    {
                        progress.Report(new ProgressInfo { Status = "3/3 正在检查包更新",
                            Detail = $"已找到 {Packages.Count} 个全局包" });
                        var (checkedOk, checkError) = await Ctx.Npm.CheckOutdatedAsync(_env, Packages, ct);
                        if (!checkedOk)
                            _loadError = string.IsNullOrWhiteSpace(_loadError)
                                ? checkError
                                : _loadError + "\n" + checkError;
                    }
                }

                ApplyFilter();
                UpdateEnvironment();
                UpdateSelectionState();
            });

            _loaded = completed;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
            ApplyFilter();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnRefresh.IsEnabled = !busy;
        BtnCheck.IsEnabled = !busy;
        BtnUpgradeAll.IsEnabled = !busy;
        PackageGrid.IsEnabled = !busy;
    }

    // ---------------- 列表与筛选 ----------------

    private void ApplyFilter()
    {
        if (CmbFilter is null || PackageCountText is null || NpmHint is null) return;

        var view = CollectionViewSource.GetDefaultView(Packages);
        if (view is null) return;

        var tag = (CmbFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        view.Filter = o => o is NpmPackageItem p && (tag != "updates" || p.HasUpdate);
        view.Refresh();

        var shown = view.Cast<object>().Count();
        PackageCountText.Text = $"{shown} / {Packages.Count}";

        UpdateEmptyState(shown, tag);
        UpdateToolbar();
        UpdateSelectionState();
    }

    private void UpdateEmptyState(int shown, string tag)
    {
        if (EmptyPanel is null) return;

        // 没有可用的 npm：展示“友好引导”，而不是一串错误文本
        if (!_env.Available)
        {
            var (title, desc) = BuildSetupMessage();
            if (SetupPanel is not null)
            {
                SetupPanel.Visibility = Visibility.Visible;
                SetupTitle.Text = title;
                SetupDesc.Text = desc;
            }
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (SetupPanel is not null) SetupPanel.Visibility = Visibility.Collapsed;

        if (shown > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyPanel.Visibility = Visibility.Visible;

        if (Packages.Count == 0 && !string.IsNullOrWhiteSpace(_loadError))
        {
            EmptyTitle.Text = "读取 npm 全局包失败";
            EmptyDesc.Text = _loadError;
        }
        else if (Packages.Count == 0)
        {
            EmptyTitle.Text = "没有全局安装的 npm 包";
            EmptyDesc.Text = "在终端执行 npm install -g <包名> 安装后，点「刷新全局包」。";
        }
        else if (tag == "updates")
        {
            EmptyTitle.Text = "没有可升级的包";
            EmptyDesc.Text = "所有已安装的全局包都是最新版本。";
        }
        else
        {
            EmptyTitle.Text = "没有匹配的包";
            EmptyDesc.Text = "试试切换上方的筛选条件。";
        }
    }

    /// <summary>把“没有 Node.js / npm 坏了”翻译成普通用户能看懂的说明。</summary>
    private (string Title, string Desc) BuildSetupMessage()
    {
        if (_env.NpmBroken)
        {
            if (!_env.NodeDetected)
            {
                return ("检测到了残留的 npm，但 Node.js 不可用",
                    "找到一个无法运行的 npm 命令，但系统里没有检测到 Node.js。" +
                    "建议重新安装官方 Node.js（安装包会自带 npm），完成后点「重新检测」。");
            }

            return ("Node.js 已安装，但 npm 不可用",
                $"检测到 Node.js {OrDash(_env.NodeVersion)}，但 npm 无法正常运行。" +
                "通常是安装不完整或 npm 文件被清理软件删除。建议运行官方 Node.js 安装包并选择 Repair（修复），已安装的全局包会保留。");
        }

        if (_env.NodeDetected)
        {
            return ("已安装 Node.js，但没有找到 npm",
                $"检测到 Node.js {_env.NodeVersion}，但没有找到可用的 npm 命令。" +
                "请安装或修复官方 Node.js（安装包会自带 npm），完成后点「重新检测」。");
        }

        return ("这个功能需要 Node.js",
            "检测到你的电脑还没有安装 Node.js。Node.js 自带 npm，安装后即可在这里检查、升级和卸载 AI 命令行工具，以及 pnpm 等全局包。");
    }

    private void UpdateToolbar()
    {
        if (BtnUpgradeAll is null) return;

        BtnRefresh.IsEnabled = !_busy;
        BtnCheck.IsEnabled = !_busy && _env.Available;
        BtnUpgradeAll.IsEnabled = !_busy && _env.Available && Packages.Any(p => p.HasUpdate);
        PackageGrid.IsEnabled = !_busy;
    }

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void PackageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    // ---------------- 详情与选中态 ----------------

    private void UpdateSelectionState()
    {
        if (NpmHint is null || DetailBody is null) return;

        var item = PackageGrid?.SelectedItem as NpmPackageItem;

        if (item is null)
        {
            DetailEmpty.Visibility = Visibility.Visible;
            DetailBody.Visibility = Visibility.Collapsed;
            DetailActions.Visibility = Visibility.Collapsed;
            DetailBadge.Visibility = Visibility.Collapsed;

            if (!_env.Available)
            {
                NpmHint.Text = "npm 不可用，请先安装或修复 Node.js";
            }
            else
            {
                var updates = Packages.Count(p => p.HasUpdate);
                var hint = $"共 {Packages.Count} 个全局包";
                if (updates > 0) hint += $"，{updates} 个可升级";
                if (!string.IsNullOrWhiteSpace(_loadError)) hint += " · 更新检查未完成，可点「检查更新」重试";
                NpmHint.Text = hint;
            }

            NpmHint.ToolTip = string.IsNullOrWhiteSpace(_loadError) ? null : _loadError;
            UpdateToolbar();
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailBody.Visibility = Visibility.Visible;
        DetailActions.Visibility = Visibility.Visible;

        DetailName.Text = item.Name;
        DetailMeta.Text = item.IsCorePackage ? "npm 全局包 · 包管理器自身" : "npm 全局包";
        DetailInstalled.Text = OrDash(item.InstalledVersion);
        DetailLatest.Text = item.Checked ? item.LatestDisplay : "—（点「检查更新」获取）";
        DetailDescription.Text = string.IsNullOrWhiteSpace(item.Description) ? "这个包没有提供描述。" : item.Description;
        DetailLocation.Text = OrDash(item.Location);

        DetailBadge.Visibility = Visibility.Visible;
        if (item.HasUpdate)
        {
            DetailBadge.Background = B("#EAF1FE");
            DetailBadgeText.Foreground = B("#2D6CDF");
            DetailBadgeText.Text = "可更新";
        }
        else if (item.Checked)
        {
            DetailBadge.Background = B("#EAF8EF");
            DetailBadgeText.Foreground = B("#15803D");
            DetailBadgeText.Text = "已是最新";
        }
        else
        {
            DetailBadge.Background = B("#F1F3F7");
            DetailBadgeText.Foreground = B("#7B8794");
            DetailBadgeText.Text = "未检查";
        }

        BtnUpgradeThis.IsEnabled = item.HasUpdate;
        BtnUpgradeThis.Content = item.HasUpdate
            ? $"升级到 {item.LatestVersion}"
            : "升级到最新版";

        BtnUninstallThis.IsEnabled = !item.IsCorePackage;
        BtnUninstallThis.ToolTip = item.IsCorePackage
            ? "npm / corepack 是包管理器自身，不提供卸载，避免把 npm 环境弄坏"
            : "从 npm 全局目录删除这个包";

        NpmHint.Text = item.HasUpdate
            ? $"已选择：{item.Name}　{item.InstalledVersion} → {item.LatestVersion}"
            : item.Checked
                ? $"已选择：{item.Name}（已是最新版本）"
                : $"已选择：{item.Name}（尚未检查最新版本）";
        NpmHint.ToolTip = null;

        UpdateToolbar();
    }

    private void UpdateEnvironment()
    {
        if (EnvLine is null) return;

        EnvLine.Text = _env.Available
            ? $"Node.js {OrDash(_env.NodeVersion)}　·　npm {_env.NpmVersion}"
            : _env.NpmBroken
                ? _env.NodeDetected
                    ? $"Node.js {_env.NodeVersion}　·　npm 不可用"
                    : "未检测到 Node.js　·　npm 不可用"
                : _env.NodeDetected
                    ? $"Node.js {_env.NodeVersion}　·　未找到 npm"
                    : "尚未安装 Node.js";

        EnvNode.Text = _env.NodeDetected ? _env.NodeVersion : "未安装";
        EnvNpm.Text = _env.Available ? OrDash(_env.NpmVersion) : "—";
        EnvPath.Text = _env.Available ? OrDash(_env.NpmPath) : "—";
        EnvRoot.Text = _env.Available ? OrDash(_env.GlobalRoot) : "—";

        EnvErrorPanel.Visibility = _env.Available ? Visibility.Collapsed : Visibility.Visible;
        if (EnvErrorTitle is not null)
        {
            EnvErrorTitle.Text = _env.NpmBroken
                ? _env.NodeDetected
                    ? "Node.js 已安装，但 npm 不可用"
                    : "检测到了残留的 npm，但 Node.js 不可用"
                : _env.NodeDetected
                    ? "没有找到 npm"
                    : "尚未检测到 Node.js";
        }
        EnvError.Text = _env.Error;
        EnvDetail.Text = string.IsNullOrWhiteSpace(_env.Detail)
            ? ""
            : "检测详情：\n" + _env.Detail;

        if (DetailEmpty is not null)
        {
            DetailEmpty.Text = _env.Available
                ? "在左侧点击一个 npm 全局包，这里会显示版本、说明和安装位置。"
                : "安装 Node.js 后，这里会显示所选全局包的版本、说明和安装位置。";
        }

        UpdateToolbar();
    }

    // ---------------- 检查更新 ----------------

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(checkUpdates: true);

    private async void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        Ctx.Npm.ResetProbe();
        _loaded = false;
        await LoadAsync(checkUpdates: true);
    }

    private async void BtnCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_env.Available)
        {
            Ui.Info("npm 不可用", _env.Error + "\n\n安装或修复 Node.js 后再点「重新检测」。");
            return;
        }

        if (Packages.Count == 0)
        {
            await LoadAsync(checkUpdates: true);
            return;
        }

        _busy = true;
        SetBusy(true);
        try
        {
            var completed = await Ctx.RunScanAsync("正在检查 npm 包更新…", async (_, ct) =>
            {
                var (ok, error) = await Ctx.Npm.CheckOutdatedAsync(_env, Packages, ct);
                _loadError = ok ? "" : error;
                ApplyFilter();
                UpdateEnvironment();
                UpdateSelectionState();
            });

            if (!completed) return;
            if (string.IsNullOrWhiteSpace(_loadError))
            {
                var count = Packages.Count(p => p.HasUpdate);
                if (count > 0) Ui.Info("检查完成", $"发现 {count} 个可升级的全局包。");
                else Ui.Info("检查完成", "所有全局包都是最新版本。");
            }
            else
            {
                Ui.Warn("检查未完成", _loadError);
            }
        }
        finally
        {
            _busy = false;
            SetBusy(false);
            ApplyFilter();
        }
    }

    // ---------------- 升级 ----------------

    private async void BtnUpgradeThis_Click(object sender, RoutedEventArgs e)
    {
        if (PackageGrid.SelectedItem is not NpmPackageItem item) return;
        if (!item.HasUpdate) return;

        if (!Ui.Confirm("升级 npm 包",
                $"将把 {item.Name} 从 {item.InstalledVersion} 升级到 {item.LatestVersion}。\n\n" +
                "升级通过 npm 全局安装完成，可能需要下载几十 MB。"))
            return;

        await UpgradePackagesAsync(new List<NpmPackageItem> { item });
    }

    private async void BtnUpgradeAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = Packages.Where(p => p.HasUpdate).ToList();
        if (pending.Count == 0)
        {
            Ui.Info("没有可升级项", "点「检查更新」确认最新版本后再试。");
            return;
        }

        if (!Ui.Confirm("升级全部 npm 包",
                $"将依次升级 {pending.Count} 个全局包到最新版本。\n\n" +
                "升级过程中请保持网络连接；每个包装完后才会开始下一个。"))
            return;

        await UpgradePackagesAsync(pending);
    }

    private async Task UpgradePackagesAsync(List<NpmPackageItem> pending)
    {
        if (pending.Count == 0 || !_env.Available) return;

        var ok = 0;
        var failures = new List<string>();
        var completed = false;

        try
        {
            completed = ProgressDialog.Run(
                pending.Count == 1 ? "正在升级 npm 包" : "正在批量升级 npm 包",
                pending.Count == 1
                    ? $"{pending[0].Name} {pending[0].InstalledVersion} → {pending[0].LatestVersion}"
                    : $"共 {pending.Count} 个包",
                async (progress, ct) =>
                {
                    for (var i = 0; i < pending.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var item = pending[i];
                        var index = i;
                        var share = 100.0 / pending.Count;
                        var sliceStart = index * share;

                        var scoped = new Progress<ProgressInfo>(p =>
                        {
                            var overall = p.Percent < 0
                                ? -1
                                : sliceStart + Math.Clamp(p.Percent, 0, 100) * share / 100.0;

                            // 单个包完成时不要提前把整条进度拉满，给后面的包留出空间
                            if (p.Percent >= 100 && index < pending.Count - 1)
                                overall = Math.Min(overall, sliceStart + share - 0.5);

                            progress.Report(new ProgressInfo
                            {
                                Percent = overall,
                                Status = pending.Count == 1
                                    ? p.Status
                                    : $"({index + 1}/{pending.Count}) {p.Status}",
                                Detail = string.IsNullOrWhiteSpace(p.Detail)
                                    ? item.Name
                                    : $"{item.Name} · {p.Detail}",
                                CanCancel = true
                            });
                        });

                        var (code, output) = await Ctx.Npm.UpdateAsync(_env, item.Name, scoped, ct);
                        if (code == 0)
                        {
                            ok++;
                        }
                        else
                        {
                            var info = NpmService.ExplainFailure(output);
                            Ui.Log($"npm 升级失败（{item.Name}）：{info.Reason} {info.Detail}", "ERROR");
                            failures.Add($"{item.Name}：{info.Reason}");
                        }
                    }

                    progress.Report(new ProgressInfo { Percent = 100, Status = "升级完成", CanCancel = false });
                });
        }
        catch (Exception ex)
        {
            Ui.Error("升级失败", ex.Message);
        }

        _loaded = false;
        await LoadAsync(checkUpdates: true);

        if (!completed)
        {
            Ui.Warn("升级已取消", $"已完成 {ok} 个，其余未执行。");
            return;
        }

        if (failures.Count == 0)
        {
            Ui.Success("升级完成", $"成功升级 {ok} 个全局包。");
        }
        else
        {
            Ui.Warn("部分包未升级",
                $"成功 {ok} 个，未完成 {failures.Count} 个。\n\n" +
                string.Join("\n", failures.Take(6)) +
                "\n\n可单独重试，或打开 npm 包主页查看说明。");
        }
    }

    // ---------------- 卸载 ----------------

    private async void BtnUninstallThis_Click(object sender, RoutedEventArgs e)
    {
        if (PackageGrid.SelectedItem is not NpmPackageItem item) return;

        if (item.IsCorePackage)
        {
            Ui.Info("不能卸载 npm", "npm / corepack 是包管理器自身，卸载后会无法继续管理其他全局包。");
            return;
        }

        if (!_env.Available)
        {
            Ui.Info("npm 不可用", _env.Error);
            return;
        }

        if (!Ui.Confirm("卸载 npm 包",
                $"将从全局 npm 目录卸载：\n{item.Name} {item.InstalledVersion}\n\n" +
                "依赖这个包的命令会立即失效。是否继续？"))
            return;

        var code = 0;
        var output = "";
        var completed = false;

        try
        {
            completed = ProgressDialog.Run("正在卸载 npm 包",
                $"{item.Name} {item.InstalledVersion}",
                async (progress, ct) =>
                {
                    (code, output) = await Ctx.Npm.UninstallAsync(_env, item.Name, progress, ct);
                });
        }
        catch (Exception ex)
        {
            Ui.Error("卸载失败", ex.Message);
        }

        _loaded = false;
        await LoadAsync(checkUpdates: true);

        if (!completed)
        {
            Ui.Warn("卸载已取消", "没有删除任何内容。");
            return;
        }

        if (code == 0)
        {
            Ui.Success("卸载完成", $"{item.Name} 已从全局 npm 目录移除。");
            return;
        }

        var info = NpmService.ExplainFailure(output);
        Ui.Log($"npm 卸载失败（{item.Name}）：{info.Reason} {info.Detail}", "ERROR");
        var text = info.Reason + "\n\n" + info.Hint;
        if (!string.IsNullOrWhiteSpace(info.Detail)) text += "\n\n" + info.Detail;
        Ui.Warn("卸载未完成", text);
    }

    // ---------------- 打开官方页面 ----------------

    private void BtnOpenNpm_Click(object sender, RoutedEventArgs e)
    {
        if (PackageGrid.SelectedItem is not NpmPackageItem item) return;

        var url = item.Homepage;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            url = $"https://www.npmjs.com/package/{item.Name}";

        Ui.OpenUrl(url);
    }

    private void BtnOpenNode_Click(object sender, RoutedEventArgs e)
        => Ui.OpenUrl("https://nodejs.org/zh-cn/download");
}
