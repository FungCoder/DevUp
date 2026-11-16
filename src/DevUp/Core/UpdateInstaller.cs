using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DevUp.Core;

/// <summary>使用短时 PowerShell helper 在主进程退出后替换单文件程序。</summary>
internal static class UpdateInstaller
{
    internal static string CreateJobDirectory()
        => PortablePaths.EnsureWritableDirectory(Path.Combine(PortablePaths.StorageRoot, "updates", Guid.NewGuid().ToString("N")));

    internal static void DiscardJob(string directory)
    {
        var root = Path.Combine(PortablePaths.StorageRoot, "updates");
        var path = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(path), "N", out _)) return;
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var name in new[] { "DevUp.new.exe", "update.json", "apply-update.ps1", "ready" })
                File.Delete(Path.Combine(path, name));
            Directory.Delete(path);
        }
        catch (Exception ex) { Ui.Log("更新暂存目录清理未完成：" + ex.Message, "WARN"); }
    }

    internal static (bool Success, string Message)? TakeResult()
    {
        var path = Path.Combine(PortablePaths.StorageRoot, "updates", "update-result.json");
        try
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 8192) throw new IOException("更新结果过大。");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var result = (document.RootElement.GetProperty("Success").GetBoolean(),
                document.RootElement.GetProperty("Message").GetString() ?? "更新结束");
            File.Delete(path);
            return result;
        }
        catch (Exception ex)
        {
            Ui.Log("读取更新结果失败：" + ex.Message, "WARN");
            return null;
        }
    }

    internal static async Task StartAsync(string downloadedFile, string digest, Version version)
    {
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前 DevUp.exe。");
        var job = Path.GetDirectoryName(Path.GetFullPath(downloadedFile))!;
        if (!Services.CleanupService.IsBelow(job, Path.Combine(PortablePaths.StorageRoot, "updates")) ||
            Path.GetFileName(downloadedFile) != "DevUp.new.exe" ||
            !Path.GetDirectoryName(target)!.Equals(PortablePaths.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新文件目录不匹配，已停止替换。");
        using var process = Process.GetCurrentProcess();
        var manifest = new
        {
            ParentId = process.Id, ParentStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            TargetName = Path.GetFileName(target), Sha256 = digest, Version = version.ToString()
        };
        File.WriteAllText(Path.Combine(job, "update.json"), JsonSerializer.Serialize(manifest), new UTF8Encoding(true));
        using var resource = typeof(UpdateInstaller).Assembly.GetManifestResourceStream("apply-update.ps1")
            ?? throw new InvalidOperationException("更新辅助脚本缺失。");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var script = Path.Combine(job, "apply-update.ps1");
        File.WriteAllText(script, reader.ReadToEnd(), new UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = PortablePaths.Root,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动更新辅助进程，当前程序继续运行。");
        var ready = Path.Combine(job, "ready");
        for (var attempt = 0; attempt < 100 && !helper.HasExited; attempt++)
        {
            if (File.Exists(ready)) return;
            await Task.Delay(50);
        }
        if (!helper.HasExited)
        {
            // 主进程仍在运行，helper 尚不能替换程序；只停止本次启动的辅助进程。
            helper.Kill(entireProcessTree: true);
            await helper.WaitForExitAsync();
        }
        var result = TakeResult();
        throw new IOException(result?.Message ?? "更新辅助进程未就绪，当前程序继续运行，请稍后重试。");
    }
}
