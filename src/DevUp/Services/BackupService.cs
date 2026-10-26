using System.IO;
using System.Text.Json;
using DevUp.Core;
using DevUp.Models;

namespace DevUp.Services;

/// <summary>驱动备份 / 还原 / 系统还原点（复用系统 pnputil 与 Checkpoint-Computer）。</summary>
public sealed class BackupService
{
    /// <summary>备份根目录（用户可在“备份与工具”页更改）。</summary>
    public static string BackupRoot => Ctx.Settings.BackupRoot;

    private static string ResolvedRoot()
        => AppSettings.EnsureDir(BackupRoot, AppSettings.DefaultBackupRoot);

    /// <summary>备份全部驱动包（pnputil /export-driver *）。</summary>
    public async Task<BackupItem> BackupAllAsync(CancellationToken ct = default)
    {
        var dir = Path.Combine(ResolvedRoot(), "全部驱动_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);

        var result = await PsBridge.RunAsync("exportDrivers",
            new Dictionary<string, string> { ["BackupDirectory"] = dir },
            elevated: true, ct: ct);
        ValidateExportResult(result, 1);

        return Describe(dir);
    }

    /// <summary>备份指定驱动包（oemN.inf）。</summary>
    public async Task<BackupItem> BackupDriversAsync(IEnumerable<string> publishedNames, CancellationToken ct = default)
    {
        var names = publishedNames.Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) throw new InvalidOperationException("没有可备份的驱动包");

        var dir = Path.Combine(ResolvedRoot(), "驱动_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);

        var result = await PsBridge.RunAsync("exportDrivers",
            new Dictionary<string, string>
            {
                ["BackupDirectory"] = dir,
                ["PublishedNames"] = string.Join(",", names)
            },
            elevated: true, ct: ct);
        ValidateExportResult(result, names.Count);

        return Describe(dir);
    }

    /// <summary>从备份目录恢复驱动（pnputil /add-driver，逐个 INF 安装）。</summary>
    public async Task<string> RestoreAsync(string backupDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(backupDir)) throw new DirectoryNotFoundException(backupDir);
        var infs = Directory.GetFiles(backupDir, "*.inf", SearchOption.AllDirectories);
        if (infs.Length == 0) throw new InvalidOperationException("备份目录中没有 INF 文件");

        var json = await PsBridge.RunAsync("importDrivers",
            new Dictionary<string, string> { ["BackupDirectory"] = backupDir },
            elevated: true, ct: ct);

        var installed = 0;
        var total = 0;
        var failures = new List<string>();

        if (json.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("installed", out var n) && n.ValueKind == JsonValueKind.Number)
                installed = n.GetInt32();
            if (data.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
                total = t.GetInt32();

            if (data.TryGetProperty("results", out var arr))
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var ok = e.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                    if (ok) continue;
                    var name = DriverUpdateService.Str(e, "name");
                    var message = DriverUpdateService.Str(e, "message");
                    failures.Add($"{name}: {message}");
                }
            }
        }

        if (total == 0) total = infs.Length;
        var text = $"已恢复 {installed} / {total} 个驱动包";
        if (failures.Count > 0)
            text += "\n\n以下驱动包还原失败：\n" + string.Join("\n", failures.Take(5));
        return text;
    }

    /// <summary>创建系统还原点（需管理员）。</summary>
    public async Task CreateRestorePointAsync(string description, CancellationToken ct = default)
    {
        await PsBridge.RunAsync("restorePoint",
            new Dictionary<string, string> { ["Description"] = description },
            elevated: true, ct: ct);
    }

    public List<BackupItem> ListBackups()
    {
        var list = new List<BackupItem>();
        var root = ResolvedRoot();
        if (!Directory.Exists(root)) return list;

        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                var item = Describe(dir);
                if (item.PackageCount > 0) list.Add(item);
            }
            catch { }
        }
        return list.OrderByDescending(b => b.CreatedAt).ToList();
    }

    private static void ValidateExportResult(JsonElement result, int expectedCount)
    {
        if (!result.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("results", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("驱动备份未返回导出结果，无法确认备份是否成功。");

        var failures = new List<string>();
        var count = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            count++;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                failures.Add("导出结果格式无效");
                continue;
            }
            if (entry.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                continue;

            var name = DriverUpdateService.Str(entry, "name");
            var message = DriverUpdateService.Str(entry, "message");
            failures.Add($"{name}: {message}");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("驱动备份失败：" + string.Join("；", failures));
        if (count != expectedCount)
            throw new InvalidOperationException(
                $"驱动备份结果不完整：请求 {expectedCount} 项，返回 {count} 项。");
    }

    private static BackupItem Describe(string dir)
    {
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        return new BackupItem
        {
            Name = Path.GetFileName(dir),
            Path = dir,
            CreatedAt = Directory.GetCreationTime(dir),
            SizeBytes = files.Sum(f => new FileInfo(f).Length),
            PackageCount = Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories).Length
        };
    }
}
