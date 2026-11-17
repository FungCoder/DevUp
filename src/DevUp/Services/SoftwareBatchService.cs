using DevUp.Core;
using DevUp.Models;

namespace DevUp.Services;

/// <summary>共用的软件搜索、批量去重和逐项执行逻辑。</summary>
internal static class SoftwareBatchService
{
    public static bool Matches(SoftwareItem item, string query, string filter)
    {
        var included = filter switch
        {
            "updates" => item.HasUpdate && !item.Ignored,
            "ignored" => item.Ignored,
            _ => !item.Ignored
        };
        query = query.Trim();
        return included && (query.Length == 0 ||
            item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (item.WingetId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    public static bool CanUpgrade(SoftwareItem item) => item.HasUpdate && !item.Ignored;

    public static bool IsUnfinished(SoftwareBatchItem item)
        => item.Outcome is SoftwareBatchOutcome.Pending or SoftwareBatchOutcome.Cancelled;

    public static List<SoftwareBatchItem> Revalidate(
        IEnumerable<SoftwareBatchItem> candidates, IEnumerable<SoftwareItem> software)
    {
        var eligibleIds = software.Where(CanUpgrade).Select(item => item.WingetId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new List<SoftwareBatchItem>();
        foreach (var item in candidates)
        {
            if (eligibleIds.Contains(item.PackageId)) pending.Add(item);
            else item.SetOutcome(SoftwareBatchOutcome.Skipped,
                "当前清单中没有可升级项，可能已更新、卸载或忽略；本次未执行");
        }
        return pending;
    }

    public static List<SoftwareBatchItem> CreateBatch(IEnumerable<SoftwareItem> items)
        => items.Where(CanUpgrade).DistinctBy(item => item.WingetId,
                StringComparer.OrdinalIgnoreCase)
            .Select(item => new SoftwareBatchItem
            {
                PackageId = item.WingetId!, DisplayName = item.DisplayName,
                InstalledVersion = item.DisplayVersion,
                AvailableVersion = item.AvailableVersion!
            }).ToList();

    /// <summary>顺序执行给定项目；单项失败继续，取消则保留当前及后续状态。</summary>
    public static async Task RunAsync(
        IReadOnlyList<SoftwareBatchItem> items,
        Func<string, IProgress<ProgressInfo>, CancellationToken,
            Task<(int ExitCode, string Output)>> install,
        IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        for (var index = 0; index < items.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[index];
            item.SetOutcome(SoftwareBatchOutcome.Running, "正在通过 winget 升级");
            var currentIndex = index;
            var scoped = new Progress<ProgressInfo>(update => progress.Report(new ProgressInfo
            {
                Percent = update.Percent >= 0
                    ? (currentIndex + update.Percent / 100.0) * 100 / items.Count : -1,
                Status = $"({currentIndex + 1}/{items.Count}) {item.DisplayName} · {update.Status}",
                Detail = update.Detail, DetailHint = update.DetailHint,
                Metric = update.Metric, CanCancel = true
            }));
            try
            {
                var (code, output) = await install(item.PackageId, scoped, ct);
                if (code == 0)
                    item.SetOutcome(SoftwareBatchOutcome.Succeeded, "winget 已报告升级成功");
                else
                {
                    var failure = SoftwareService.ExplainFailure(code, output);
                    item.SetOutcome(SoftwareBatchOutcome.Failed,
                        $"{failure.Title} · {failure.Detail} ({failure.CodeText})");
                }
            }
            catch (OperationCanceledException)
            {
                item.SetOutcome(SoftwareBatchOutcome.Cancelled,
                    "操作已取消；安装器可能已修改软件，继续前会重新检查");
                throw;
            }
            catch (UserCancelledException)
            {
                item.SetOutcome(SoftwareBatchOutcome.Cancelled, "已取消授权，继续前会重新检查");
                throw;
            }
            catch (Exception exception)
            {
                item.SetOutcome(SoftwareBatchOutcome.Failed, exception.Message);
            }
        }
    }
}
