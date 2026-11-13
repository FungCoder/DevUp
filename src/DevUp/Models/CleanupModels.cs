using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DevUp.Models;

internal sealed record CleanupFile(
    string Root, string Path, long Length, DateTime LastWriteUtc, DateTime CreationUtc)
{
    public string SizeText => CleanupSize.Format(Length);
}

internal sealed record CleanupCategory(
    string Name, string Group, string Description, bool DefaultSelected,
    IReadOnlyList<CleanupFile> Files, long Bytes, int Skipped, string Note);

internal sealed record CleanupScan(IReadOnlyList<CleanupCategory> Categories)
{
    public long Bytes => Categories.Sum(category => category.Bytes);
    public int FileCount => Categories.Sum(category => category.Files.Count);
}

internal sealed record CleanupProgress(
    string Category, int Completed, int Total, long Bytes, int Files,
    string Detail, CleanupCategory? Result = null);

internal sealed record CleanupOutcome(
    int Deleted, long Bytes, int Skipped, bool Cancelled, IReadOnlyList<string> Errors);

internal static class CleanupSize
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):N1} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):N2} GB"
    };
}

/// <summary>清理页分类行，扫描结果只在 UI 线程更新。</summary>
internal sealed class CleanupRow : INotifyPropertyChanged
{
    private bool _selected;
    private string _status = "等待扫描";
    private CleanupCategory _category;

    public CleanupRow(CleanupCategory category)
    {
        _category = category;
        _selected = category.DefaultSelected;
    }

    public string Name => _category.Name;
    public string Group => _category.Group;
    public string Description => _category.Description;
    public string Note => _category.Note;
    public IReadOnlyList<CleanupFile> Files => _category.Files;
    public long Bytes => _category.Bytes;
    public string SizeText => CleanupSize.Format(Bytes);
    public string CountText => $"{Files.Count:N0} 个文件";
    public bool Selected { get => _selected; set { _selected = value; Raise(); } }
    public bool IsActive => _status is "扫描中" or "清理中";
    public string Status
    {
        get => _status;
        set { _status = value; Raise(); Raise(nameof(IsActive)); }
    }

    public void SetResult(CleanupCategory category)
    {
        _category = category;
        foreach (var property in new[] { nameof(Note), nameof(Files), nameof(Bytes),
                     nameof(SizeText), nameof(CountText) }) Raise(property);
        Status = category.Note.Length > 0 ? category.Note : "扫描完成";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
