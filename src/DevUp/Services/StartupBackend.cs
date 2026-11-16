using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevUp.Models;
using Microsoft.Win32;

namespace DevUp.Services;

/// <summary>只操作重新扫描到的入口，修改前备份，修改后从各自来源重新验证。</summary>
internal static partial class StartupBackend
{
    internal const string BackupPath = @"SOFTWARE\DevUp\StartupBackups";
    internal const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";
    private const string DisabledFolder = "DevUp.DisabledStartup";
    private static readonly string[] RunKeys = [RunPath + "Run", RunPath + "RunOnce"];

    internal static StartupScan Scan()
    {
        var scan = new StartupScan([], []);
        var backups = ReadBackups(scan.Warnings);
        var unreadable = new HashSet<(StartupSource, bool, bool, string)>();
        foreach (var machine in new[] { false, true })
        {
            foreach (var view32 in machine ? new[] { false, true } : new[] { false })
                foreach (var key in RunKeys)
                    ReadSection(scan, key, () => ScanRegistry(scan, machine, view32, key),
                        () => unreadable.Add((StartupSource.Registry, machine, view32, key)));
            ReadSection(scan, machine ? "公共启动文件夹" : "用户启动文件夹",
                () => ScanFolder(scan, machine),
                () => unreadable.Add((StartupSource.Folder, machine, false, Folder(machine))));
        }
        ReadSection(scan, "计划任务", () => ScanTasks(scan));
        ReadSection(scan, "Windows 服务", () => ScanServices(scan));

        var live = scan.Entries.ToDictionary(entry => entry.Id);
        foreach (var backup in backups.Values)
        {
            var entry = backup.Entry;
            if (live.TryGetValue(entry.Id, out var current))
            {
                var index = scan.Entries.IndexOf(current);
                scan.Entries[index] = current with
                {
                    HasBackup = true,
                    Note = current.Note + (current.Command != entry.Command
                        ? " 原配置已发生变化，恢复前需要处理冲突。"
                        : current.Enabled == true ? " 存在禁用备份；入口仍启用或已被软件重新注册。" : " 可恢复原配置。")
                };
            }
            else if (entry.Source is StartupSource.Registry or StartupSource.Folder)
            {
                var valid = !unreadable.Contains((entry.Source, entry.Machine, entry.Registry32, entry.Location));
                if (valid && entry.Source == StartupSource.Folder)
                {
                    try { valid = File.Exists(DisabledFile(entry)) && FileHash(DisabledFile(entry)) == backup.FileHash; }
                    catch (Exception ex) { valid = false; scan.Warnings.Add($"启动文件备份 {entry.Name}：{ex.Message}"); }
                }
                scan.Entries.Add(entry with
                {
                    Enabled = valid ? false : null, HasBackup = true, Present = false,
                    Note = valid ? "原启动入口已移出生效位置；可恢复。" : "禁用文件备份缺失或已改变，无法确认，恢复已受保护。"
                });
            }
            else
            {
                scan.Entries.Add(entry with { Enabled = null, HasBackup = true, Protected = true, Present = false,
                    Note = "无法读取原入口，可能已删除或无权限；保留恢复记录。" });
            }
        }
        scan.Entries.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        return scan;
    }

