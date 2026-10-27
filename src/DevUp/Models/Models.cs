using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DevUp.Models;

/// <summary>设备（来自 Get-PnpDevice / Win32_PnPEntity）。</summary>
public sealed class DeviceItem
{
    public string InstanceId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Problem { get; set; } = "CM_PROB_NONE";
    public string ProblemDescription { get; set; } = "";
    public bool IsPresent { get; set; }
    public string Manufacturer { get; set; } = "";
    public string Service { get; set; } = "";
    public string HardwareIds { get; set; } = "";
    public string CompatibleIds { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string DriverProvider { get; set; } = "";
    public string DriverDate { get; set; } = "";
    public string DriverInfPath { get; set; } = "";
    public string Signed { get; set; } = "";
    public string ClassGuid { get; set; } = "";

    /// <summary>该设备的异常提醒是否已被用户忽略（仅对“存在且有异常”的设备生效）。</summary>
    public bool Ignored { get; set; }

    /// <summary>问题码数值（28=安装失败, 22=已禁用, 14=待重启, 10=无法启动, 43/45=未启动）。</summary>
    public int ProblemCode { get; set; }

    public bool HasProblem => ProblemCode != 0;
    public bool NeedsDriver => HasProblem && ProblemCode is 1 or 10 or 19 or 28 or 43 or 45;
    public bool IsUnknownDevice => ProblemCode is 1 or 19 or 28;
    public bool IsThirdParty => !string.IsNullOrEmpty(DriverProvider)
                               && !DriverProvider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    public string ProblemText => ProblemCode switch
    {
        0 => "正常",
        1 => "未安装驱动",
        10 => "设备无法启动",
        14 => "需重启完成安装",
        19 => "驱动入口加载失败",
        22 => "设备已禁用",
        28 => "驱动安装失败",
        43 => "系统报告硬件错误",
        45 => "未连接",
        _ => $"问题码 {ProblemCode}"
    };
}

/// <summary>驱动仓库（DriverStore）中的一个驱动包。</summary>
public sealed class DriverPackageItem
{
    public string InfPath { get; set; } = "";
    public string PublishedName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string DriverDate { get; set; } = "";
    public string Signature { get; set; } = "未检测";
    public bool IsThirdParty => !Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Windows Update / Microsoft Update 提供的驱动更新候选。</summary>
public sealed class DriverUpdateItem : INotifyPropertyChanged
{
    private bool _select;
    private bool _ignored;

    /// <summary>是否勾选（安装选中时只处理勾选项）。</summary>
    public bool Select
    {
        get => _select;
        set
        {
            if (_select == value) return;
            _select = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Select)));
        }
    }

    /// <summary>是否被用户忽略（忽略后从更新列表隐藏，可在「已忽略」中恢复）。</summary>
    public bool Ignored
    {
        get => _ignored;
        set
        {
            if (_ignored == value) return;
            _ignored = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ignored)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; set; } = "";
    public string UpdateId { get; set; } = "";
    public int Revision { get; set; }
    public string Kb { get; set; } = "";
    public string DriverModel { get; set; } = "";
    public string DriverClass { get; set; } = "";
    public string DriverManufacturer { get; set; } = "";
    public string DriverVerDate { get; set; } = "";
    public string Size { get; set; } = "";
    public string Source { get; set; } = "Windows Update";
    public string Key => $"{UpdateId}:{Revision}";
    public string Status { get; set; } = "";
}

/// <summary>已安装软件（注册表 ARP + MSIX）。</summary>
public sealed class SoftwareItem : INotifyPropertyChanged
{
    public string DisplayName { get; set; } = "";

    private string _displayVersion = "";
    public string DisplayVersion
    {
        get => _displayVersion;
        set { if (_displayVersion != value) { _displayVersion = value; OnChanged(); } }
    }

