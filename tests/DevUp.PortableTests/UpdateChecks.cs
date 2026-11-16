using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

    private static byte[] Encode(CleanupRulePack pack)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(gzip, pack);
        return memory.ToArray();
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static object Asset(string name, byte[] data, string? url = null, string? digest = null)
        => new { name, size = data.Length, digest = digest ?? "sha256:" + Hash(data),
            browser_download_url = url ?? "https://github.com/FungCoder/DevUp/releases/download/v1.3.0/" + name };
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
        var directory = Path.Combine(fixture.Root, "rules");
        var builtIn = Pack("2026.11.16.1");
        var store = new CleanupRuleStore(directory, builtIn);
        var first = Encode(Pack("2026.11.17.1"));
        store.Install(first, Hash(first));
        Check(store.Current.Version == "2026.11.17.1" && File.Exists(Path.Combine(directory, "current.gz")), "规则安装没有持久化");
        Reject(() => store.Install(first, new string('0', 64)), "错误规则哈希未被拒绝");
        var unsafePack = Encode(Pack("2026.11.18.1", SafeRules.Replace("Vendor\\Cache", "Vendor\\..\\Documents")));
        Reject(() => store.Install(unsafePack, Hash(unsafePack)), "危险规则路径未被拒绝");
        var future = Encode(Pack("2026.11.18.1") with { MinimumAppVersion = "99.0" });
        Reject(() => store.Install(future, Hash(future)), "规则包引擎兼容检查没有生效");
        Check(store.Current.Version == "2026.11.17.1", "无效规则更新改变了当前版本");
        var second = Encode(Pack("2026.11.18.1"));
        store.Install(second, Hash(second));
        File.WriteAllText(Path.Combine(directory, "current.gz"), "corrupt");
        var fallback = new CleanupRuleStore(directory, builtIn);
        Check(fallback.Current.Version == "2026.11.17.1", "当前规则损坏没有回退上一份规则");
        var repaired = Encode(Pack("2026.11.19.1"));
        fallback.Install(repaired, Hash(repaired));
        File.WriteAllText(Path.Combine(directory, "current.gz"), "corrupt");
        Check(new CleanupRuleStore(directory, builtIn).Current.Version == "2026.11.17.1",
            "回退后安装更新覆盖了上一份可用规则");
        File.WriteAllText(Path.Combine(directory, "previous.gz"), "corrupt");
        Check(new CleanupRuleStore(directory, builtIn).Current.Version == builtIn.Version, "本地两份规则损坏没有回退内嵌规则");
        var bomb = Encode(Pack("2026.11.18.1", new string('x', 2_000_000)));
        Reject(() => CleanupRuleStore.Decode(bomb), "没有限制压缩规则的解压大小");
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
        var rules = Encode(Pack("2026.11.19.1"));
        var metadata = Release("1.3.0", Asset("DevUp.exe", binary), Asset(CleanupRuleStore.AssetName, rules));
        using var server = new MemoryServer(request => request.RequestUri!.Host == "api.github.com" ? metadata :
            request.RequestUri.AbsolutePath.EndsWith(CleanupRuleStore.AssetName) ? rules : binary);
        var updateStore = new CleanupRuleStore(Path.Combine(fixture.Root, "updates-rules"), builtIn);
        var updater = new DevUpUpdateService(updateStore, () => new HttpClient(server, false), new Version(1, 2, 0));
        await updater.CheckAsync();
        Check(updater.HasProgramUpdate && updater.Available!.RulesAvailable, "没有分别识别程序和规则更新");
        await updater.InstallRulesAsync(null, default);
        Check(updateStore.Current.Version == "2026.11.19.1" && !updater.Available!.RulesAvailable, "规则独立更新失败");
        var downloaded = await updater.DownloadProgramAsync(Path.Combine(fixture.Root, "download"), null, default);
        Check(Hash(await File.ReadAllBytesAsync(downloaded)) == Hash(binary), "新版程序下载或校验失败");
        var sameVersion = new DevUpUpdateService(updateStore, () => new HttpClient(server, false), new Version(1, 3, 0, 0));
        await sameVersion.CheckAsync();
        Check(!sameVersion.HasProgramUpdate && !sameVersion.Available!.RulesAvailable,
            "三段与四段版本号比较导致重复更新");

        using var badServer = new MemoryServer(request => request.RequestUri!.Host == "api.github.com" ? metadata : [1, 2, 3]);
        var tampered = new DevUpUpdateService(new CleanupRuleStore(Path.Combine(fixture.Root, "bad-rules"), builtIn),
            () => new HttpClient(badServer, false), new Version(1, 2, 0));
        await tampered.CheckAsync();
        Check(tampered.HasProgramUpdate && !tampered.Available!.RulesAvailable, "损坏规则阻止了独立的程序更新提示");
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
            var offline = new DevUpUpdateService(updateStore, () => new HttpClient(offlineServer, false));
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
            catch (Exception ex) when (ex is IOException ||
                ex is InvalidOperationException && ex.Message.Contains("更新文件目录不匹配")) { checks++; }
        }
        finally { UpdateInstaller.DiscardJob(rejectedJob); }
        Console.WriteLine($"规则与程序更新回归通过：{checks} 项；替换测试只操作专用临时目录，不启动新版程序。");
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
