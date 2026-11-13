using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

internal static class CleanupChecks
{
    internal sealed class ImmediateProgress<T>(Action<T>? report = null) : IProgress<T>
    {
        public void Report(T value) => report?.Invoke(value);
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DevUp-CleanupTests-" + Guid.NewGuid().ToString("N"));
        public List<string> Protected { get; } = new();
        public bool Running { get; set; }
        private readonly List<string> _junctions = new();

        public Fixture() => Directory.CreateDirectory(Root);

        public string FileAt(string relative, int bytes = 16, int days = 8)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
            return path;
        }

        public CleanupService Service(string rules) => new(rules,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TEMP"] = Root },
            () => Protected, _ => Running);

        public void Junction(string relative, string target)
        {
            var path = Path.Combine(Root, relative);
            var start = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{path}\" \"{target}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("无法创建测试目录链接：" + output);
            _junctions.Add(path);
        }

        public void Dispose()
        {
            // 只清理本测试明确创建的临时根目录与链接。
            foreach (var junction in _junctions)
                if (Directory.Exists(junction)) Directory.Delete(junction);
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }

    private const string BasicRules = """
        [用户临时文件]
        Section=系统垃圾
        DetectFile=%TEMP%\Temp
        Default=True
        FileKey1=%TEMP%\Temp|*|RECURSE

        [Google Chrome 缓存]
        Section=浏览器缓存
        DetectFile=%TEMP%\Profiles
        Default=True
        FileKey1=%TEMP%\Profiles\*\Cache|*|RECURSE
        """;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr attributes);

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        var progress = new ImmediateProgress<CleanupProgress>();
        var temporaryOnly = new HashSet<string> { "用户临时文件" };
        using (var fixture = new Fixture())
        {
            var old = fixture.FileAt("Temp/old.tmp", 12);
            var fresh = fixture.FileAt("Temp/new.tmp", 15, 0);
            var backup = fixture.FileAt("Temp/Backup/keep.zip");
            var readOnly = fixture.FileAt("Temp/readonly.tmp");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            fixture.Protected.Add(Path.GetDirectoryName(backup)!);
            var cache = fixture.FileAt("Profiles/Profile 1/Cache/data", 50, 0);
            var history = fixture.FileAt("Profiles/Profile 1/History");
            var service = fixture.Service(BasicRules);
            var scan = await service.ScanAsync(progress, default);
            Check(scan.FileCount == 2 && scan.Bytes == 62, "扫描没有正确处理年龄、只读、保护目录或无扩展名缓存");
            Check(File.Exists(old) && File.Exists(cache), "扫描阶段删除了文件");
            var createdLater = fixture.FileAt("Temp/created-after-scan.tmp");
            var result = await service.CleanAsync(scan, temporaryOnly, progress, default);
            Check(result.Deleted == 1 && result.Bytes == 12 && result.Skipped == 0, "成功删除统计错误");
            Check(!File.Exists(old) && File.Exists(fresh) && File.Exists(backup) &&
                  File.Exists(cache) && File.Exists(history) && File.Exists(createdLater),
                "清理越过勾选范围、误删保留内容或扫描后新增文件");
            try
            {
                await service.CleanAsync(scan, temporaryOnly, progress, default);
                Check(false, "重复使用扫描结果没有被拒绝");
            }
            catch (InvalidOperationException) { Check(true, "失效清单检查"); }
        }

        using (var fixture = new Fixture())
        {
            var changed = fixture.FileAt("Temp/changed.tmp");
            var locked = fixture.FileAt("Temp/locked.tmp");
            var protectedLater = fixture.FileAt("Temp/ProtectedLater/keep.zip");
            var removed = fixture.FileAt("Temp/gone.tmp");
            var service = fixture.Service(BasicRules);
            var scan = await service.ScanAsync(progress, default);
            File.AppendAllText(changed, "changed");
            File.Delete(removed);
            fixture.Protected.Add(Path.GetDirectoryName(protectedLater)!);
            using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var result = await service.CleanAsync(scan, temporaryOnly, progress, default);
            Check(result.Deleted == 0 && result.Bytes == 0 && result.Skipped == 4, "变化、占用、消失或新保护文件没有被跳过");
            Check(File.Exists(changed) && File.Exists(locked) && File.Exists(protectedLater), "删除检查破坏了文件");
        }

        using (var fixture = new Fixture())
        {
            var first = fixture.FileAt("Temp/a.tmp");
            fixture.FileAt("Temp/b.tmp");
            fixture.FileAt("Temp/c.tmp");
            var service = fixture.Service(BasicRules);
            var scan = await service.ScanAsync(progress, default);
            using var cancelled = new CancellationTokenSource();
            var result = await service.CleanAsync(scan, temporaryOnly,
                new ImmediateProgress<CleanupProgress>(_ => cancelled.Cancel()), cancelled.Token);
            Check(result.Cancelled && result.Deleted == 1 && result.Bytes == 16,
                "中途取消没有保留已删除文件的准确统计");
            Check(Directory.GetFiles(Path.GetDirectoryName(first)!).Length == 2, "取消后仍继续删除文件");
            var newScan = await service.ScanAsync(progress, default);
            using var preCancelled = new CancellationTokenSource();
            preCancelled.Cancel();
            result = await service.CleanAsync(newScan, temporaryOnly, progress, preCancelled.Token);
            Check(result.Cancelled && result.Deleted == 0, "清理前取消仍删除了文件");
        }

        using (var fixture = new Fixture())
        {
            fixture.FileAt("Temp/test.tmp");
            var cache = fixture.FileAt("Profiles/Default/Cache/data");
            fixture.Running = true;
            var service = fixture.Service(BasicRules);
            var scan = await service.ScanAsync(progress, default);
            Check(scan.Categories.Single(category => category.Name == "Google Chrome 缓存").Files.Count == 0,
                "运行中的浏览器缓存未跳过");
            fixture.Running = false;
            var complete = await service.ScanAsync(progress, default);
            fixture.Running = true;
            var outcome = await service.CleanAsync(complete, new HashSet<string> { "Google Chrome 缓存" }, progress, default);
            Check(outcome.Deleted == 0 && File.Exists(cache), "扫描后启动的浏览器缓存被删除");
            try
            {
                await service.CleanAsync(scan, temporaryOnly, progress, default);
                Check(false, "旧扫描结果仍可使用");
            }
            catch (InvalidOperationException) { Check(true, "旧扫描结果检查"); }
            using var cancellation = new CancellationTokenSource();
            try
            {
                await service.ScanAsync(new ImmediateProgress<CleanupProgress>(_ => cancellation.Cancel()), cancellation.Token);
                Check(false, "扫描取消没有传播");
            }
            catch (OperationCanceledException) { Check(true, "扫描取消检查"); }
        }

        using (var fixture = new Fixture())
        {
            var real = fixture.FileAt("Outside/keep.tmp");
            Directory.CreateDirectory(Path.Combine(fixture.Root, "Temp"));
            fixture.Junction("Temp/Link", Path.GetDirectoryName(real)!);
            var service = fixture.Service(BasicRules);
            var scan = await service.ScanAsync(progress, default);
            Check(scan.FileCount == 0 && File.Exists(real), "扫描跟随了目录链接");
            var original = fixture.FileAt("Profiles/Default/Cache/data");
            scan = await service.ScanAsync(progress, default);
            var parent = Path.GetDirectoryName(original)!;
            Directory.Move(parent, parent + "-old");
            fixture.Junction("Profiles/Default/Cache", Path.GetDirectoryName(real)!);
            var result = await service.CleanAsync(scan, new HashSet<string> { "Google Chrome 缓存" }, progress, default);
            Check(result.Deleted == 0 && result.Skipped == 1 && File.Exists(real), "扫描后替换目录链接没有被阻止");
        }

        using (var fixture = new Fixture())
        {
            var target = fixture.FileAt("Outside/keep.tmp");
            var link = Path.Combine(fixture.Root, "Temp", "hardlink.tmp");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            if (!CreateHardLinkW(link, target, IntPtr.Zero)) throw new IOException("创建测试硬链接失败");
            var service = fixture.Service(BasicRules);
            var scan = await service.ScanAsync(progress, default);
            var result = await service.CleanAsync(scan, temporaryOnly, progress, default);
            Check(result.Deleted == 0 && result.Skipped == 1 && File.Exists(target), "硬链接没有被安全跳过");
            Check(!CleanupService.IsBelow("C:\\TempOther\\a", "C:\\Temp"), "路径边界检查接受了相邻目录");
            Check(CleanupService.IsBelow("C:\\Temp\\a", "C:\\"), "盘符根目录的路径边界错误");
        }

        using (var fixture = new Fixture())
        {
            fixture.FileAt("Temp/test.tmp");
            var broadRules = "[用户临时文件]\nDetectFile=%TEMP%\nFileKey1=%TEMP%|*|RECURSE";
            var service = new CleanupService(broadRules,
                new Dictionary<string, string> { ["TEMP"] = fixture.Root, ["USERPROFILE"] = fixture.Root },
                () => Array.Empty<string>(), _ => false);
            var scan = await service.ScanAsync(progress, default);
            Check(scan.FileCount == 0, "TEMP 指向用户根目录时没有阻止扫描");
            var builtIn = new CleanupService();
            Check(builtIn.Categories.Count == 17 && builtIn.Categories.Count(category => !category.DefaultSelected) == 3,
                "内嵌规则数量或诊断资料默认选择错误");
            Check(Path.IsPathFullyQualified(Native.GetDownloadsFolder()), "Windows 下载目录保护没有取得绝对路径");
        }
        Console.WriteLine($"垃圾清理回归通过：{checks} 项，所有删除仅发生在测试临时目录。");
    }
}