    public string Publisher { get; set; } = "";
    public string InstallDate { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string Source { get; set; } = "注册表";
    public string Scope { get; set; } = "";
    public string Key { get; set; } = "";

    private string? _wingetId;

    /// <summary>winget 能匹配到的包 ID（由 winget list 输出反查得到）。</summary>
    public string? WingetId
    {
        get => _wingetId;
        set { if (_wingetId != value) { _wingetId = value; OnChanged(); OnChanged(nameof(HasWingetMatch)); } }
    }

    private string? _availableVersion;
    public string? AvailableVersion
    {
        get => _availableVersion;
        set { if (_availableVersion != value) { _availableVersion = value; OnChanged(); OnChanged(nameof(HasUpdate)); } }
    }

    private bool _ignored;

    /// <summary>该条软件更新是否已被忽略（键含可用版本，出现新版本后会自动重新显示）。</summary>
    public bool Ignored
    {
        get => _ignored;
        set { if (_ignored != value) { _ignored = value; OnChanged(); } }
    }

    public bool HasUpdate => !string.IsNullOrEmpty(AvailableVersion) && !string.IsNullOrEmpty(WingetId);

    /// <summary>是否在 winget 源中匹配到了该软件（用于状态标签）。</summary>
    public bool HasWingetMatch => !string.IsNullOrEmpty(WingetId);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// winget 官方清单里的包元数据：发行说明、发布日期、安装包地址与 SHA256。
/// 只读取本地 winget 源中的清单，不做任何推测；缺失字段保持为空。
/// </summary>
public sealed class WingetPackageDetails
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallerType { get; set; } = "";
    public string InstallerUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string ReleaseNotesUrl { get; set; } = "";
    public string Homepage { get; set; } = "";

    public bool HasSha256 => !string.IsNullOrWhiteSpace(Sha256);
    public bool HasInstallerUrl => !string.IsNullOrWhiteSpace(InstallerUrl);
    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
    public bool HasReleaseNotesUrl => !string.IsNullOrWhiteSpace(ReleaseNotesUrl);
}

/// <summary>本地安装包的计算结果（用于和官方清单比对）。</summary>
public sealed class FileHashResult
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Md5 { get; set; } = "";
    public bool? MatchesOfficial { get; set; }
}

/// <summary>winget 报告的可用更新。</summary>
public sealed class SoftwareUpdateItem
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public string Source { get; set; } = "winget";
    public string Status { get; set; } = "";
}

/// <summary>npm 全局安装的包（AI 命令行工具、pnpm 等）。</summary>
public sealed class NpmPackageItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string Description { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string Location { get; set; } = "";

    private string? _latestVersion;
    public string? LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (_latestVersion == value) return;
            _latestVersion = value;
            OnChanged();
            OnChanged(nameof(HasUpdate));
            OnChanged(nameof(StatusText));
            OnChanged(nameof(LatestDisplay));
        }
    }

    private bool _checked;
    /// <summary>是否已经成功向 npm 源检查过最新版本（检查失败时为 false，避免误报“已是最新”）。</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            OnChanged();
            OnChanged(nameof(HasUpdate));
            OnChanged(nameof(StatusText));
        }
    }

    public bool HasUpdate => Checked
                             && !string.IsNullOrWhiteSpace(LatestVersion)
                             && !string.Equals(LatestVersion, InstalledVersion, StringComparison.OrdinalIgnoreCase);

    public string LatestDisplay => string.IsNullOrWhiteSpace(LatestVersion) ? "—" : LatestVersion!;

    public string StatusText => HasUpdate ? "可更新" : Checked ? "已是最新" : "未检查";

    /// <summary>npm / corepack 本身不提供卸载按钮，避免把包管理器卸掉。</summary>
    public bool IsCorePackage =>
        Name.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
        Name.Equals("corepack", StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>npm 运行环境探测结果。</summary>
public sealed class NpmEnvironment
{
    public bool Available { get; set; }
    public string NpmPath { get; set; } = "";
    public string NpmVersion { get; set; } = "";
    public string NodeVersion { get; set; } = "";
    public string GlobalRoot { get; set; } = "";
    public string Error { get; set; } = "";
    public string Detail { get; set; } = "";

    /// <summary>找到了 npm 命令文件，但它无法正常运行（通常是 Node.js 安装不完整）。</summary>
    public bool NpmBroken { get; set; }

    public bool NodeDetected => !string.IsNullOrWhiteSpace(NodeVersion);
}

/// <summary>驱动备份（pnputil 导出）。</summary>
public sealed class BackupItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public int PackageCount { get; set; }
    public string SizeText => SizeBytes > 0 ? $"{SizeBytes / 1024.0 / 1024.0:0.0} MB" : "";
}

/// <summary>扫描汇总。</summary>
public sealed class ScanSummary
{
    public int DeviceTotal { get; set; }
    public int DevicePresent { get; set; }
    public int DeviceProblem { get; set; }
    public int DeviceUnknown { get; set; }
    public int DriverPackages { get; set; }
    public int DriverUpdates { get; set; }
    public int SoftwareTotal { get; set; }
    public int SoftwareUpdates { get; set; }
    public DateTime ScannedAt { get; set; } = DateTime.Now;
}

public sealed class LogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";
}

public sealed class ScanState : INotifyPropertyChanged
{
    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ObservableCollection<DriverPackageItem> DriverPackages { get; } = new();
    public ObservableCollection<DriverUpdateItem> DriverUpdates { get; } = new();
    public ObservableCollection<SoftwareItem> Software { get; } = new();
    public ObservableCollection<SoftwareUpdateItem> SoftwareUpdates { get; } = new();
    public ObservableCollection<BackupItem> Backups { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();

    private ScanSummary _summary = new();
    private bool _isBusy;
    private string _statusText = "就绪";

    public ScanSummary Summary
    {
        get => _summary;
        set { _summary = value; Raise(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; Raise(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
