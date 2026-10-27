using System.IO;
using System.Text;
using System.Text.Json;

namespace DevUp.Services;

/// <summary>
/// 用户设置（保存到程序目录下的 DevUpData/data/settings.json）。
/// 目前包含：驱动备份目录、下载目录。
/// </summary>
public sealed class AppSettings
{
    public const string ModeAsk = "ask";
    public const string ModeAdmin = "admin";
    public const string ModeNormal = "normal";

    public string BackupRoot { get; set; } = DefaultBackupRoot;
    public string DownloadRoot { get; set; } = DefaultDownloadRoot;

    /// <summary>启动方式：ask=每次询问，admin=直接提权，normal=普通权限。</summary>
    public string StartupMode { get; set; } = ModeAsk;

    /// <summary>已忽略的驱动更新（键：UpdateId:Revision）。</summary>
    public List<string> IgnoredDriverUpdates { get; set; } = new();

    /// <summary>已忽略的异常设备（键：InstanceId）。</summary>
    public List<string> IgnoredDevices { get; set; } = new();

    /// <summary>已忽略的软件更新（键：wingetId@可用版本）。</summary>
    public List<string> IgnoredSoftware { get; set; } = new();

    public static string Dir => Core.PortablePaths.Data;

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static string DefaultBackupRoot =>
        Path.Combine(Core.PortablePaths.StorageRoot, "backups");

    public static string DefaultDownloadRoot =>
        Path.Combine(Core.PortablePaths.StorageRoot, "downloads");

    public static AppSettings Load()
    {
        var source = File.Exists(FilePath)
            ? FilePath : Path.Combine(Core.PortablePaths.Root, "data", "settings.json");
        AppSettings settings;
        try
        {
            if (!File.Exists(source)) return new AppSettings();
            var json = File.ReadAllText(source, Encoding.UTF8);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            if (s is null) return new AppSettings();

            // 配置文件被手工改坏时，集合可能为 null；这里统一补齐，避免后续空引用
            s.IgnoredDriverUpdates ??= new List<string>();
            s.IgnoredDevices ??= new List<string>();
            s.IgnoredSoftware ??= new List<string>();
            if (string.IsNullOrWhiteSpace(s.BackupRoot)) s.BackupRoot = DefaultBackupRoot;
            if (string.IsNullOrWhiteSpace(s.DownloadRoot)) s.DownloadRoot = DefaultDownloadRoot;
            s.BackupRoot = Path.GetFullPath(s.BackupRoot, Core.PortablePaths.Root);
            s.DownloadRoot = Path.GetFullPath(s.DownloadRoot, Core.PortablePaths.Root);
            if (source != FilePath)
            {
                if (string.Equals(s.BackupRoot, Path.Combine(Core.PortablePaths.Root, "backups"),
                        StringComparison.OrdinalIgnoreCase)) s.BackupRoot = DefaultBackupRoot;
                if (string.Equals(s.DownloadRoot, Path.Combine(Core.PortablePaths.Root, "downloads"),
                        StringComparison.OrdinalIgnoreCase)) s.DownloadRoot = DefaultDownloadRoot;
            }
            if (string.IsNullOrWhiteSpace(s.StartupMode)) s.StartupMode = ModeAsk;
            settings = s;
        }
        catch
        {
            return new AppSettings();
        }
        // 仅迁移配置文件，不移动或删除旧目录中的其他内容；保存失败必须明确报错。
        if (source != FilePath) settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var document = JsonSerializer.SerializeToNode(this)!;
            document[nameof(BackupRoot)] = Core.PortablePaths.ToStoredPath(BackupRoot);
            document[nameof(DownloadRoot)] = Core.PortablePaths.ToStoredPath(DownloadRoot);
            var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            throw new IOException($"保存设置失败：{FilePath}。{ex.Message}", ex);
        }
    }

    /// <summary>确保指定目录可写，失败时明确报错，不回退到其他目录。</summary>
    public static string EnsureDir(string path)
        => Core.PortablePaths.EnsureWritableDirectory(path);
}
