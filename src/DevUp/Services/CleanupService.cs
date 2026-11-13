using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using DevUp.Core;
using DevUp.Models;
using FluentCleaner.Models;
using FluentCleaner.Services;

namespace DevUp.Services;

/// <summary>内嵌白名单规则驱动的只读扫描与显式清理，不执行外部规则或脚本。</summary>
internal sealed class CleanupService
{
    private readonly IReadOnlyList<CleanerEntry> _rules;
    private readonly IReadOnlyDictionary<string, string> _variables;
    private readonly Func<IEnumerable<string>> _protectedPaths;
    private readonly Func<string, bool> _isProcessRunning;
    private CleanupScan? _lastScan;

    public CleanupService() : this(ReadRules(), DefaultVariables(), () =>
        new[] { PortablePaths.Root, PortablePaths.StorageRoot,
            Ctx.Settings.BackupRoot, Ctx.Settings.DownloadRoot }
            .Concat(PersonalFolders()), IsProcessRunning) { }

    internal CleanupService(string rules, IReadOnlyDictionary<string, string> variables,
        Func<IEnumerable<string>> protectedPaths, Func<string, bool> isProcessRunning)
    {
        _rules = new Winapp2Parser().Parse(rules);
        if (_rules.Count == 0 || _rules.Any(rule => rule.FileKeys.Count == 0 ||
                rule.RegKeys.Count != 0 || rule.ExcludeKeys.Count != 0 ||
                rule.FileKeys.Any(key => key.Flag == FileKeyFlag.RemoveSelf)))
            throw new InvalidDataException("清理规则为空或包含不支持的操作。");
        _variables = variables;
        _protectedPaths = protectedPaths;
        _isProcessRunning = isProcessRunning;
    }

    public IReadOnlyList<CleanupCategory> Categories => _rules.Select(rule =>
        Category(rule, Array.Empty<CleanupFile>(), 0, 0, "")).ToArray();

    public Task<CleanupScan> ScanAsync(IProgress<CleanupProgress> progress, CancellationToken ct)
        => Task.Run(() => Scan(progress, ct), ct);

    public Task<CleanupOutcome> CleanAsync(CleanupScan scan, IReadOnlySet<string> names,
        IProgress<CleanupProgress> progress, CancellationToken ct)
        => Task.Run(() => Clean(scan, names, progress, ct));

