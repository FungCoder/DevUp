using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevUp.Models;
using DevUp.Core;
using DevUp.Services;

internal static class UpdateChecks
{
    private const string SafeRules = """
        [扩展 · Test Cache]
        DetectFile=%LocalAppData%\Vendor\Cache
        Default=False
        FileKey1=%LocalAppData%\Vendor\Cache|*|RECURSE
        ExcludeKey1=FILE|%LocalAppData%\Vendor\Cache|keep.db
        ExcludeKey2=PATH|%LocalAppData%\Vendor\Cache\Protected|*.bin
        """;

    private sealed class MemoryServer(Func<HttpRequestMessage, byte[]> respond) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(respond(request)), RequestMessage = request
            });
        }
    }

    private static CleanupRulePack Pack(string version, string rules = SafeRules)
        => new(1, version, "1.3.0", new string('a', 40), "CC-BY-SA-4.0", rules);

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static object Asset(string name, byte[] data, string? url = null, string? digest = null)
        => new { name, size = data.Length, digest = digest ?? "sha256:" + Hash(data),
            browser_download_url = url ?? "https://github.com/FungCoder/DevUp/releases/download/v" +
                typeof(CleanupService).Assembly.GetName().Version!.ToString(3) + "/" + name };
    private static byte[] Release(string version, params object[] assets)
        => JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v" + version, draft = false,
            prerelease = false, body = "test", assets });

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException) { checks++; return; }
            throw new InvalidOperationException(message);
        }
        using var fixture = new CleanupChecks.Fixture();
        var store = new CleanupRuleStore();
        Check(store.Rules.Count > 0 && store.Current.Rules.Length > 0, "单文件内嵌规则未加载");
        Reject(() => CleanupRuleStore.ValidatePack(Pack("2026.11.18.1") with { MinimumAppVersion = "99.0" }),
            "内嵌规则兼容性检查没有生效");
        Reject(() => CleanupRuleStore.ValidatePack(Pack("2026.11.18.1",
            SafeRules.Replace("Vendor\\Cache", "Vendor\\..\\Documents"))), "危险规则路径未被拒绝");
        Check(CleanupRulePolicy.Adapt(SafeRules).Contains("Default=False"), "适配器没有保持扩展规则默认未勾选");
        Reject(() => CleanupRulePolicy.Validate(SafeRules.Replace("Vendor\\Cache", "Vendor\\Backups\\Cache")),
            "备份子目录被识别为可清理缓存");
        Reject(() => CleanupRulePolicy.Validate(SafeRules.Replace("Vendor\\Cache", "Microsoft\\OneNote\\16.0\\cache")),
            "可能包含未同步笔记的 OneNote 缓存未被保护");
        Reject(() => CleanupRulePolicy.Validate(SafeRules.Replace("Vendor\\Cache", "Packages\\Microsoft.WindowsStore_test\\LocalCache")),
            "允许递归清空整个 UWP LocalCache");

        var old = fixture.FileAt("Vendor/Cache/old.tmp", 12);
        var keep = fixture.FileAt("Vendor/Cache/keep.db");
        var nested = fixture.FileAt("Vendor/Cache/Protected/keep.bin");
        fixture.FileAt("Vendor/Cache/Protected/keep.txt");
        var privateFile = fixture.FileAt("Vendor/Cache/Login Data");
        fixture.FileAt("Vendor/Cache/new.tmp", 10, 0);
        var service = new CleanupService("[Base]\nDetectFile=%TEMP%\\Nothing\nFileKey1=%TEMP%\\Nothing|*",
            new Dictionary<string, string> { ["TEMP"] = fixture.Root, ["LOCALAPPDATA"] = fixture.Root },
            () => fixture.Protected, _ => false, CleanupRulePolicy.Validate(SafeRules));
        var progress = new CleanupChecks.ImmediateProgress<CleanupProgress>();
        var scan = await service.ScanAsync(progress, default);
        Check(scan.FileCount == 1 && scan.Bytes == 12, "扩展规则没有正确应用排除规则、文件保护或保留期：" +
            string.Join("; ", scan.Categories.SelectMany(item => item.Files).Select(item => item.Path)));
        var result = await service.CleanAsync(scan, new HashSet<string> { "扩展 · Test Cache" }, progress, default);
        Check(result.Deleted == 1 && !File.Exists(old) && File.Exists(keep) && File.Exists(nested) && File.Exists(privateFile),
            "扩展规则清理越过安全边界");
        fixture.FileAt("Vendor/Cache/old.tmp");
        var runningRules = CleanupRulePolicy.Validate(SafeRules.Replace("Test Cache", "Google Chrome Caches"));
        var runningBrowser = new CleanupService("[Base]\nDetectFile=%TEMP%\\Nothing\nFileKey1=%TEMP%\\Nothing|*",
            new Dictionary<string, string> { ["TEMP"] = fixture.Root, ["LOCALAPPDATA"] = fixture.Root },
            () => fixture.Protected, _ => true, runningRules);
        var runningScan = await runningBrowser.ScanAsync(progress, default);
        Check(runningScan.FileCount == 0 && runningScan.Categories.Any(category => category.Note.Contains("软件正在运行")),
            "浏览器运行时扩展缓存未跳过，或没有保留退出软件的提示");

        var binary = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "DevUp.exe"));
        var currentVersion = typeof(CleanupService).Assembly.GetName().Version!;
        // 旧 Release 即使仍有独立规则附件，也只能下载 DevUp.exe。
        var metadata = Release(currentVersion.ToString(3), Asset("DevUp.exe", binary), Asset("DevUp.rules.json.gz", [1, 2, 3]));
        using var server = new MemoryServer(request => request.RequestUri!.Host == "api.github.com" ? metadata :
            request.RequestUri.AbsolutePath.EndsWith("DevUp.exe") ? binary :
                throw new InvalidOperationException("客户端不应下载独立规则附件"));
        var updater = new DevUpUpdateService(() => new HttpClient(server, false), new Version(1, 2, 0));
        await updater.CheckAsync();
        Check(updater.HasProgramUpdate && server.Requests == 1, "检查更新下载了独立规则或未发现程序更新");
        var downloaded = await updater.DownloadProgramAsync(Path.Combine(fixture.Root, "download"), null, default);
        Check(Hash(await File.ReadAllBytesAsync(downloaded)) == Hash(binary), "新版程序下载或校验失败");
        var sameVersion = new DevUpUpdateService(() => new HttpClient(server, false),
            new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build, 0));
        await sameVersion.CheckAsync();
        Check(!sameVersion.HasProgramUpdate,
            "三段与四段版本号比较导致重复更新");

        using var badServer = new MemoryServer(request => request.RequestUri!.Host == "api.github.com" ? metadata : [1, 2, 3]);
        var tampered = new DevUpUpdateService(
            () => new HttpClient(badServer, false), new Version(1, 2, 0));
        await tampered.CheckAsync();
        Check(tampered.HasProgramUpdate, "损坏程序附件影响了版本发现");
        try
        {
            await tampered.DownloadProgramAsync(Path.Combine(fixture.Root, "bad-download"), null, default);
            throw new InvalidOperationException("程序下载没有校验大小及哈希");
        }
        catch (InvalidDataException) { checks++; }
        Check(!File.Exists(Path.Combine(fixture.Root, "bad-download", "DevUp.new.exe")), "校验失败留下了可执行更新文件");
        using var hostile = JsonDocument.Parse(Release("1.3.1", Asset("DevUp.exe", binary, "https://example.org/DevUp.exe")));
        Check(DevUpUpdateService.FindAsset(hostile.RootElement, "DevUp.exe", 32 * 1024 * 1024) is null,
            "允许从其他仓库或网站下载自更新程序");
        using var missingHash = JsonDocument.Parse(Release("1.3.1", Asset("DevUp.exe", binary, digest: "")));
        Check(DevUpUpdateService.FindAsset(missingHash.RootElement, "DevUp.exe", 32 * 1024 * 1024) is null,
            "缺失哈希时仍允许自动更新");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await updater.CheckAsync(cancelled.Token); throw new InvalidOperationException("更新检查没有响应取消"); }
        catch (OperationCanceledException) { checks++; }

        var previousAutoCheck = Ctx.Settings.AutoCheckDevUpUpdates;
        var previousCheckTime = Ctx.Settings.LastDevUpUpdateCheckUtc;
        var settingsBytes = File.Exists(AppSettings.FilePath) ? await File.ReadAllBytesAsync(AppSettings.FilePath) : null;
        try
        {
            Ctx.Settings.AutoCheckDevUpUpdates = true;
            Ctx.Settings.LastDevUpUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(-2);
            var before = server.Requests;
            await sameVersion.CheckOnStartupAsync();
            var after = server.Requests;
            await sameVersion.CheckOnStartupAsync();
            Check(after > before && server.Requests == after, "启动检查没有限制每天至多一次");
            using var offlineServer = new MemoryServer(_ => throw new HttpRequestException("offline"));
            var offline = new DevUpUpdateService(() => new HttpClient(offlineServer, false));
            Ctx.Settings.LastDevUpUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(-2);
            await offline.CheckOnStartupAsync();
            await offline.CheckOnStartupAsync();
            Check(offlineServer.Requests == 1, "网络失败后每次启动仍反复检查");
        }
        finally
        {
            Ctx.Settings.AutoCheckDevUpUpdates = previousAutoCheck;
            Ctx.Settings.LastDevUpUpdateCheckUtc = previousCheckTime;
            if (settingsBytes is not null) await File.WriteAllBytesAsync(AppSettings.FilePath, settingsBytes);
            else if (File.Exists(AppSettings.FilePath)) File.Delete(AppSettings.FilePath);
        }

        await CheckInstallerAsync(binary, false, false);
        checks++;
        await CheckInstallerAsync(binary, true, false);
        checks++;
        await CheckInstallerAsync(binary, false, true);
        checks++;
        await CheckInstallerAsync(binary, false, false, true);
        checks++;
        var validationRoot = Path.Combine(fixture.Root, "更新目录 含空格");
        var validationJob = Path.Combine(validationRoot, "DevUpData", "updates", Guid.NewGuid().ToString("N"));
        var validationCandidate = Path.Combine(validationJob, "DevUp.new.exe");
        var validationTarget = Path.Combine(validationRoot, "DevUp.exe");
        Check(UpdateInstaller.ValidatePaths(validationCandidate, validationTarget,
            validationRoot + Path.DirectorySeparatorChar) == validationJob,
            "程序目录末尾分隔符导致合法更新目录被拒绝");
        Check(UpdateInstaller.ValidatePaths(validationCandidate, validationTarget, validationRoot) == validationJob,
            "不带末尾分隔符的程序目录未通过检查");
        Check(UpdateInstaller.ValidatePaths(validationCandidate, validationTarget,
            validationRoot.Replace('\\', '/')) == validationJob, "等价目录分隔符未规范化");
        void RejectPath(string candidate, string target)
        {
            try { UpdateInstaller.ValidatePaths(candidate, target, validationRoot); }
            catch (InvalidOperationException) { checks++; return; }
            throw new InvalidOperationException("更新路径越界未被拒绝：" + candidate + " -> " + target);
        }
        RejectPath(validationCandidate, Path.Combine(validationRoot + "-other", "DevUp.exe"));
        RejectPath(Path.Combine(validationRoot, "DevUp.new.exe"), validationTarget);
        RejectPath(Path.Combine(validationJob, "other.exe"), validationTarget);
        RejectPath(Path.Combine(validationJob, "nested", "DevUp.new.exe"), validationTarget);
        var rejectedJob = UpdateInstaller.CreateJobDirectory();
        try
        {
            var candidate = Path.Combine(rejectedJob, "DevUp.new.exe");
            await File.WriteAllBytesAsync(candidate, binary);
            try
            {
                await UpdateInstaller.StartAsync(candidate, Hash(binary), typeof(CleanupService).Assembly.GetName().Version!);
                throw new InvalidOperationException("helper 未拒绝替换非 DevUp 的测试宿主");
            }
            catch (IOException ex) when (ex.Message.Contains("原文件不是 DevUp")) { checks++; }
        }
        finally { UpdateInstaller.DiscardJob(rejectedJob); }
        await CheckProductionUpdateFlowAsync();
        checks++;
        Console.WriteLine($"规则与程序更新回归通过：{checks} 项；真实准备、退出、替换和重启仅操作隔离测试宿主。");
    }

    private static async Task CheckProductionUpdateFlowAsync()
    {
        using var fixture = new CleanupChecks.Fixture();
        var root = Path.Combine(fixture.Root, "自动更新 含空格");
        var probeFiles = Path.Combine(AppContext.BaseDirectory, "UpdateProbe");
        foreach (var source in Directory.EnumerateFiles(probeFiles, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(root, Path.GetRelativePath(probeFiles, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        var target = Path.Combine(root, "DevUp.UpdateProbe.exe");
        var expected = Hash(await File.ReadAllBytesAsync(target));
        var start = new ProcessStartInfo(target)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = fixture.Root, RedirectStandardError = true
        };
        start.ArgumentList.Add("--prepare");
        using var parent = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var errors = parent.StandardError.ReadToEndAsync(timeout.Token);
        try { await parent.WaitForExitAsync(timeout.Token); }
        catch { if (!parent.HasExited) parent.Kill(true); throw; }
        if (parent.ExitCode != 0)
            throw new InvalidOperationException("真实更新准备流程失败：" + await errors);

        var resultFile = Path.Combine(root, "DevUpData", "updates", "update-result.json");
        var restartedFile = Path.Combine(root, "restarted.json");
        while (!File.Exists(resultFile) || !File.Exists(restartedFile))
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (File.Exists(resultFile))
            {
                using var failed = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile, timeout.Token));
                if (!failed.RootElement.GetProperty("Success").GetBoolean())
                    throw new InvalidOperationException("真实替换流程失败：" + failed.RootElement.GetProperty("Message").GetString());
            }
            await Task.Delay(50, timeout.Token);
        }
        using var restarted = JsonDocument.Parse(await File.ReadAllTextAsync(restartedFile, timeout.Token));
        try
        {
            using var child = Process.GetProcessById(restarted.RootElement.GetProperty("Id").GetInt32());
            if (child.StartTime.ToUniversalTime().Ticks == restarted.RootElement.GetProperty("StartedUtcTicks").GetInt64())
                await child.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { /* 重启的测试宿主已经退出。 */ }
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile, timeout.Token));
        var backup = Path.Combine(root, "DevUpData", "updates", "DevUp.previous.exe");
        if (!result.RootElement.GetProperty("Success").GetBoolean() || !File.Exists(backup) ||
            Hash(await File.ReadAllBytesAsync(backup, timeout.Token)) != expected ||
            Hash(await File.ReadAllBytesAsync(target, timeout.Token)) != expected)
            throw new InvalidOperationException("更新重启后没有保留正确的程序与备份。");
    }

    private static async Task CheckInstallerAsync(byte[] binary, bool tampered, bool invalidTarget, bool locked = false)
    {
        using var fixture = new CleanupChecks.Fixture();
        var target = fixture.FileAt("DevUp.exe");
        await File.WriteAllBytesAsync(target, binary);
        var job = Path.Combine(fixture.Root, "DevUpData", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var candidate = Path.Combine(job, "DevUp.new.exe");
        await File.WriteAllBytesAsync(candidate, tampered ? [1, 2, 3] : binary);
        var script = Path.Combine(job, "apply-update.ps1");
        using (var stream = typeof(CleanupService).Assembly.GetManifestResourceStream("apply-update.ps1")!)
        using (var reader = new StreamReader(stream))
            await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(), new UTF8Encoding(true));
        await File.WriteAllTextAsync(Path.Combine(job, "update.json"), JsonSerializer.Serialize(new
        {
            ParentId = int.MaxValue, ParentStartedUtcTicks = DateTime.UtcNow.Ticks,
            TargetName = invalidTarget ? "..\\outside.exe" : "DevUp.exe",
            Sha256 = Hash(binary), Version = typeof(CleanupService).Assembly.GetName().Version!.ToString()
        }), new UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-NoRestart" })
            start.ArgumentList.Add(argument);
        using var held = locked ? new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        if (process.ExitCode != (tampered || invalidTarget || locked ? 1 : 0))
            throw new InvalidOperationException("更新辅助脚本结果不符：" + await errors + await output);
        if (Hash(await File.ReadAllBytesAsync(target)) != Hash(binary))
            throw new InvalidOperationException("更新失败后原程序发生变化");
        var backup = Path.Combine(fixture.Root, "DevUpData", "updates", "DevUp.previous.exe");
        if (!tampered && !invalidTarget && !locked && (!File.Exists(backup) || Hash(await File.ReadAllBytesAsync(backup)) != Hash(binary)))
            throw new InvalidOperationException("替换完成但没有保留旧版程序");
    }
}
