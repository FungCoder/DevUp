using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

namespace DevUp.Views;

public partial class SoftwareView
{
    private List<SoftwareBatchItem> _batchResults = [];
    private bool _filterRefreshQueued;

    private void QueueFilterRefresh()
    {
        if (_filterRefreshQueued) return;
        _filterRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _filterRefreshQueued = false;
            ApplyFilter();
        }));
    }

    private void Search_Changed(object sender, TextChangedEventArgs args) => ApplyFilter();
    private void ClearSearch_Click(object sender, RoutedEventArgs args) => SearchBox.Clear();
    private void SelectionCheck_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as CheckBox)?.DataContext is SoftwareItem item && !SoftwareBatchService.CanUpgrade(item))
            item.IsSelected = false;
        UpdateBatchControls();
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs args)
    {
        foreach (var item in CollectionViewSource.GetDefaultView(Ctx.State.Software).Cast<SoftwareItem>())
            if (SoftwareBatchService.CanUpgrade(item)) item.IsSelected = true;
        UpdateBatchControls();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs args)
    {
        foreach (var item in Ctx.State.Software) item.IsSelected = false;
        UpdateBatchControls();
    }

    private void UpdateBatchControls()
    {
        if (UpgradeSelectedButton is null || SelectionSummary is null || BatchSummary is null) return;
        var selected = Ctx.State.Software.Count(item => item.IsSelected && SoftwareBatchService.CanUpgrade(item));
        var visible = CollectionViewSource.GetDefaultView(Ctx.State.Software).Cast<SoftwareItem>()
            .Count(item => item.IsSelected && SoftwareBatchService.CanUpgrade(item));
        SelectionSummary.Text = $"已勾选 {selected} 项，其中 {selected - visible} 项在当前筛选之外";
        UpgradeSelectedButton.Content = selected > 0 ? $"升级选中 {selected} 项" : "升级选中项";
        UpgradeSelectedButton.IsEnabled = !Ctx.State.IsBusy && selected > 0;
        RetryFailedButton.IsEnabled = !Ctx.State.IsBusy && _batchResults.Any(item => item.Outcome == SoftwareBatchOutcome.Failed);
        ContinueBatchButton.IsEnabled = !Ctx.State.IsBusy && _batchResults.Any(SoftwareBatchService.IsUnfinished);
        BatchSummary.Text = $"共 {_batchResults.Count} 项 · 成功 {Count(SoftwareBatchOutcome.Succeeded)} · " +
            $"失败 {Count(SoftwareBatchOutcome.Failed)} · 未执行 {Count(SoftwareBatchOutcome.Pending)} · " +
            $"取消待复核 {Count(SoftwareBatchOutcome.Cancelled)} · 跳过 {Count(SoftwareBatchOutcome.Skipped)}";
        if (_batchResults.Count > 0 && !Ctx.State.SoftwareUpdateCheck.IsCurrent && !Ctx.State.IsBusy)
            BatchSummary.Text += "。更新结果尚未复核；重试或继续前会重新检查。";
        BatchResultsPanel.Header = $"批量升级结果 · 成功 {Count(SoftwareBatchOutcome.Succeeded)} · " +
            $"失败 {Count(SoftwareBatchOutcome.Failed)} · 待继续 {_batchResults.Count(SoftwareBatchService.IsUnfinished)}";

        int Count(SoftwareBatchOutcome outcome) => _batchResults.Count(item => item.Outcome == outcome);
    }

    private async Task<bool> EnsureCurrentCheckAsync(bool force = false)
    {
        if (!force && Ctx.State.SoftwareUpdateCheck.IsCurrent && Ctx.State.SoftwareInventoryCheck.IsCurrent)
            return true;
        return await Ctx.RunScanAsync("正在复核软件清单和可用更新…", async (_, ct) =>
        {
            await Scanner.ScanSoftwareAsync(ct);
            await Scanner.ScanSoftwareUpdatesAsync(ct);
        });
    }

    private async void UpgradeSelected_Click(object sender, RoutedEventArgs args)
        => await StartBatchAsync(Ctx.State.Software.Where(item => item.IsSelected), false);

    private async Task StartBatchAsync(IEnumerable<SoftwareItem> source, bool all)
    {
        if (Ctx.State.IsBusy) return;
        var ids = source.Where(SoftwareBatchService.CanUpgrade).Select(item => item.WingetId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!await EnsureCurrentCheckAsync()) return;
        var pending = SoftwareBatchService.CreateBatch(Ctx.State.Software
            .Where(item => all || ids.Contains(item.WingetId ?? "")));
        if (pending.Count == 0)
        {
            Ui.Info("没有可升级项", "检查后没有符合条件的更新。已忽略项目不会参加升级。");
            return;
        }
        if (!Ui.Confirm(all ? "全部升级" : "升级选中项",
            $"将依次升级 {pending.Count} 个软件。" +
            (all ? "包含搜索和筛选之外的所有可升级软件，已忽略项目除外。" : "包含已勾选但被筛选隐藏的项目。") +
            "\n\n逐项结果将保留在页面底部；开始新批次会替换上一批结果。是否继续？")) return;
        ShowBatchResults(pending);
        await ExecuteBatchAsync(pending);
    }

    internal void ShowBatchResults(List<SoftwareBatchItem> results)
    {
        _batchResults = results;
        BatchResultsGrid.ItemsSource = _batchResults;
        BatchResultsPanel.Visibility = Visibility.Visible;
        BatchResultsPanel.IsExpanded = ActualHeight >= 760;
        UpdateBatchControls();
    }

    private async void RetryFailed_Click(object sender, RoutedEventArgs args)
        => await ResumeBatchAsync(item => item.Outcome == SoftwareBatchOutcome.Failed);

    private async void ContinueBatch_Click(object sender, RoutedEventArgs args)
        => await ResumeBatchAsync(SoftwareBatchService.IsUnfinished);

    private async Task ResumeBatchAsync(Func<SoftwareBatchItem, bool> predicate)
    {
        if (Ctx.State.IsBusy) return;
        var candidates = _batchResults.Where(predicate).ToList();
        if (candidates.Count == 0 || !await EnsureCurrentCheckAsync(force: true)) return;
        var pending = SoftwareBatchService.Revalidate(candidates, Ctx.State.Software);
        if (pending.Count > 0 && Ui.Confirm("继续批量升级",
            $"已重新检查，将执行 {pending.Count} 个仍可升级的项目。已成功项目不会重复安装。是否继续？"))
            await ExecuteBatchAsync(pending);
        UpdateBatchControls();
    }

    private async Task ExecuteBatchAsync(List<SoftwareBatchItem> pending)
    {
        foreach (var item in pending) item.SetOutcome(SoftwareBatchOutcome.Pending, "等待执行");
        Ctx.State.IsBusy = true;
        Ctx.State.StatusText = "正在批量升级软件…";
        Ctx.State.SoftwareUpdateCheck.MarkStale();
        try
        {
            var completed = ProgressDialog.Run("正在批量升级", $"共 {pending.Count} 个软件",
                (progress, ct) => SoftwareBatchService.RunAsync(pending,
                    (id, scoped, token) => Ctx.Software.InstallWithProgressAsync(id, scoped, token),
                    progress, ct));
            Ctx.State.StatusText = completed ? "批量升级已结束，结果见软件页" : "批量升级已取消，结果见软件页";
        }
        catch (Exception exception)
        {
            foreach (var item in pending.Where(item => item.Outcome == SoftwareBatchOutcome.Running))
                item.SetOutcome(SoftwareBatchOutcome.Failed, exception.Message);
            Ui.Error("批量升级中断", exception.Message + "\n逐项结果已保留，可在页面中重试或继续。");
        }
        finally
        {
            foreach (var item in pending)
                Ui.Log($"批量升级 {item.PackageId}：{item.OutcomeText} · {item.Message}",
                    item.Outcome == SoftwareBatchOutcome.Failed ? "ERROR" : "INFO");
            Ctx.State.IsBusy = false;
            UpdateBatchControls();
        }
        await RefreshAsync();
        UpdateBatchControls();
        Ui.Toast("批量升级结果：" + BatchSummary.Text);
    }

    private void CopyBatchResults_Click(object sender, RoutedEventArgs args)
    {
        var text = new StringBuilder(BatchSummary.Text).AppendLine();
        foreach (var item in _batchResults)
            text.AppendLine($"{item.DisplayName} [{item.PackageId}] · {item.OutcomeText}\n" +
                $"检查时版本：{item.InstalledVersion} → {item.AvailableVersion}\n{item.Message}");
        try { Clipboard.SetText(text.ToString()); Ui.Toast("已复制全部批量升级结果"); }
        catch (Exception exception) { Ui.Error("复制失败", exception.Message); }
    }
}
