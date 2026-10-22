using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

namespace DevUp.Views;

public partial class SoftwareView : UserControl
{
    private readonly Dictionary<string, WingetPackageDetails?> _detailsCache = new();
    private CancellationTokenSource? _detailsCts;

    private WingetPackageDetails? _details;
    private string? _detailsKey;
    private string? _downloadedPath;
    private string? _downloadedKey;

    public SoftwareView()
    {
        InitializeComponent();
        Loaded += (_, _) => { ApplyFilter(); _ = LoadDetailsAsync(); };
        // 扫描是异步的：清单或可用版本变化后刷新筛选与计数
        Ctx.State.Software.CollectionChanged += (_, _) => ApplyFilter();
        Scanner.SoftwareChanged += () => Dispatcher.Invoke(ApplyFilter);
        SoftHint.Text = "";
    }

    private static string SelectedFilterTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

    private static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // ---------------- 列表与筛选 ----------------

    private void ApplyFilter()
    {
        // XAML 加载期间事件可能先于字段赋值触发
        if (CmbFilter is null || SoftHint is null || SoftCountText is null) return;

        var view = CollectionViewSource.GetDefaultView(Ctx.State.Software);
        if (view is null) return;

        var tag = SelectedFilterTag(CmbFilter);
        view.Filter = o => o is SoftwareItem s && (tag switch
        {
            "updates" => s.HasUpdate && !s.Ignored,
            "ignored" => s.Ignored,
            _ => !s.Ignored
        });
        view.Refresh();

        var shown = view.Cast<object>().Count();
        var total = Ctx.State.Software.Count;

        SoftCountText.Text = $"{shown} / {total}";
        UpdateEmptyState(shown, total, tag);
        UpdateSelectionState();
    }

    private void UpdateEmptyState(int shown, int total, string tag)
    {
        if (EmptyPanel is null) return;

        if (shown > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyPanel.Visibility = Visibility.Visible;
        var ignored = Ctx.State.Software.Count(s => s.Ignored);
        if (total == 0)
        {
            EmptyTitle.Text = "还没有软件清单";
            EmptyDesc.Text = "点上方「刷新已装清单」读取桌面软件与 MSIX 应用";
        }
        else if (tag == "ignored")
        {
            EmptyTitle.Text = "没有已忽略的软件更新";
            EmptyDesc.Text = "在「仅可更新」中选中一项，点右侧「忽略此更新」，之后可在这里恢复";
        }
        else if (tag == "updates")
        {
            EmptyTitle.Text = "没有可更新的软件";
            EmptyDesc.Text = ignored > 0
                ? $"有 {ignored} 个更新已被忽略，可在筛选「已忽略」中查看并恢复"
                : "当前 winget 源中没有适用于已装软件的新版本";
        }
        else
        {
            EmptyTitle.Text = "没有匹配的软件";
            EmptyDesc.Text = ignored > 0
                ? $"有 {ignored} 个更新已被忽略，可在筛选「已忽略」中查看并恢复"
                : "试试切换上方的筛选条件";
        }
    }

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void SoftwareGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 窗口未激活时第一次点击可能只激活窗口，这里显式选中行
        var row = ItemsControl.ContainerFromElement(SoftwareGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row is not null && !row.IsSelected) SoftwareGrid.SelectedItem = row.Item;
    }