    internal static List<StartupChangeResult> Change(StartupChangeRequest request)
    {
        // 使用其他管理员账户提升时不能把那个账户的 HKCU 当成原用户的 HKCU。
        if (request.UserSid != WindowsIdentity.GetCurrent().User?.Value)
            throw new InvalidOperationException("提权账户与原用户不同，已停止操作。请使用原用户的管理员权限运行 DevUp。");
        if (request.Entries is null || request.Entries.Count is 0 or > 1000)
            throw new ArgumentException("请选择 1 至 1000 个自启入口。");
        // 跨进程互斥，避免两个窗口同时覆盖同一份恢复记录。
        using var mutex = new Mutex(false, @"Local\DevUp.StartupManagement");
        var locked = false;
        try
        {
            try { locked = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new InvalidOperationException("其他窗口正在修改自启配置，请稍后重试。");
            var live = Scan().Entries.ToDictionary(entry => entry.Id);
            var results = new List<StartupChangeResult>();
            foreach (var selected in request.Entries.DistinctBy(entry => entry.Id))
            {
                try
                {
                    Validate(selected);
                    if (!live.TryGetValue(selected.Id, out var actual) || actual.Command != selected.Command ||
                        actual.Fingerprint != selected.Fingerprint)
                        throw new InvalidOperationException("入口已改变或消失，请重新扫描后再操作。");
                    if (actual.Protected || actual.Enabled is null &&
                            (!actual.Present || actual.Source is not (StartupSource.Registry or StartupSource.Folder)))
                        throw new InvalidOperationException("入口受保护或状态无法确认，未修改。");
                    if (request.Enable && !actual.HasBackup)
                        throw new InvalidOperationException("没有 DevUp 保存的原配置，不能猜测恢复设置。");
                    if (!request.Enable && actual.Enabled == false)
                    {
                        results.Add(new(actual.Id, actual.Name, true, "原本已禁用，无需修改。"));
                        continue;
                    }
                    Apply(actual, request.Enable);
                    results.Add(new(actual.Id, actual.Name, true, "等待重新读取系统状态。"));
                }
                catch (Exception ex)
                {
                    results.Add(new(selected.Id, selected.Name, false, ex.Message));
                }
            }
            // 调用成功并不足以证明生效：必须再次查询真实入口。
            var after = Scan().Entries.ToDictionary(entry => entry.Id);
            return results.Select(result =>
            {
                if (!result.Success) return result;
                var verified = after.TryGetValue(result.Id, out var entry) && entry.Enabled == request.Enable;
                if (verified && request.Enable)
                {
                    try
                    {
                        using var root = BaseKey(entry!.Machine);
                        using var key = root.OpenSubKey(BackupPath, true);
                        key?.DeleteValue(result.Id, false);
                        key?.Flush();
                    }
                    catch (Exception ex)
                    {
                        return result with { Success = false, Message = "已恢复配置，但清理恢复记录失败：" + ex.Message };
                    }
                }
                return result with
                {
                    Success = verified,
                    Message = verified ? request.Enable ? "已恢复，系统配置复核通过。" : "已禁用，系统配置复核通过。"
                        : request.Enable && entry is { Present: true, Enabled: null }
                            ? "原配置已写回，但启用状态无法确认；未标记成功，已保留恢复记录。"
                            : "状态复核未通过，可能被软件重新注册、配置已改变或无读取权限；请查看入口明细。"
                };
            }).ToList();
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }

    private static void Apply(StartupEntry entry, bool enable)
    {
        var backup = ReadBackup(entry);
        if (backup is not null && (backup.Entry.Command != entry.Command ||
                backup.Entry.Fingerprint != entry.Fingerprint))
            throw new InvalidOperationException("当前入口与原配置冲突，未覆盖任何配置。");
        if (enable && backup is null) throw new InvalidOperationException("原配置不存在，无法恢复。");
        switch (entry.Source)
        {
            case StartupSource.Registry: ChangeRegistry(entry, enable, backup); break;
            case StartupSource.Folder: ChangeFolder(entry, enable, backup); break;
            case StartupSource.Task: ChangeTask(entry, enable, backup); break;
            case StartupSource.Service: ChangeService(entry, enable, backup); break;
            default: throw new ArgumentException("不支持的启动来源。");
        }
    }

    private static void ReadSection(StartupScan scan, string name, Action action, Action? onError = null)
    {
        try { action(); }
        catch (Exception ex) { onError?.Invoke(); scan.Warnings.Add($"{name}：{ex.Message}"); }
    }

    private static RegistryKey BaseKey(bool machine, bool view32 = false) =>
        RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            view32 ? RegistryView.Registry32 : RegistryView.Registry64);

    private static string Folder(bool machine) => Environment.GetFolderPath(machine
        ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);

    private static string DisabledFile(StartupEntry entry) => Path.Combine(entry.Location, DisabledFolder, entry.Name);

    private static void Validate(StartupEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 16383 || entry.Command.Length > 32768)
            throw new ArgumentException("自启入口无效。");
        var valid = entry.Source switch
        {
            StartupSource.Registry => RunKeys.Contains(entry.Location, StringComparer.OrdinalIgnoreCase)
                && (!entry.Registry32 || entry.Machine),
            StartupSource.Folder => Path.GetFullPath(entry.Location).Equals(Folder(entry.Machine),
                StringComparison.OrdinalIgnoreCase) && Path.GetFileName(entry.Name) == entry.Name
                && entry.Name is not ("." or "..") && entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
            StartupSource.Task => entry.Machine && entry.Location.StartsWith('\\')
                && entry.Location.Length <= 1024 && !entry.Location.Contains("..", StringComparison.Ordinal),
            StartupSource.Service => entry.Machine && entry.Location == @"SYSTEM\CurrentControlSet\Services"
                && !entry.Name.Contains('\\') && !entry.Name.Contains('/'),
            _ => false
        };
        if (!valid) throw new ArgumentException("自启入口来源不在允许范围内。");
    }

