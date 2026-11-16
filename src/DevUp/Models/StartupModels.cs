using System.Security.Cryptography;
using System.Text;

namespace DevUp.Models;

internal enum StartupSource { Registry, Folder, Task, Service }

/// <summary>一个真实的自启动入口；身份与界面名称分开，避免按名称修改错误项目。</summary>
internal sealed record StartupEntry
{
    public StartupSource Source { get; init; }
    public bool Machine { get; init; }
    public bool Registry32 { get; init; }
    public string Location { get; init; } = "";
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public bool? Enabled { get; init; }
    public bool Present { get; init; } = true;
    public bool HasBackup { get; init; }
    public bool Protected { get; init; }
    public string Note { get; init; } = "";
    public string Id => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Source}|{Machine}|{Registry32}|{Location.ToUpperInvariant()}|{Name.ToUpperInvariant()}")));
    public string SourceText => Source switch
    {
        StartupSource.Registry => "注册表" + (Registry32 ? " · 32 位" : ""),
        StartupSource.Folder => "启动文件夹",
        StartupSource.Task => "开机 / 登录任务",
        _ => "Windows 服务"
    };
    public string ScopeText => Machine ? "计算机" : "当前用户";
    public string StateText => Enabled switch { true => "已启用", false => "已禁用", _ => "无法确认" };
    public bool CanDisable => !Protected && Present && Enabled != false;
    public bool CanRestore => !Protected && HasBackup && Enabled == false;
    public bool CanGroup => !string.IsNullOrEmpty(Executable) && !new[]
        { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "rundll32.exe", "msiexec.exe", "svchost.exe", "dllhost.exe" }
        .Contains(Path.GetFileName(Executable), StringComparer.OrdinalIgnoreCase);
    public string GroupKey => CanGroup ? Executable.ToUpperInvariant() : Id;
    public string Details => $"{Name}\n{SourceText} · {ScopeText}\n{Location}\n\n{Command}\n\n{Note}";
}

internal sealed record StartupScan(List<StartupEntry> Entries, List<string> Warnings);

internal sealed record StartupChangeRequest(string UserSid, bool Enable, List<StartupEntry> Entries);

internal sealed record StartupChangeResult(string Id, string Name, bool Success, string Message);

/// <summary>原始配置保存在相同权限范围的注册表中，应用移动或退出不影响恢复。</summary>
internal sealed record StartupBackup
{
    public StartupEntry Entry { get; init; } = new();
    public int ValueKind { get; init; }
    public int ServiceStart { get; init; }
    public byte[]? Approval { get; init; }
    public string FileHash { get; init; } = "";
}
