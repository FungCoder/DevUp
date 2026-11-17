using System.ComponentModel;

namespace DevUp.Models;

public enum SoftwareBatchOutcome { Pending, Running, Succeeded, Failed, Cancelled, Skipped }

/// <summary>独立于可刷新的软件清单保存的批量升级结果。</summary>
public sealed class SoftwareBatchItem
    : INotifyPropertyChanged
{
    public string PackageId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public string AvailableVersion { get; init; } = "";
    public SoftwareBatchOutcome Outcome { get; private set; }
    public string Message { get; private set; } = "等待执行";
    public string OutcomeText => Outcome switch
    {
        SoftwareBatchOutcome.Running => "正在升级",
        SoftwareBatchOutcome.Succeeded => "升级完成",
        SoftwareBatchOutcome.Failed => "升级失败",
        SoftwareBatchOutcome.Cancelled => "已取消，需复核",
        SoftwareBatchOutcome.Skipped => "已跳过",
        _ => "尚未执行"
    };

    public void SetOutcome(SoftwareBatchOutcome outcome, string message)
    {
        Outcome = outcome;
        Message = message;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
