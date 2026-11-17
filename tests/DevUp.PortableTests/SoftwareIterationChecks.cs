using System.IO;
using System.Text.Json;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

internal static class SoftwareIterationChecks
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var status = new CheckStatus();
        Check(status.CountText == "—" && !status.HasResult && !status.IsCurrent,
            "未检查被误报为零或成功");
        var notificationCount = 0;
        status.PropertyChanged += (_, _) => notificationCount++;
        await status.RunAsync(() => Task.FromResult(0));
        Check(status.IsCurrent && status.CountText == "0" && status.LastSucceededAt.HasValue,
            "成功的零结果没有记录成功时间");
        await status.RunAsync(() => Task.FromResult(3));
        var succeededAt = status.LastSucceededAt;
        try { await status.RunAsync(() => throw new IOException("模拟网络故障")); }
        catch (IOException) { }
        Check(status.Phase == CheckPhase.Failed && status.CountText == "3" &&
            status.LastSucceededAt == succeededAt && status.Error.Contains("模拟网络故障") &&
            status.Description.Contains("上次结果"), "检查失败后丢失旧结果或仍标为当前");
        try { await status.RunAsync(() => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { }
        Check(status.Phase == CheckPhase.Cancelled && status.CountText == "3" &&
            status.LastSucceededAt == succeededAt, "检查取消覆盖了成功结果");
        await status.RunAsync(() => Task.FromResult(1));
        status.MarkStale();
        Check(!status.IsCurrent && status.Phase == CheckPhase.Stale && status.CountText == "1",
            "修改后的结果没有标为待复核");
        var failedFirst = new CheckStatus();
        try { await failedFirst.RunAsync(() => throw new IOException("失败")); }
        catch (IOException) { }
        Check(failedFirst.CountText == "—" && !failedFirst.HasResult,
            "首次检查失败误报没有更新");
        var gate = new TaskCompletionSource<int>();
        var running = status.RunAsync(() => gate.Task);
        Check(status.Phase == CheckPhase.Running && !status.IsCurrent,
            "检查过程中仍显示成功");
        gate.SetResult(0);
        await running;
        Check(status.IsCurrent && status.CountText == "0" && notificationCount >= 10,
            "检查恢复成功没有更新状态或通知界面");

        var items = new[]
        {
            Software("Alpha 编辑器", "Vendor.Editor", "Acme"),
            Software("Beta 工具", "Vendor.Tool", "Other"),
            Software("重复包", "vendor.editor", "Acme"),
            Software("忽略的软件", "Vendor.Ignored", "Acme"),
            new SoftwareItem { DisplayName = "未匹配的软件" }
        };
        items[3].Ignored = true;
        Check(SoftwareBatchService.Matches(items[0], " alpha ", "all") &&
            SoftwareBatchService.Matches(items[0], "ACME", "updates") &&
            SoftwareBatchService.Matches(items[0], "vendor.editor", "all"),
            "名称、发布者、包 ID 搜索或空格处理失败");
        Check(!SoftwareBatchService.Matches(items[3], "", "updates") &&
            SoftwareBatchService.Matches(items[3], "", "ignored") &&
            !SoftwareBatchService.Matches(items[1], "alpha", "all"), "搜索破坏筛选边界");
        var batch = SoftwareBatchService.CreateBatch(items);
        Check(batch.Count == 2 && batch.All(item => item.PackageId != "Vendor.Ignored"),
            "批量未按包 ID 去重或包含忽略及未匹配的软件");
        var calls = new List<string>();
        var progress = new Progress<ProgressInfo>();
        await SoftwareBatchService.RunAsync(batch, (id, _, _) =>
        {
            calls.Add(id);
            return Task.FromResult((id == "Vendor.Editor" ? 1 : 0, "模拟安装输出"));
        }, progress, CancellationToken.None);
        Check(calls.SequenceEqual(new[] { "Vendor.Editor", "Vendor.Tool" }) &&
            batch[0].Outcome == SoftwareBatchOutcome.Failed &&
            batch[1].Outcome == SoftwareBatchOutcome.Succeeded && batch[0].Message.Length > 0,
            "批量失败阻止后续项目或缺少错误原因");
        var retry = SoftwareBatchService.Revalidate(
            batch.Where(item => item.Outcome == SoftwareBatchOutcome.Failed), items);
        calls.Clear();
        await SoftwareBatchService.RunAsync(retry, (id, _, _) =>
        {
            calls.Add(id);
            return Task.FromResult((0, ""));
        }, progress, CancellationToken.None);
        Check(calls.SequenceEqual(new[] { "Vendor.Editor" }) &&
            batch.All(item => item.Outcome == SoftwareBatchOutcome.Succeeded),
            "重试重复执行了已成功项目或丢失其他结果");

        var cancelled = SoftwareBatchService.CreateBatch(items);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await SoftwareBatchService.RunAsync(cancelled, (_, _, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult((0, ""));
            }, progress, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        Check(cancelled[0].Outcome == SoftwareBatchOutcome.Cancelled &&
            cancelled[1].Outcome == SoftwareBatchOutcome.Pending &&
            cancelled.All(SoftwareBatchService.IsUnfinished), "取消没有区分当前项和未执行项");
        items[0].AvailableVersion = null;
        items[2].AvailableVersion = null;
        var continued = SoftwareBatchService.Revalidate(cancelled, items);
        Check(continued.Count == 1 && continued[0].PackageId == "Vendor.Tool" &&
            cancelled[0].Outcome == SoftwareBatchOutcome.Skipped,
            "继续前没有跳过已经更新的软件");
        var exceptional = SoftwareBatchService.CreateBatch(items);
        await SoftwareBatchService.RunAsync(exceptional, (_, _, _) =>
            throw new IOException("模拟安装器异常"), progress, CancellationToken.None);
        Check(exceptional.Single().Outcome == SoftwareBatchOutcome.Failed &&
            exceptional.Single().Message == "模拟安装器异常", "安装器异常没有落入逐项结果");

        var state = new ScanState();
        var directory = Path.Combine(Path.GetTempPath(), "DevUp-iteration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var jsonPath = Path.Combine(directory, "report.json");
            var htmlPath = Path.Combine(directory, "report.html");
            ReportService.ExportJson(state, jsonPath);
            ReportService.ExportHtml(state, htmlPath);
            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
            Check(!json.RootElement.GetProperty("checks").GetProperty("softwareUpdates")
                .GetProperty("HasResult").GetBoolean(), "导出报告丢失未检查状态");
            Check(File.ReadAllText(htmlPath).Contains("尚未检查"), "HTML 报告将未检查误报为零");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"软件迭代检查通过：{checks} 项（未安装真实软件）。");
    }

    internal static SoftwareItem Software(string name, string id, string publisher = "")
        => new()
        {
            DisplayName = name, WingetId = id, Publisher = publisher,
            DisplayVersion = "1.0", AvailableVersion = "2.0"
        };
}