    private static Dictionary<string, StartupBackup> ReadBackups(List<string> warnings)
    {
        var result = new Dictionary<string, StartupBackup>();
        foreach (var machine in new[] { false, true })
        {
            try
            {
                using var root = BaseKey(machine);
                using var key = root.OpenSubKey(BackupPath);
                if (key is null) continue;
                foreach (var id in key.GetValueNames())
                {
                    try
                    {
                        var backup = JsonSerializer.Deserialize<StartupBackup>(key.GetValue(id) as string ?? "");
                        if (backup is null) throw new InvalidDataException("恢复记录为空。");
                        Validate(backup.Entry);
                        if (backup.Entry.Id != id || backup.Entry.Machine != machine)
                            throw new InvalidDataException("恢复记录身份不匹配。");
                        result.Add(id, backup);
                    }
                    catch (Exception ex) { warnings.Add($"恢复记录 {id}：{ex.Message}"); }
                }
            }
            catch (Exception ex) { warnings.Add($"读取自启恢复记录：{ex.Message}"); }
        }
        return result;
    }

    private static StartupBackup? ReadBackup(StartupEntry entry)
    {
        using var root = BaseKey(entry.Machine);
        using var key = root.OpenSubKey(BackupPath);
        var json = key?.GetValue(entry.Id) as string;
        if (json is null) return null;
        var backup = JsonSerializer.Deserialize<StartupBackup>(json)
            ?? throw new InvalidDataException("恢复记录损坏。");
        Validate(backup.Entry);
        if (backup.Entry.Id != entry.Id) throw new InvalidDataException("恢复记录身份不匹配。");
        return backup;
    }

    private static void SaveBackup(StartupBackup backup)
    {
        using var root = BaseKey(backup.Entry.Machine);
        using var key = root.CreateSubKey(BackupPath, true);
        key.SetValue(backup.Entry.Id, JsonSerializer.Serialize(backup), RegistryValueKind.String);
        key.Flush();
    }

    internal static string ParseExecutable(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command).Trim();
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            return end > 1 ? NormalizeExecutable(expanded[1..end]) : "";
        }
        // SCM 接受未加引号且含空格的路径；以真实可执行文件为界，不按第一个空格截断。
        foreach (Match match in Regex.Matches(expanded, @"(?i)\.(exe|com)(?=\s|$)"))
        {
            var candidate = expanded[..(match.Index + match.Length)];
            if (File.Exists(candidate)) return NormalizeExecutable(candidate);
        }
        var first = expanded.Split([' ', '\t'], 2)[0];
        return NormalizeExecutable(first);
    }

    private static string NormalizeExecutable(string path)
    {
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : ""; }
        catch (ArgumentException) { return ""; }
    }

    private static bool WindowsExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CheckRegularPath(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("启动路径包含链接或联接点，已停止操作。");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("启动文件是链接，已停止操作。");
    }
}
