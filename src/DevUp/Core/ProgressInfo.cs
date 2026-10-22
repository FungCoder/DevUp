namespace DevUp.Core;

/// <summary>进度回调载荷（Percent 为负数时界面显示不确定进度）。</summary>
public sealed class ProgressInfo
{
    public double Percent { get; init; } = -1;
    public string? Status { get; init; }
    public string? Detail { get; init; }
    public bool CanCancel { get; init; } = true;
}