    private CleanupScan Scan(IProgress<CleanupProgress> progress, CancellationToken ct)
    {
        _lastScan = null;
        var categories = new List<CleanupCategory>(_rules.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedPaths = GetProtectedPaths();
        var reportClock = Stopwatch.StartNew();
        long totalBytes = 0;
        var count = 0;
        for (var index = 0; index < _rules.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var rule = _rules[index];
            var files = new List<CleanupFile>();
            long bytes = 0;
            var skipped = 0;
            var note = "";
            progress.Report(new(rule.Name, index, _rules.Count, totalBytes, count, "正在检查缓存目录"));
            if (HasRunningProcess(rule))
            {
                note = "软件正在运行，请退出后重新扫描";
            }
            else
            {
                var cutoff = DateTime.UtcNow - MinimumAge(rule);
                foreach (var key in rule.FileKeys)
                {
                    var patterns = key.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var root in ResolveRoots(key.Path, protectedPaths, ct, () => skipped++))
                    {
                        var pending = new Stack<string>();
                        pending.Push(root);
                        while (pending.TryPop(out var directory))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!IsSafePath(directory, protectedPaths)) { skipped++; continue; }
                            try
                            {
                                foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                                {
                                    ct.ThrowIfCancellationRequested();
                                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                                    if (IsProtected(item.FullName, protectedPaths)) continue;
                                    if (item is DirectoryInfo)
                                    {
                                        if (key.Flag == FileKeyFlag.Recurse) pending.Push(item.FullName);
                                        continue;
                                    }
                                    if (item is not FileInfo file || file.LastWriteTimeUtc > cutoff ||
                                        !patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(
                                            pattern, file.Name, ignoreCase: true)) || !seen.Add(file.FullName)) continue;
                                    if ((file.Attributes & FileAttributes.ReadOnly) != 0) { skipped++; continue; }
                                    var candidate = new CleanupFile(root, file.FullName, file.Length,
                                        file.LastWriteTimeUtc, file.CreationTimeUtc);
                                    files.Add(candidate);
                                    bytes += candidate.Length;
                                    totalBytes += candidate.Length;
                                    count++;
                                    if (count > 250_000)
                                        throw new InvalidOperationException("候选文件超过 25 万个，请先缩小临时目录后重试。");
                                    if (reportClock.ElapsedMilliseconds >= 100)
                                    {
                                        progress.Report(new(rule.Name, index, _rules.Count,
                                            totalBytes, count, file.FullName));
                                        reportClock.Restart();
                                    }
                                }
                            }
                            catch (Exception ex) when (IsFileError(ex)) { skipped++; }
                        }
                    }
                }
                if (skipped > 0) note = $"跳过 {skipped:N0} 个不可访问、只读或链接项目";
                else if (files.Count == 0) note = "未发现可清理文件";
            }
            var result = Category(rule, files.AsReadOnly(), bytes, skipped, note);
            categories.Add(result);
            progress.Report(new(rule.Name, index + 1, _rules.Count, totalBytes, count, note, result));
        }
        ct.ThrowIfCancellationRequested();
        _lastScan = new CleanupScan(categories.AsReadOnly());
        return _lastScan;
    }

    private CleanupOutcome Clean(CleanupScan scan, IReadOnlySet<string> names,
        IProgress<CleanupProgress> progress, CancellationToken ct)
    {
        if (!ReferenceEquals(scan, _lastScan))
            throw new InvalidOperationException("扫描结果已失效，请重新扫描后再清理。");
        _lastScan = null; // 每份扫描清单只能消费一次；取消和失败后也必须重新扫描。
        var selected = scan.Categories.Where(category => names.Contains(category.Name)).ToArray();
        var total = selected.Sum(category => category.Files.Count);
        var processed = 0;
        var deleted = 0;
        var skipped = 0;
        long bytes = 0;
        var errors = new List<string>();
        var protectedPaths = GetProtectedPaths();
        var clock = Stopwatch.StartNew();
        foreach (var category in selected)
        {
            var rule = _rules.Single(entry => entry.Name == category.Name);
            var running = HasRunningProcess(rule);
            var processClock = Stopwatch.StartNew();
            foreach (var file in category.Files)
            {
                if (ct.IsCancellationRequested)
                    return new(deleted, bytes, skipped, true, errors.AsReadOnly());
                try
                {
                    if (processClock.ElapsedMilliseconds >= 250)
                    {
                        running = HasRunningProcess(rule);
                        processClock.Restart();
                    }
                    if (running) throw new IOException("软件正在运行，请退出后重新扫描。");
                    if (!IsBelow(file.Path, file.Root) || !IsSafePath(file.Path, protectedPaths))
                        throw new IOException("文件路径已改变、位于受保护目录或包含链接。");
                    CleanupFileAccess.DeleteVerified(file);
                    deleted++;
                    bytes += file.Length;
                }
                catch (Exception ex) when (IsFileError(ex) || ex is Win32Exception)
                {
                    skipped++;
                    if (errors.Count < 12) errors.Add($"{file.Path}：{ex.Message}");
                }
                processed++;
                if (processed == 1 || clock.ElapsedMilliseconds >= 100 || processed == total)
                {
                    progress.Report(new(category.Name, processed, total, bytes, deleted,
                        $"已处理 {processed:N0} / {total:N0} 个文件 · 跳过 {skipped:N0} 个"));
                    clock.Restart();
                }
            }
        }
        return new(deleted, bytes, skipped, false, errors.AsReadOnly());
    }

    private IEnumerable<string> ResolveRoots(string rawPath, string[] protectedPaths,
        CancellationToken ct, Action skipped)
    {
        var expanded = rawPath;
        foreach (var variable in _variables)
            expanded = expanded.Replace($"%{variable.Key}%", variable.Value, StringComparison.OrdinalIgnoreCase);
        if (expanded.Contains('%') || !Path.IsPathFullyQualified(expanded) || expanded.StartsWith(@"\\"))
            throw new InvalidDataException("清理规则含有未识别的变量或非本地绝对路径。");
        var volume = Path.GetPathRoot(expanded)!;
        var segments = expanded[volume.Length..].Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string> { volume };
        foreach (var segment in segments)
        {
            var next = new List<string>();
            foreach (var parent in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsSafePath(parent, protectedPaths)) { skipped(); continue; }
                try
                {
                    if (segment.Contains('*') || segment.Contains('?'))
                    {
                        foreach (var directory in Directory.EnumerateDirectories(parent, segment))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (IsSafePath(directory, protectedPaths)) next.Add(directory);
                        }
                    }
                    else
                    {
                        var directory = Path.GetFullPath(Path.Combine(parent, segment));
                        if (Directory.Exists(directory)) next.Add(directory);
                    }
                }
                catch (Exception ex) when (IsFileError(ex)) { skipped(); }
            }
            paths = next;
        }
        var roots = new List<string>();
        foreach (var path in paths)
        {
            if (IsSafeRoot(path, rawPath) && IsSafePath(path, protectedPaths)) roots.Add(path);
            else skipped();
        }
        return roots;
    }

    private bool IsSafeRoot(string path, string rawPath)
    {
        if (Path.GetPathRoot(path)?.TrimEnd('\\').Equals(path.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase) == true) return false;
        // 拒绝错误 TEMP 配置把用户目录、AppData 或 Windows 根目录当成垃圾目录。
        if (_variables.Where(pair => !pair.Key.Equals("TEMP", StringComparison.OrdinalIgnoreCase))
            .Any(pair => pair.Value.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || IsBelow(pair.Value, path))) return false;
        if (rawPath.Equals("%TEMP%", StringComparison.OrdinalIgnoreCase))
        {
            if (_variables.TryGetValue("WINDIR", out var windows) && IsBelow(path, windows) &&
                !string.Equals(path, Path.Combine(windows, "Temp"), StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var key in new[] { "PROGRAMFILES", "PROGRAMFILES(X86)" })
                if (_variables.TryGetValue(key, out var programs) && IsBelow(path, programs)) return false;
        }
        return true;
    }

    private static bool IsSafePath(string path, string[] protectedPaths)
    {
        if (IsProtected(path, protectedPaths)) return false;
        try
        {
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (IsFileError(ex)) { return false; }
    }

    private string[] GetProtectedPaths() => _protectedPaths().Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsProtected(string path, IEnumerable<string> protectedPaths)
        => protectedPaths.Any(root => string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase) || IsBelow(path, root));

    internal static bool IsBelow(string path, string root)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasRunningProcess(CleanerEntry rule)
        => ProcessNames(rule.Name).Any(_isProcessRunning);

    private static string[] ProcessNames(string name) => name switch
    {
        "Microsoft Edge 缓存" => ["msedge"],
        "Google Chrome 缓存" => ["chrome"],
        "Firefox 缓存" => ["firefox"],
        "Brave 缓存" => ["brave"],
        "Visual Studio Code 缓存" => ["Code"],
        "Discord 缓存" => ["Discord", "DiscordCanary", "DiscordPTB"],
        "Slack 缓存" => ["slack"],
        "Spotify 缓存" => ["Spotify"],
        _ => []
    };

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static TimeSpan MinimumAge(CleanerEntry rule) => rule.Name switch
    {
        "用户临时文件" or "Windows 临时文件" => TimeSpan.FromDays(7),
        "Windows 错误报告" or "应用崩溃转储" or "系统崩溃转储" => TimeSpan.FromDays(1),
        _ => TimeSpan.Zero
    };

    private static CleanupCategory Category(CleanerEntry rule, IReadOnlyList<CleanupFile> files,
        long bytes, int skipped, string note)
        => new(rule.Name, rule.Section ?? "其他缓存", rule.Warning ?? "", rule.Default,
            files, bytes, skipped, note);

    private static bool IsFileError(Exception exception)
        => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static string ReadRules()
    {
        using var stream = typeof(CleanupService).Assembly.GetManifestResourceStream("cleaning-rules.ini")
            ?? throw new InvalidOperationException("内嵌清理规则缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyDictionary<string, string> DefaultVariables()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'),
            ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["PROGRAMDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["PROGRAMFILES"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["PROGRAMFILES(X86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

    private static IEnumerable<string> PersonalFolders() => new[]
        {
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos
        }.Select(Environment.GetFolderPath).Append(Native.GetDownloadsFolder());
}
