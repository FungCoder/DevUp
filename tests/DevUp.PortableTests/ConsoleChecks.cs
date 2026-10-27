using System.IO;
using System.Diagnostics;
using DevUp.Core;
using DevUp.Services;

internal static class ConsoleChecks
{
    public static async Task RunAsync()
    {
        var lines = new List<string>();
        var terminal = new TerminalOutput(100, 10, lines.Add);
        const string raw = "\x1b[2J\x1b[H正在下载\r\n  1.0 MB / 50.0 MB\x1b[2;3H12.5\x1b[2;7H MB / 50.0 MB\r\n已成功验证安装程序哈希";
        foreach (var character in raw) terminal.Append(new[] { character });
        if (!lines.Contains("正在下载") || !lines.Any(line => line.Contains("12.5 MB / 50.0 MB")) ||
            !lines.Contains("已成功验证安装程序哈希"))
            throw new InvalidOperationException("分片转义序列、光标刷新或中文输出解析失败");
        if (PseudoConsoleProcess.QuoteArgument("a\\\"b\\") != "\"a\\\\\\\"b\\\\\"")
            throw new InvalidOperationException("Windows 参数转义失败");
        var output = new List<string>();
        using var command = PseudoConsoleProcess.Start(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            new[] { "/d", "/c", "echo 10 MB / 100 MB" }, line => { lock (output) output.Add(line); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await command.Process.WaitForExitAsync(timeout.Token);
        await command.CompleteOutputAsync();
        if (command.Process.ExitCode != 0 || !output.Any(line => line.Contains("10 MB / 100 MB")))
            throw new InvalidOperationException($"真实伪终端进程输出错误（退出码 {command.Process.ExitCode}）：{string.Join(" | ", output)}");

        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var live = PseudoConsoleProcess.Start(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            new[] { "/d", "/c", "echo 0 MB / 100 MB & timeout /t 2 /nobreak >nul & echo 10 MB / 100 MB" },
            line => { if (line.Contains("0 MB / 100 MB")) first.TrySetResult(); });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (live.Process.HasExited) throw new InvalidOperationException("输出被缓冲到了进程结束后");
        await live.Process.WaitForExitAsync(timeout.Token);
        await live.CompleteOutputAsync();
        using var cancel = PseudoConsoleProcess.Start(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            new[] { "/d", "/c", "timeout /t 30 /nobreak >nul" }, _ => { });
        await Task.Delay(200);
        await cancel.StopAsync();
        await cancel.CompleteOutputAsync();
        if (!cancel.Process.HasExited) throw new InvalidOperationException("取消后子进程没有结束");
        try
        {
            using var missing = PseudoConsoleProcess.Start("devup-missing-command-for-test.exe", Array.Empty<string>(), _ => { });
            throw new InvalidOperationException("不存在的命令没有报错");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { }
        Console.WriteLine("终端分片、光标刷新、中文、参数转义和真实子进程检查通过。");
    }

    public static async Task DownloadProbeAsync(string packageId = "SoftDeluxe.FreeDownloadManager")
    {
        var target = Path.Combine(PortablePaths.EnsureWritableDirectory(PortablePaths.Temp), "download-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            var timer = Stopwatch.StartNew();
            var samples = new List<ProgressInfo>();
            ProgressInfo? completed = null;
            var callback = new ImmediateProgress(display =>
            {
                if (display.Percent == 100) completed = display;
                if (display.Percent < 0 || display.Percent >= 100 || display.Metric?.Contains('/') != true) return;
                if (samples.Count > 0 && samples[^1].Metric == display.Metric) return;
                samples.Add(display);
                Console.WriteLine($"{timer.Elapsed.TotalSeconds:0.0}s {display.Metric} | {display.Detail} | {display.Percent:0.0}%");
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await SoftwareService.RunWingetWithProgressAsync(packageId, new[]
            {
                "download", "--id", packageId, "--exact", "--source", "winget",
                "--download-directory", target, "--skip-dependencies", "--accept-source-agreements",
                "--accept-package-agreements", "--disable-interactivity", "--no-vt"
            }, callback, timeout.Token);
            Console.WriteLine($"最终进度流程探测结束，退出码 {result.ExitCode}，不同下载计数 {samples.Count} 个，完成 {completed?.Metric}（{completed?.Percent:0}%）。");
            if (result.ExitCode != 0 || completed?.Metric?.Contains('/') != true ||
                (samples.Count >= 2 && !samples.Any(sample => sample.Detail?.Contains("/s") == true)) ||
                (packageId == "SoftDeluxe.FreeDownloadManager" && samples.Count < 2))
                throw new InvalidOperationException("最终流程没有持续输出真实速度和百分比：" + result.Output);
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(target)) != PortablePaths.Temp)
                throw new InvalidOperationException("下载测试目录超出预期位置");
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class ImmediateProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) => report(value);
    }

}