    private void SoftwareGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionState();
        _ = LoadDetailsAsync();
    }

    /// <summary>供概览页跳转使用：切换“仅显示可更新”。</summary>
    public void ShowOnlyUpdates(bool onlyUpdates)
    {
        if (CmbFilter is null) return;

        var tag = onlyUpdates ? "updates" : "all";
        foreach (var obj in CmbFilter.Items)
            if (obj is ComboBoxItem c && (c.Tag as string) == tag)
            {
                CmbFilter.SelectedItem = c;
                break;
            }
        ApplyFilter();
    }

    // ---------------- 选中态与详情 ----------------

    private void UpdateSelectionState()
    {
        if (SoftHint is null || DetailBody is null) return;

        var total = Ctx.State.Software.Count;
        var updates = Ctx.State.Software.Count(s => s.HasUpdate && !s.Ignored);
        var ignored = Ctx.State.Software.Count(s => s.Ignored);
        var item = SoftwareGrid?.SelectedItem as SoftwareItem;

        if (item is null)
        {
            DetailEmpty.Visibility = Visibility.Visible;
            DetailBody.Visibility = Visibility.Collapsed;
            DetailActions.Visibility = Visibility.Collapsed;
            DetailBadge.Visibility = Visibility.Collapsed;

            SoftHint.Text = total == 0
                ? "尚未读取软件清单"
                : updates > 0
                    ? $"可更新 {updates} 项　·　共 {total} 个软件" + (ignored > 0 ? $"　·　已忽略 {ignored} 项" : "")
                    : ignored > 0
                        ? $"没有待处理的更新　·　已忽略 {ignored} 项，可在筛选中查看并恢复"
                        : $"没有可更新的软件　·　共 {total} 个";
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailBody.Visibility = Visibility.Visible;
        DetailActions.Visibility = Visibility.Visible;

        DetailName.Text = item.DisplayName;
        DetailMeta.Text = string.Join("　·　",
            new[] { item.Publisher, item.Source }.Where(s => !string.IsNullOrWhiteSpace(s)));
        DetailInstalled.Text = string.IsNullOrWhiteSpace(item.DisplayVersion) ? "—" : item.DisplayVersion;
        DetailAvailable.Text = string.IsNullOrWhiteSpace(item.AvailableVersion) ? "—" : item.AvailableVersion;
        DetailId.Text = string.IsNullOrWhiteSpace(item.WingetId) ? "未匹配" : item.WingetId!;
        DetailTypeDate.Text = _details is null
            ? "—"
            : string.Join("　·　", new[]
            {
                string.IsNullOrWhiteSpace(_details.InstallerType) ? "" : _details.InstallerType,
                string.IsNullOrWhiteSpace(_details.ReleaseDate) ? "" : _details.ReleaseDate
            }.Where(s => s.Length > 0));

        DetailBadge.Visibility = Visibility.Visible;
        if (item.Ignored)
        {
            DetailBadge.Background = B("#F1F3F7");
            DetailBadgeText.Foreground = B("#7B8794");
            DetailBadgeText.Text = "已忽略";
        }
        else if (item.HasUpdate)
        {
            DetailBadge.Background = B("#EAF1FE");
            DetailBadgeText.Foreground = B("#2D6CDF");
            DetailBadgeText.Text = "可更新";
        }
        else if (item.HasWingetMatch)
        {
            DetailBadge.Background = B("#EAF8EF");
            DetailBadgeText.Foreground = B("#15803D");
            DetailBadgeText.Text = "已是最新";
        }
        else
        {
            DetailBadge.Background = B("#F1F3F7");
            DetailBadgeText.Foreground = B("#7B8794");
            DetailBadgeText.Text = "未匹配";
        }

        BtnUpgradeThis.IsEnabled = item.HasUpdate && !item.Ignored;
        BtnDownloadThis.IsEnabled = !string.IsNullOrEmpty(item.WingetId);

        BtnIgnoreThis.Visibility = item.HasUpdate || item.Ignored ? Visibility.Visible : Visibility.Collapsed;
        BtnIgnoreThis.Content = item.Ignored ? "恢复此更新" : "忽略此更新";

        SoftHint.Text = item.Ignored
            ? $"已选择：{item.DisplayName}　{item.DisplayVersion} → {item.AvailableVersion}（已忽略，可恢复）"
            : item.HasUpdate
                ? $"已选择：{item.DisplayName}　{item.DisplayVersion} → {item.AvailableVersion}"
                : $"已选择：{item.DisplayName}（当前没有可用更新）";
    }

    private async Task LoadDetailsAsync()
    {
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = null;

        _details = null;
        _detailsKey = null;
        _downloadedPath = null;
        _downloadedKey = null;

        if (InfoBody is null) return;

        InfoLoading.Visibility = Visibility.Collapsed;
        InfoError.Visibility = Visibility.Collapsed;
        InfoBody.Visibility = Visibility.Collapsed;
        LocalHashes.Visibility = Visibility.Collapsed;
        VerifyBadge.Visibility = Visibility.Collapsed;
        InfoEmpty.Visibility = Visibility.Collapsed;

        var item = SoftwareGrid?.SelectedItem as SoftwareItem;
        if (item is null) return;

        if (string.IsNullOrEmpty(item.WingetId))
        {
            InfoEmpty.Text = "该软件没有匹配到 winget 包，无法读取官方更新说明与校验值。可使用软件自带更新或前往官网核对。";
            InfoEmpty.Visibility = Visibility.Visible;
            return;
        }

        var version = item.AvailableVersion ?? item.DisplayVersion;
        var key = $"{item.WingetId}@{version}";
        _detailsKey = key;

        if (_detailsCache.TryGetValue(key, out var cached))
        {
            ApplyDetails(item, cached);
            return;
        }

        InfoLoading.Visibility = Visibility.Visible;
        BtnVerify.IsEnabled = false;

        var cts = new CancellationTokenSource();
        _detailsCts = cts;

        try
        {
            var details = await Ctx.Software.GetPackageDetailsAsync(item.WingetId, version, cts.Token);
            _detailsCache[key] = details;

            if (cts.IsCancellationRequested) return;
            if (!ReferenceEquals(SoftwareGrid?.SelectedItem, item)) return;
            ApplyDetails(item, details);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            InfoLoading.Visibility = Visibility.Collapsed;
            InfoError.Text = "读取官方元数据失败：" + ex.Message;
            InfoError.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_detailsCts, cts))
            {
                _detailsCts = null;
                cts.Dispose();
            }
        }
    }

    private void ApplyDetails(SoftwareItem item, WingetPackageDetails? details)
    {
        InfoLoading.Visibility = Visibility.Collapsed;
        InfoError.Visibility = Visibility.Collapsed;
        InfoEmpty.Visibility = Visibility.Collapsed;

        if (details is null)
        {
            InfoError.Text = "winget 清单中没有找到该版本的详细信息（可能已被下架）。";
            InfoError.Visibility = Visibility.Visible;
            DetailTypeDate.Text = "—";
            return;
        }

        _details = details;
        InfoBody.Visibility = Visibility.Visible;

        var typeDate = new[]
        {
            string.IsNullOrWhiteSpace(details.InstallerType) ? "" : details.InstallerType,
            string.IsNullOrWhiteSpace(details.ReleaseDate) ? "" : details.ReleaseDate
        }.Where(s => s.Length > 0).ToArray();
        DetailTypeDate.Text = typeDate.Length > 0 ? string.Join("　·　", typeDate) : "—";

        var meta = new List<string>();
        var requested = item.AvailableVersion ?? item.DisplayVersion;
        if (!string.IsNullOrWhiteSpace(details.Version) && details.Version != requested)
            meta.Add("清单版本 " + details.Version);
        if (!string.IsNullOrWhiteSpace(details.ReleaseDate)) meta.Add("发布日期 " + details.ReleaseDate);
        if (!string.IsNullOrWhiteSpace(details.InstallerType)) meta.Add("安装包 " + details.InstallerType);
        ReleaseMeta.Text = string.Join("　·　", meta);
        ReleaseMeta.Visibility = meta.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (details.HasReleaseNotes)
        {
            ReleaseNotes.Text = details.ReleaseNotes;
            ReleaseNotes.Foreground = (Brush)FindResource("TextBrush");
        }
        else
        {
            ReleaseNotes.Text = details.HasReleaseNotesUrl
                ? "该版本未提供更新说明文本，可打开官方发布说明查看。"
                : "该版本未提供更新说明文本，也未提供发布说明链接。";
            ReleaseNotes.Foreground = (Brush)FindResource("SubTextBrush");
        }

        BtnReleaseNotes.Visibility = details.HasReleaseNotesUrl ? Visibility.Visible : Visibility.Collapsed;

        OfficialSha.Text = details.HasSha256 ? details.Sha256.ToUpperInvariant() : "";
        OfficialSha.Visibility = details.HasSha256 ? Visibility.Visible : Visibility.Collapsed;
        BtnCopySha.Visibility = details.HasSha256 ? Visibility.Visible : Visibility.Collapsed;
        OfficialShaMissing.Visibility = details.HasSha256 ? Visibility.Collapsed : Visibility.Visible;

        BtnInstallerUrl.Visibility = details.HasInstallerUrl ? Visibility.Visible : Visibility.Collapsed;
        BtnInstallerUrl.Content = details.HasInstallerUrl && Uri.TryCreate(details.InstallerUrl, UriKind.Absolute, out var installerUri)
            ? $"打开官方下载地址（{installerUri.Host}）"
            : "打开官方下载地址";
        BtnVerify.IsEnabled = details.HasInstallerUrl;
        BtnVerify.Content = details.HasSha256 ? "下载并校验安装包" : "下载并计算校验值";

        LocalHashes.Visibility = Visibility.Collapsed;
        VerifyBadge.Visibility = Visibility.Collapsed;
        VerifyNote.Text = details.HasSha256
            ? "校验一致说明本地安装包与 winget 官方清单登记的 SHA256 完全相同，可确认未被替换或篡改。"
            : "官方清单未登记 SHA256：这里只能展示本地计算值，无法自动证明与官方一致。";
    }

    // ---------------- 官方链接与复制 ----------------

    private void BtnReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_details?.ReleaseNotesUrl is { Length: > 0 } url) Ui.OpenUrl(url);
    }

    private void BtnInstallerUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_details?.InstallerUrl is { Length: > 0 } url) Ui.OpenUrl(url);
    }

    private void BtnCopySha_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null || !_details.HasSha256) return;
        CopyToClipboard(_details.Sha256);
    }

    private void BtnCopyMd5_Click(object sender, RoutedEventArgs e)
    {
        if (LocalMd5.Text is { Length: > 0 } md5) CopyToClipboard(md5);
    }

    private void BtnCopyLocalSha_Click(object sender, RoutedEventArgs e)
    {
        if (LocalSha.Text is { Length: > 0 } sha) CopyToClipboard(sha);
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            Ui.Toast("校验值已复制到剪贴板");
        }
        catch (Exception ex) { Ui.Warn("复制失败", ex.Message); }
    }

    // ---------------- 下载与校验 ----------------

    private async void BtnVerify_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null) return;
        Ui.Log($"开始校验安装包：{_details.Id} {_details.Version}");

        if (_downloadedPath is { Length: > 0 } existing && File.Exists(existing) && _downloadedKey == _detailsKey)
        {
            await VerifyFileAsync(existing, showDialog: true);
            return;
        }

        var path = await DownloadInstallerAsync();
        if (path is not null) await VerifyFileAsync(path, showDialog: true);
    }

    private async void BtnDownloadThis_Click(object sender, RoutedEventArgs e)
    {
        var path = await DownloadInstallerAsync();
        if (path is null) return;

        var result = await VerifyFileAsync(path, showDialog: false);
        var summary = result?.MatchesOfficial switch
        {
            true => "\n\nSHA256 与 winget 官方清单一致，安装包可信。",
            false => "\n\n注意：SHA256 与官方清单不一致，请勿运行该文件！",
            _ => "\n\n官方清单未提供 SHA256，已展示本地计算值。"
        };

        if (result?.MatchesOfficial == false)
            Ui.Warn("安装包校验失败", $"文件已保存到：\n{path}{summary}");
        else
            Ui.Success("下载完成", $"安装包已保存到：\n{path}{summary}");
    }

    private Task<string?> DownloadInstallerAsync()
    {
        var item = SoftwareGrid.SelectedItem as SoftwareItem;
        if (item is null || string.IsNullOrEmpty(item.WingetId)) return Task.FromResult<string?>(null);

        var version = item.AvailableVersion ?? item.DisplayVersion;
        var target = "";

        var done = ProgressDialog.Run("正在下载安装包", item.DisplayName,
            async (progress, ct) =>
            {
                target = await Ctx.Software.DownloadInstallerAsync(
                    item.WingetId!, item.DisplayName, version, progress, ct);
            });

        if (!done || string.IsNullOrEmpty(target)) return Task.FromResult<string?>(null);

        Ui.Log($"安装包已下载：{target}");
        _downloadedPath = target;
        _downloadedKey = _detailsKey;
        return Task.FromResult<string?>(target);
    }

    private async Task<FileHashResult?> VerifyFileAsync(string path, bool showDialog)
    {
        var details = _details;
        var key = _detailsKey;
        if (details is null) return null;

        VerifyBadge.Visibility = Visibility.Visible;
        VerifyBadge.Background = B("#F1F3F7");
        VerifyBadgeText.Foreground = B("#7B8794");
        VerifyBadgeText.Text = "正在计算校验值…";
        BtnVerify.IsEnabled = false;

        try
        {
            var result = await Task.Run(() => SoftwareService.ComputeFileHashesAsync(path, details.Sha256));

            // 计算期间用户可能已经切换到别的软件：只更新仍对应当前选中项的结果
            if (key != _detailsKey) return result;

            LocalHashes.Visibility = Visibility.Visible;
            LocalMd5.Text = result.Md5;
            LocalSha.Text = result.Sha256;
            LocalFile.Text = "文件：" + path;

            switch (result.MatchesOfficial)
            {
                case true:
                    VerifyBadge.Background = B("#EAF8EF");
                    VerifyBadgeText.Foreground = B("#15803D");
                    VerifyBadgeText.Text = "校验一致 · SHA256 与官方清单相同";
                    break;
                case false:
                    VerifyBadge.Background = B("#FDECEC");
                    VerifyBadgeText.Foreground = B("#C62F2F");
                    VerifyBadgeText.Text = "校验失败 · SHA256 与官方清单不同";
                    break;
                default:
                    VerifyBadge.Background = B("#F1F3F7");
                    VerifyBadgeText.Foreground = B("#7B8794");
                    VerifyBadgeText.Text = "仅本地计算 · 官方清单未提供 SHA256";
                    break;
            }

            if (showDialog)
            {
                if (result.MatchesOfficial == false)
                    Ui.Warn("安装包校验失败",
                        $"本地 SHA256 与官方清单不一致，请勿运行：\n{path}\n\n官方：{details.Sha256}\n本地：{result.Sha256}");
                else if (result.MatchesOfficial == true)
                    Ui.Success("校验通过",
                        $"安装包与 winget 官方清单一致。\n\nSHA256：{result.Sha256}\nMD5：{result.Md5}");
                else
                    Ui.Info("已计算校验值",
                        $"官方清单未提供 SHA256，以下为本地计算结果：\n\nSHA256：{result.Sha256}\nMD5：{result.Md5}");
            }

            return result;
        }
        catch (Exception ex)
        {
            VerifyBadge.Background = B("#FDECEC");
            VerifyBadgeText.Foreground = B("#C62F2F");
            VerifyBadgeText.Text = "计算失败";
            Ui.Warn("计算校验值失败", ex.Message);
            return null;
        }
        finally
        {
            if (key == _detailsKey) BtnVerify.IsEnabled = details.HasInstallerUrl;
        }
    }

    // ---------------- 刷新 / 检查 / 升级 ----------------

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunAsync("正在刷新已安装软件…", async () =>
        {
            await Scanner.ScanSoftwareAsync();
            ApplyFilter();
            await LoadDetailsAsync();
        });

    private async void BtnCheck_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunAsync("正在通过 winget 检查软件更新…", async () =>
        {
            await Scanner.ScanSoftwareUpdatesAsync();
            ApplyFilter();
            await LoadDetailsAsync();
            var ignored = Ctx.State.Software.Count(s => s.Ignored);
            Ui.Info("检查完成", $"发现 {Ctx.State.Summary.SoftwareUpdates} 个可更新的软件。" +
                                 (ignored > 0 ? $"\n另有 {ignored} 个更新已忽略，可在筛选「已忽略」中查看并恢复。" : ""));
        });

    private void BtnIgnoreThis_Click(object sender, RoutedEventArgs e)
    {
        if (SoftwareGrid.SelectedItem is not SoftwareItem item) return;

        if (string.IsNullOrEmpty(item.WingetId) || string.IsNullOrEmpty(item.AvailableVersion))
        {
            Ui.Info("无法忽略", "这条记录没有关联到 winget 更新，因此没有可忽略的版本。");
            return;
        }

        if (item.Ignored)
        {
            IgnoreStore.RestoreSoftware(item.WingetId, item.AvailableVersion);
            item.Ignored = false;
            Ui.Toast($"已恢复软件更新：{item.DisplayName}");
            Scanner.RefreshSummary();
            // 恢复后回到「仅可更新」列表，方便确认它已经重新出现
            ShowOnlyUpdates(true);
        }
        else
        {
            IgnoreStore.IgnoreSoftware(item.WingetId, item.AvailableVersion);
            item.Ignored = true;
            Ui.Toast($"已忽略软件更新：{item.DisplayName}，可在筛选「已忽略」中恢复");
            Scanner.RefreshSummary();
            ApplyFilter();
        }
    }

    private async void BtnUpgradeThis_Click(object sender, RoutedEventArgs e)
    {
        if (SoftwareGrid.SelectedItem is not SoftwareItem item) return;
        await UpgradeOneAsync(item);
    }

    private async Task UpgradeOneAsync(SoftwareItem item)
    {
        if (item.Ignored)
        {
            Ui.Info("此更新已忽略", "先点「恢复此更新」，再执行升级。");
            return;
        }

        if (string.IsNullOrEmpty(item.WingetId))
        {
            Ui.Info("无法升级", $"未能在 winget 源中匹配到《{item.DisplayName}》，请使用软件自带的更新功能。");
            return;
        }

        if (!Ui.Confirm("确认升级", $"将把《{item.DisplayName}》从 {item.DisplayVersion} 升级到 {item.AvailableVersion}。\n\n" +
                                     "winget 会在安装前校验安装包的 SHA256，校验不通过会中止安装。"))
            return;

        var code = 0;
        var output = "";
        var completed = false;
        try
        {
            completed = ProgressDialog.Run("正在升级软件", item.DisplayName,
                async (progress, ct) =>
                {
                    (code, output) = await Ctx.Software.InstallWithProgressAsync(item.WingetId!, progress, ct);
                });
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：不算失败
            await RefreshAsync();
            Ui.Warn("升级已取消", "本次升级已停止，临时安装包已清理。");
            return;
        }

        if (!completed)
        {
            await RefreshAsync();
            Ui.Warn("升级已取消", "本次升级已停止，临时安装包已清理。");
            return;
        }

        if (code != 0)
        {
            await HandleUpgradeFailureAsync(item, code, output);
            return;
        }

        await RefreshAsync();
        Ui.Success("升级完成", $"{item.DisplayName}\n{item.DisplayVersion} → {item.AvailableVersion ?? item.DisplayVersion}");
    }

    /// <summary>
    /// 升级失败后给出可读原因，并在 winget 拒绝升级时提供“直接安装新版本 / 打开下载页”的补救路径。
    /// </summary>
    private async Task HandleUpgradeFailureAsync(SoftwareItem item, int code, string output)
    {
        var info = SoftwareService.ExplainFailure(code, output);
        Ui.Log($"升级失败（{item.DisplayName} / {item.WingetId}）：{info.CodeText} {info.Detail}", "ERROR");

        // 先刷新列表：即使升级没有完成，也要把界面上可能残留的旧状态更新掉
        await RefreshAsync();

        if (info.CanRetryAsInstall && !string.IsNullOrWhiteSpace(item.AvailableVersion))
        {
            var message = $"《{item.DisplayName}》升级到 {item.AvailableVersion} 时被 winget 拒绝。\n\n" +
                          info.Reason + "\n\n" + info.Hint;

            var choice = Ui.Ask("升级未完成", message, "重试安装", "打开下载页");
            switch (choice)
            {
                case DialogChoice.Ok:
                    await ForceInstallAsync(item);
                    return;
                case DialogChoice.Extra:
                    await OpenInstallerAsync(item);
                    return;
                default:
                    return;
            }
        }

        var text = info.Reason + "\n\n" + info.Hint;
        if (info.Title == "升级未完成" && !string.IsNullOrWhiteSpace(info.Detail))
            text = info.Reason + "\n\n" + info.Detail + "\n\n" + info.Hint;

        if (info.CanOpenInstaller)
        {
            if (Ui.Confirm("升级未完成", text + "\n\n是否打开官方安装包下载地址，手动安装？"))
                await OpenInstallerAsync(item);
            return;
        }

        Ui.Warn("升级未完成", text);
    }

    /// <summary>winget upgrade 拒绝升级时，改用 install --force 直接覆盖安装新版本。</summary>
    private async Task ForceInstallAsync(SoftwareItem item)
    {
        var version = item.AvailableVersion ?? "";
        var code = 0;
        var output = "";
        var completed = false;
        try
        {
            completed = ProgressDialog.Run("正在安装新版本", $"{item.DisplayName} {version}",
                async (progress, ct) =>
                {
                    (code, output) = await Ctx.Software.InstallWithProgressAsync(
                        item.WingetId!, progress, ct, forceVersion: version);
                });
        }
        catch (OperationCanceledException)
        {
            await RefreshAsync();
            Ui.Warn("安装已取消", "本次安装已停止，临时安装包已清理。");
            return;
        }

        await RefreshAsync();
        if (!completed) return;

        if (code == 0)
        {
            Ui.Success("升级完成", $"{item.DisplayName}\n{item.DisplayVersion} → {version}");
            return;
        }

        var info = SoftwareService.ExplainFailure(code, output);
        Ui.Log($"强制安装失败（{item.DisplayName} / {item.WingetId} {version}）：{info.CodeText} {info.Detail}", "ERROR");

        var text = info.Reason + "\n\n" + info.Hint;
        if (info.CanOpenInstaller)
        {
            if (Ui.Confirm("安装仍未成功", text + "\n\n是否打开官方安装包下载地址，手动安装？"))
                await OpenInstallerAsync(item);
            return;
        }

        Ui.Warn("安装仍未成功", text);
    }

    /// <summary>打开 winget 清单里的官方安装包地址；没有直链时退回软件主页。</summary>
    private async Task OpenInstallerAsync(SoftwareItem item)
    {
        if (string.IsNullOrEmpty(item.WingetId)) return;

        var version = item.AvailableVersion ?? item.DisplayVersion;
        var key = $"{item.WingetId}@{version}";

        WingetPackageDetails? details = null;
        if (!_detailsCache.TryGetValue(key, out details) || details is null)
        {
            await Ctx.RunAsync("正在获取官方安装包地址…", async () =>
            {
                details = await Ctx.Software.GetPackageDetailsAsync(item.WingetId!, version);
                _detailsCache[key] = details;
            });
        }

        var url = details?.InstallerUrl;
        if (string.IsNullOrWhiteSpace(url)) url = details?.Homepage;
        if (string.IsNullOrWhiteSpace(url))
        {
            Ui.Warn("没有官方下载地址", "winget 清单里没有提供安装包或主页地址，请到软件官网下载最新版。");
            return;
        }

        Ui.OpenUrl(url);
    }

    private Task RefreshAsync()
        => Ctx.RunAsync("正在刷新软件清单…", async () =>
        {
            await Scanner.ScanSoftwareAsync();
            await Scanner.ScanSoftwareUpdatesAsync();
            ApplyFilter();
            await LoadDetailsAsync();
        });

    private async void BtnUpgradeAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = Ctx.State.Software
            .Where(s => s.HasUpdate && !s.Ignored && !string.IsNullOrEmpty(s.WingetId))
            .ToList();
        if (pending.Count == 0)
        {
            var ignored = Ctx.State.Software.Count(s => s.Ignored);
            Ui.Info("没有可升级项", ignored > 0
                ? $"当前没有待升级的软件，另有 {ignored} 个更新已忽略。\n可在筛选「已忽略」中恢复后再升级。"
                : "请先点击『检查更新（winget）』。");
            return;
        }

        if (!Ui.Confirm("全部升级", $"将依次升级 {pending.Count} 个软件，安装过程可能需要几分钟。"))
            return;

        int ok = 0;
        var failures = new List<string>();
        var completed = false;

        try
        {
            completed = ProgressDialog.Run("正在批量升级", $"共 {pending.Count} 个软件",
                async (progress, ct) =>
                {
                    for (var i = 0; i < pending.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var item = pending[i];
                        var index = i;
                        var share = 100.0 / pending.Count;

                        var scoped = new Progress<ProgressInfo>(p =>
                            progress.Report(new ProgressInfo
                            {
                                Percent = p.Percent >= 0 ? index * share + p.Percent * share / 100.0 : -1,
                                Status = $"({index + 1}/{pending.Count}) {p.Status}",
                                Detail = string.IsNullOrEmpty(p.Detail) ? item.DisplayName : $"{item.DisplayName} · {p.Detail}",
                                CanCancel = true
                            }));

                        try
                        {
                            var (code, output) = await Ctx.Software.InstallWithProgressAsync(item.WingetId!, scoped, ct);
                            if (code == 0) ok++;
                            else
                            {
                                var info = SoftwareService.ExplainFailure(code, output);
                                Ui.Log($"批量升级失败（{item.DisplayName} / {item.WingetId}）：{info.CodeText} {info.Detail}", "ERROR");
                                failures.Add($"{item.DisplayName}：{info.Title}");
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            failures.Add($"{item.DisplayName}：{ex.Message}");
                        }
                    }

                    progress.Report(new ProgressInfo { Percent = 100, Status = "升级完成", CanCancel = false });
                });
        }
        catch (OperationCanceledException)
        {
            await RefreshAsync();
            Ui.Warn("升级已取消", $"已完成 {ok} 个，其余未执行；临时安装包已清理。");
            return;
        }

        await RefreshAsync();

        if (!completed)
        {
            Ui.Warn("升级已取消", $"已完成 {ok} 个，其余未执行。");
            return;
        }

        Ui.Success("批量升级完成", $"成功 {ok} 个，未完成 {failures.Count} 个。" +
                                  (failures.Count > 0
                                      ? "\n\n" + string.Join("\n", failures.Take(6)) +
                                        "\n\n可对未完成的软件单独点击『升级』，会给出更具体的原因和处理方式。"
                                      : ""));
    }
}
