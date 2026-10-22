using System.IO;
using System.Text;
using System.Text.Json;

namespace DevUp.Services;

/// <summary>
/// 用户设置（保存到 %LOCALAPPDATA%\DevUp\settings.json，不需要管理员权限）。
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

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevUp");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static string DefaultBackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DevUp", "驱动备份");

    public static string DefaultDownloadRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DevUp");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            if (s is null) return new AppSettings();

            // 配置文件被手工改坏时，集合可能为 null；这里统一补齐，避免后续空引用
            s.IgnoredDriverUpdates ??= new List<string>();
            s.IgnoredDevices ??= new List<string>();
            s.IgnoredSoftware ??= new List<string>();
            if (string.IsNullOrWhiteSpace(s.BackupRoot)) s.BackupRoot = DefaultBackupRoot;
            if (string.IsNullOrWhiteSpace(s.DownloadRoot)) s.DownloadRoot = DefaultDownloadRoot;
            if (string.IsNullOrWhiteSpace(s.StartupMode)) s.StartupMode = ModeAsk;
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Core.Ui.Log("保存设置失败: " + ex.Message, "WARN");
        }
    }

    /// <summary>确保目录存在；失败时回退到默认目录。</summary>
    public static string EnsureDir(string path, string fallback)
    {
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
