using System.Runtime.InteropServices;
using DevUp.Models;
using Microsoft.Win32;

namespace DevUp.Services;

internal static partial class StartupBackend
{
    private static void ScanRegistry(StartupScan scan, bool machine, bool view32, string path)
    {
        using var root = BaseKey(machine, view32);
        using var key = root.OpenSubKey(path);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command)
                continue;
            var entry = new StartupEntry
            {
                Source = StartupSource.Registry, Machine = machine, Registry32 = view32,
                Location = path, Name = name, Command = command, Executable = ParseExecutable(command),
                Enabled = true, Note = path.EndsWith("RunOnce", StringComparison.Ordinal)
                    ? "一次性登录任务；禁用将移走该启动命令。" : "登录时运行；禁用将移走该启动命令。"
            };
            var approved = Approval(entry);
            scan.Entries.Add(entry with { Enabled = ApprovalState(approved) });
        }
    }

    // StartupApproved 为 Explorer 自身的控制信息。只识别已知状态，未知状态不猜测。
    internal static bool? ApprovalState(byte[]? data) => data switch
    {
        null => true,
        { Length: >= 12 } when BitConverter.ToUInt32(data, 0) is 2 or 6 => true,
        { Length: >= 12 } when BitConverter.ToUInt32(data, 0) is 3 or 7 => false,
        _ => null
    };

    private static string ApprovalPath(StartupEntry entry) => RunPath + @"Explorer\StartupApproved\" +
        (entry.Source == StartupSource.Folder ? "StartupFolder" :
            (entry.Location.EndsWith("RunOnce", StringComparison.OrdinalIgnoreCase) ? "RunOnce" : "Run") +
            (entry.Registry32 ? "32" : ""));

    private static byte[]? Approval(StartupEntry entry)
    {
        using var root = BaseKey(entry.Machine);
        using var key = root.OpenSubKey(ApprovalPath(entry));
        return key?.GetValue(entry.Name) switch { null => null, byte[] bytes => bytes, _ => [] };
    }

    private static void RestoreApproval(StartupEntry entry, StartupBackup backup)
    {
        using var root = BaseKey(entry.Machine);
        using var key = backup.Approval is null ? root.OpenSubKey(ApprovalPath(entry), true)
            : root.CreateSubKey(ApprovalPath(entry), true);
        if (backup.Approval is null) key?.DeleteValue(entry.Name, false);
        else key!.SetValue(entry.Name, backup.Approval, RegistryValueKind.Binary);
        key?.Flush();
    }

    private static void ChangeRegistry(StartupEntry entry, bool enable, StartupBackup? backup)
    {
        using var root = BaseKey(entry.Machine, entry.Registry32);
        using var key = root.CreateSubKey(entry.Location, true);
        var value = key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (enable)
        {
            if (value is not null && !Equals(value, backup!.Entry.Command))
                throw new IOException("同名启动命令已被其他程序修改，未覆盖。");
            if (backup!.ValueKind is not ((int)RegistryValueKind.String) and not ((int)RegistryValueKind.ExpandString))
                throw new InvalidDataException("原注册表类型无效。");
            key.SetValue(entry.Name, backup.Entry.Command, (RegistryValueKind)backup.ValueKind);
            RestoreApproval(entry, backup);
        }
        else
        {
            if (value is not string command || command != entry.Command)
                throw new IOException("启动命令在操作期间发生改变，请重新扫描。");
            SaveBackup(new StartupBackup { Entry = entry, ValueKind = (int)key.GetValueKind(entry.Name),
                Approval = Approval(entry) });
            key.DeleteValue(entry.Name, true);
        }
        key.Flush();
    }

    private static void ScanFolder(StartupScan scan, bool machine)
    {
        var folder = Folder(machine);
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            ReadSection(scan, file, () =>
            {
                var executable = Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ShortcutTarget(file) : ParseExecutable('"' + file + '"');
                var entry = new StartupEntry { Source = StartupSource.Folder, Machine = machine,
                    Location = folder, Name = Path.GetFileName(file), Command = file,
                    Executable = executable, Fingerprint = FileHash(file), Enabled = true,
                    Note = "禁用会将文件移入启动目录下的专用子目录，保留文件内容。" };
                scan.Entries.Add(entry with { Enabled = ApprovalState(Approval(entry)) });
            });
        }
    }

    private static string ShortcutTarget(string file)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(file);
            return NormalizeExecutable((string)((dynamic)shortcut).TargetPath);
        }
        finally { ReleaseCom(shortcut); ReleaseCom(shell); }
    }

    private static void ChangeFolder(StartupEntry entry, bool enable, StartupBackup? backup)
    {
        var original = Path.Combine(entry.Location, entry.Name);
        var disabled = DisabledFile(entry);
        CheckRegularPath(original);
        CheckRegularPath(disabled);
        if (enable)
        {
            if (!File.Exists(disabled)) throw new IOException("备份文件不存在，无法恢复。");
            if (File.Exists(original)) throw new IOException("启动目录已有同名文件，未覆盖。");
            if (FileHash(disabled) != backup!.FileHash) throw new IOException("备份文件已改变，未恢复。");
            File.Move(disabled, original);
            RestoreApproval(entry, backup);
        }
        else
        {
            if (!File.Exists(original)) throw new IOException("启动文件已消失，请重新扫描。");
            if (File.Exists(disabled)) throw new IOException("已存在同名禁用文件，未覆盖原备份。");
            var hash = FileHash(original);
            if (hash != entry.Fingerprint) throw new IOException("启动文件在操作期间发生改变，请重新扫描。");
            SaveBackup(new StartupBackup { Entry = entry, FileHash = hash, Approval = Approval(entry) });
            Directory.CreateDirectory(Path.GetDirectoryName(disabled)!);
            CheckRegularPath(disabled);
            File.Move(original, disabled);
            if (FileHash(disabled) != hash) throw new IOException("文件在移动期间发生改变，请检查启动文件。");
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
