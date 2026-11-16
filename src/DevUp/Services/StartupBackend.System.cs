using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using DevUp.Models;

namespace DevUp.Services;

internal static partial class StartupBackend
{
    private static void WithScheduler(Action<dynamic> action)
    {
        object? scheduler = null;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
            ((dynamic)scheduler!).Connect();
            action((dynamic)scheduler);
        }
        finally { ReleaseCom(scheduler); }
    }

    private static void ScanTasks(StartupScan scan) => WithScheduler(scheduler =>
    {
        object root = scheduler.GetFolder("\\");
        try { ScanTaskFolder(scan, root); }
        finally { ReleaseCom(root); }
    });

    private static void ScanTaskFolder(StartupScan scan, dynamic folder)
    {
        object? tasks = null;
        object? children = null;
        try
        {
            tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN，不能漏掉隐藏任务。
            var count = (int)((dynamic)tasks).Count;
            for (var index = 1; index <= count; index++)
            {
                object? task = null;
                try
                {
                    task = ((dynamic)tasks)[index];
                    var entry = ReadTask(task);
                    if (entry is not null) scan.Entries.Add(entry);
                }
                catch (Exception ex) { scan.Warnings.Add($"任务 {folder.Path} #{index}：{ex.Message}"); }
                finally { ReleaseCom(task); }
            }
            children = folder.GetFolders(0);
            var childCount = (int)((dynamic)children).Count;
            for (var index = 1; index <= childCount; index++)
            {
                object? child = null;
                try
                {
                    child = ((dynamic)children)[index];
                    ScanTaskFolder(scan, child);
                }
                catch (Exception ex) { scan.Warnings.Add($"任务子目录 {folder.Path} #{index}：{ex.Message}"); }
                finally { ReleaseCom(child); }
            }
        }
        finally { ReleaseCom(children); ReleaseCom(tasks); }
    }

    private static StartupEntry? ReadTask(dynamic task)
    {
        var xml = XDocument.Parse((string)task.Xml);
        var root = xml.Root ?? throw new InvalidDataException("任务定义为空。");
        var ns = root.Name.Namespace;
        var triggers = root.Element(ns + "Triggers")?.Elements().Where(element =>
            element.Name.LocalName is "BootTrigger" or "LogonTrigger").ToArray() ?? [];
        if (triggers.Length == 0) return null;
        var actions = root.Element(ns + "Actions")?.Elements().ToArray() ?? [];
        var commands = actions.Select(action => action.Name.LocalName == "Exec"
            ? $"\"{action.Element(ns + "Command")?.Value}\" {action.Element(ns + "Arguments")?.Value}".Trim()
            : action.ToString(SaveOptions.DisableFormatting)).ToArray();
        var executablePaths = actions.Where(action => action.Name.LocalName == "Exec")
            .Select(action => NormalizeExecutable(Environment.ExpandEnvironmentVariables(
                action.Element(ns + "Command")?.Value ?? ""))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var path = (string)task.Path;
        var protectedTask = path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase);
        // Enabled 是此功能唯一修改的字段，其他任务定义变化必须使旧快照失效。
        root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Remove();
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting))));
        return new StartupEntry
        {
            Source = StartupSource.Task, Machine = true, Location = path, Name = (string)task.Name,
            Command = string.Join("\n", commands),
            Fingerprint = fingerprint,
            Executable = actions.Length == 1 && executablePaths.Length == 1 ? executablePaths[0] : "",
            Enabled = (bool)task.Enabled && triggers.Any(trigger =>
                !string.Equals(trigger.Element(ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase)),
            Protected = protectedTask,
            Note = protectedTask ? "Windows 系统维护任务，仅展示；请在任务计划程序中按需管理。"
                : "禁用整个任务，包含此任务的所有动作与触发器；不终止已经运行的任务。"
        };
    }

    private static void ChangeTask(StartupEntry entry, bool enable, StartupBackup? backup)
    {
        WithScheduler(scheduler =>
        {
            object? folder = null;
            object? task = null;
            try
            {
                folder = scheduler.GetFolder("\\");
                task = ((dynamic)folder).GetTask(entry.Location);
                var actual = ReadTask(task);
                if (actual is null || actual.Command != entry.Command || actual.Fingerprint != entry.Fingerprint || actual.Protected)
                    throw new IOException("任务定义已改变或受保护，未修改。");
                if (!enable) SaveBackup(backup ?? new StartupBackup { Entry = actual });
                ((dynamic)task).Enabled = enable;
                if ((bool)((dynamic)task).Enabled != enable) throw new IOException("任务启用状态校验失败。");
            }
            finally { ReleaseCom(task); ReleaseCom(folder); }
        });
    }

    private static void ScanServices(StartupScan scan)
    {
        using var root = BaseKey(true);
        const string path = @"SYSTEM\CurrentControlSet\Services";
        using var services = root.OpenSubKey(path) ?? throw new IOException("无法读取服务列表。");
        foreach (var name in services.GetSubKeyNames())
        {
            ReadSection(scan, "服务 " + name, () =>
            {
                using var key = services.OpenSubKey(name);
                if (key?.GetValue("Type") is not int type || (type & 0x30) == 0) return;
                if (key.GetValue("Start") is not int start || start is not (2 or 3 or 4)) return;
                var command = key.GetValue("ImagePath", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var executable = ParseExecutable(command);
                var system = WindowsExecutable(executable) || string.IsNullOrWhiteSpace(executable)
                    || key.GetValue("LaunchProtected", 0) is int protection && protection != 0;
                using var triggers = key.OpenSubKey("TriggerInfo");
                var triggered = triggers is not null;
                // 手动服务仍可能被服务触发器启动，不能把它标为彻底禁用。
                if (start == 3 && !triggered) return;
                var note = start switch
                {
                    2 => key.GetValue("DelayedAutoStart", 0) is int delayed && delayed == 1 ? "自动（延迟启动）。" : "自动启动。",
                    3 => "手动 / 触发启动，不能视为已禁用。",
                    _ => "服务启动类型为禁用。"
                };
                scan.Entries.Add(new StartupEntry { Source = StartupSource.Service, Machine = true,
                    Location = path, Name = name, Command = command, Executable = executable,
                    Enabled = start == 4 ? false : true, Protected = system,
                    Note = note + (system ? " Windows 或受保护服务，仅展示。" : " 禁用可能影响软件功能；不停止当前正在运行的服务。") });
            });
        }
    }

    private static void ChangeService(StartupEntry entry, bool enable, StartupBackup? backup)
    {
        using var root = BaseKey(true);
        using var key = root.OpenSubKey(entry.Location + "\\" + entry.Name)
            ?? throw new IOException("服务已消失。");
        var command = key.GetValue("ImagePath", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        if (command != entry.Command || WindowsExecutable(ParseExecutable(command)) ||
            key.GetValue("LaunchProtected", 0) is int protection && protection != 0)
            throw new IOException("服务配置已改变或受保护，未修改。");
        var start = key.GetValue("Start") as int? ?? throw new IOException("无法读取服务启动类型。");
        if (!enable && start is not (2 or 3)) throw new IOException("服务启动类型已改变，请重新扫描。");
        if (enable && backup!.ServiceStart is not (2 or 3)) throw new InvalidDataException("服务原始启动类型无效。");
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, entry.Name, 0x0002);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!enable) SaveBackup(backup ?? new StartupBackup { Entry = entry, ServiceStart = start });
                if (!ChangeServiceConfig(service, uint.MaxValue, enable ? (uint)backup!.ServiceStart : 4,
                        uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error,
        string? binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
