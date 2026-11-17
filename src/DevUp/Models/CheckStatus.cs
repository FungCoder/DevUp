using System.ComponentModel;

namespace DevUp.Models;

public enum CheckPhase { NotChecked, Running, Succeeded, Failed, Cancelled, Stale }

/// <summary>模块检查状态；失败、取消及过期均保留上次成功结果和时间。</summary>
public sealed class CheckStatus : INotifyPropertyChanged
{
    public CheckPhase Phase { get; private set; }
    public DateTime? LastSucceededAt { get; private set; }
    public string Error { get; private set; } = "";
    public bool HasResult => LastSucceededAt.HasValue;
    public bool IsCurrent => Phase == CheckPhase.Succeeded;
    public string CountText => HasResult ? _count.ToString() : "—";
    private int _count;

    public string Description
    {
        get
        {
            var text = Phase switch
            {
                CheckPhase.Running => "正在检查",
                CheckPhase.Succeeded => "检查完成",
                CheckPhase.Failed => "检查失败",
                CheckPhase.Cancelled => "检查已取消",
                CheckPhase.Stale => "需要重新检查",
                _ => "尚未检查"
            };
            return HasResult
                ? $"{text} · {(IsCurrent ? "" : "上次结果 ")}{LastSucceededAt:MM-dd HH:mm}"
                : text;
        }
    }

    /// <summary>执行检查并记录结果；异常和取消向调用方传播。</summary>
    public async Task RunAsync(Func<Task<int>> check)
    {
        Phase = CheckPhase.Running;
        Error = "";
        Notify();
        try
        {
            var count = await check();
            _count = count;
            LastSucceededAt = DateTime.Now;
            Phase = CheckPhase.Succeeded;
        }
        catch (OperationCanceledException)
        {
            Phase = CheckPhase.Cancelled;
            throw;
        }
        catch (Core.UserCancelledException)
        {
            Phase = CheckPhase.Cancelled;
            throw;
        }
        catch (Exception exception)
        {
            Phase = CheckPhase.Failed;
            Error = exception.Message;
            throw;
        }
        finally { Notify(); }
    }

    public void MarkStale()
    {
        if (!HasResult || Phase != CheckPhase.Succeeded) return;
        Phase = CheckPhase.Stale;
        Notify();
    }

    public void UpdateCount(int count)
    {
        if (!HasResult || _count == count) return;
        _count = count;
        Notify();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
