using System.IO;
using System.Text.Json;
using DevUp.Core;
using DevUp.Services;

if (args.Contains("--dialogs-only") || args.Contains("--dialogs-preview"))
{
    DialogChecks.Run(args.Contains("--dialogs-preview"));
    return;
}

if (args.Contains("--console-only"))
{
    await ConsoleChecks.RunAsync();
    return;
}
if (args.Contains("--winget-download-probe"))
{
    await ConsoleChecks.DownloadProbeAsync();
    return;
}
if (args.Contains("--probe-small"))
{
    await ConsoleChecks.DownloadProbeAsync("7zip.7zip");
    return;
}
SoftwareChecks.Run();
if (args.Contains("--software-only")) return;

var originalDirectory = Environment.CurrentDirectory;
var variables = new[] { "TEMP", "TMP", "npm_config_cache", "npm_config_prefix", "USERPROFILE", "LOCALAPPDATA" };
var environment = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
var checks = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

try
{
    // 从其他目录启动、切换工作目录，都不能影响应用自身的数据根目录。
    Environment.CurrentDirectory = Path.GetPathRoot(AppContext.BaseDirectory)!;
    PortablePaths.Initialize();
    Check(PortablePaths.Root == Path.GetFullPath(AppContext.BaseDirectory), "数据根目录未跟随程序");
    Check(PortablePaths.StorageRoot == Path.Combine(PortablePaths.Root, "DevUpData"), "没有统一的数据总目录");
    Check(new[] { PortablePaths.Data, PortablePaths.Logs, PortablePaths.Runtime, PortablePaths.Temp }
        .All(path => Path.GetDirectoryName(path) == PortablePaths.StorageRoot), "应用目录没有集中在总目录下");
    Check(environment.All(entry => Environment.GetEnvironmentVariable(entry.Key) == entry.Value),
        "初始化修改了系统软件的进程环境");

    var settings = new AppSettings();
    settings.Save();
    using (var json = JsonDocument.Parse(File.ReadAllText(AppSettings.FilePath)))
    {
        Check(json.RootElement.GetProperty("BackupRoot").GetString() == Path.Combine("DevUpData", "backups"), "备份默认路径未保存为相对路径");
        Check(json.RootElement.GetProperty("DownloadRoot").GetString() == Path.Combine("DevUpData", "downloads"), "下载默认路径未保存为相对路径");
    }
    var loaded = AppSettings.Load();
    Check(loaded.DownloadRoot == Path.Combine(PortablePaths.StorageRoot, "downloads"), "默认下载路径未集中在总目录下");
    Check(AppSettings.EnsureDir("chosen-downloads") == Path.Combine(PortablePaths.Root, "chosen-downloads"),
        "用户选择的目录未生效");

    // 父目录被文件占用时必须报错，且不创建默认下载目录。
    var blocker = Path.Combine(PortablePaths.Temp, "blocked-directory");
    File.WriteAllText(blocker, "block");
    try
    {
        try
        {
            AppSettings.EnsureDir(Path.Combine(blocker, "downloads"));
            throw new InvalidOperationException("不可写目录没有报错");
        }
        catch (IOException) { checks++; }
        Check(!Directory.Exists(AppSettings.DefaultDownloadRoot), "错误时擅自创建了默认下载目录");
    }
    finally { File.Delete(blocker); }

    using (var lockFile = new FileStream(AppSettings.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        try
        {
            settings.Save();
            throw new InvalidOperationException("保存失败被静默忽略");
        }
        catch (IOException) { checks++; }
    }

    // 迁移旧配置时保留用户选择的外部路径，不移动或删除旧文件。
    var legacyFile = Path.Combine(PortablePaths.Root, "data", "settings.json");
    var previousLegacy = File.Exists(legacyFile) ? File.ReadAllBytes(legacyFile) : null;
    var currentSettings = File.ReadAllBytes(AppSettings.FilePath);
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        var externalDownload = Path.Combine(Path.GetDirectoryName(PortablePaths.Root.TrimEnd(Path.DirectorySeparatorChar))!, "custom-downloads");
        File.WriteAllText(legacyFile, JsonSerializer.Serialize(new AppSettings
        {
            BackupRoot = "backups", DownloadRoot = externalDownload, StartupMode = AppSettings.ModeNormal
        }));
        File.Delete(AppSettings.FilePath);
        var migrated = AppSettings.Load();
        Check(File.Exists(AppSettings.FilePath), "旧配置未迁入新目录");
        Check(migrated.BackupRoot == AppSettings.DefaultBackupRoot, "旧默认目录未切换到总目录下");
        Check(migrated.DownloadRoot == externalDownload, "迁移修改了用户选择的外部路径");
        Check(migrated.StartupMode == AppSettings.ModeNormal && File.Exists(legacyFile), "旧设置或旧文件未被保留");
    }
    finally
    {
        File.WriteAllBytes(AppSettings.FilePath, currentSettings);
        if (previousLegacy is not null) File.WriteAllBytes(legacyFile, previousLegacy);
        else
        {
            File.Delete(legacyFile);
            if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(legacyFile)!).Any())
                Directory.Delete(Path.GetDirectoryName(legacyFile)!);
        }
    }

    if (args.Contains("--paths-only"))
    {
        Console.WriteLine($"便携目录及配置迁移检查通过：{checks} 项。");
        return;
    }

    var result = await PsBridge.RunAsync("systemInfo");
    Check(result.GetProperty("ok").GetBoolean(), "真实 PowerShell 调用失败");
    Check(Path.GetRelativePath(PortablePaths.StorageRoot, PsBridge.RuntimeDir).StartsWith("runtime" + Path.DirectorySeparatorChar),
        "运行组件写到了程序目录外");
    Check(File.Exists(PsBridge.ScriptPath), "内嵌脚本未释放");
    Check(File.Exists(Path.Combine(Ui.LogDir, "devup.log")), "日志没有写入便携目录");
    Check(!Directory.EnumerateFiles(PortablePaths.Temp, "devup_*.json").Any(), "成功调用后残留通信文件");

    try
    {
        await PsBridge.RunAsync("invalid-portable-test-action");
        throw new InvalidOperationException("后端错误未传递");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("未知动作")) { checks++; }
    Check(!Directory.EnumerateFiles(PortablePaths.Temp, "devup_*.json").Any(), "失败调用后残留通信文件");
    Console.WriteLine($"便携路径回归检查通过：{checks} 项。");
}
finally
{
    Environment.CurrentDirectory = originalDirectory;
    PsBridge.CleanupRuntime();
}
