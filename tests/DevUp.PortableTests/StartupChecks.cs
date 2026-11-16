using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DevUp.Models;
using DevUp.Services;
using Microsoft.Win32;

internal static class StartupChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        var prefix = "DevUp-Test-" + Guid.NewGuid().ToString("N");
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var folderExisted = Directory.Exists(folder);
        var disabledFolder = Path.Combine(folder, "DevUp.DisabledStartup");
        var disabledExisted = Directory.Exists(disabledFolder);
        var filename = prefix + ".lnk";
        var file = Path.Combine(folder, filename);
        var disabled = Path.Combine(disabledFolder, filename);
        var ids = new HashSet<string>();
        object? shell = null;
        object? shortcut = null;
        object? scheduler = null;
        object? taskRoot = null;
        string? taskName = null;
        var schedulerNote = "";
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var run = user.CreateSubKey(StartupBackend.RunPath + "Run", true);
        using var once = user.CreateSubKey(StartupBackend.RunPath + "RunOnce", true);
        const string command = "\"%SystemRoot%\\System32\\cmd.exe\" /c exit";
        List<StartupEntry> Entries() => StartupBackend.Scan().Entries.Where(entry => entry.Name.StartsWith(prefix)).ToList();
        List<StartupChangeResult> Change(List<StartupEntry> entries, bool enable)
        {
            foreach (var entry in entries) ids.Add(entry.Id);
            return StartupBackend.Change(new(sid, enable, entries));
        }
        try
        {
            run.SetValue(prefix, command, RegistryValueKind.ExpandString);
            once.SetValue(prefix + "-once", command, RegistryValueKind.String);
            Directory.CreateDirectory(folder);
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(file);
            ((dynamic)shortcut).TargetPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            ((dynamic)shortcut).Arguments = "/c exit";
            ((dynamic)shortcut).Save();
            var originalFile = File.ReadAllBytes(file);
            // 一个专用、无副作用的登录测试任务；当前账户无注册权限时明确记录。
            try
            {
                scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
                ((dynamic)scheduler!).Connect();
                taskRoot = ((dynamic)scheduler).GetFolder("\\");
                var xml = $"""
                    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                      <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger></Triggers>
                      <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
                      <Settings><Enabled>true</Enabled><ExecutionTimeLimit>PT1M</ExecutionTimeLimit></Settings>
                      <Actions Context="Author"><Exec><Command>{Environment.SystemDirectory}\cmd.exe</Command><Arguments>/c exit</Arguments></Exec></Actions>
                    </Task>
                    """;
                object task = ((dynamic)taskRoot).RegisterTask(prefix + "-task", xml, 2, sid, null, 3, null);
                Marshal.FinalReleaseComObject(task);
                taskName = prefix + "-task";
            }
            catch (COMException ex) { schedulerNote = "计划任务注册未覆盖：" + ex.Message; }
            var entries = Entries();
            Check(entries.Count == (taskName is null ? 3 : 4), "自启扫描遗漏测试入口：" + string.Join(",", entries.Select(entry => entry.Name)));
            Check(entries.All(entry => entry.Enabled == true), "新建测试入口的启用状态不准确");
            if (taskName is not null && !DevUp.Core.Native.IsAdministrator())
            {
                ((dynamic)taskRoot!).DeleteTask(taskName, 0);
                taskName = null;
                entries = entries.Where(entry => entry.Source != StartupSource.Task).ToList();
                schedulerNote = "已验证计划任务扫描；禁用与恢复需要管理员测试环境。";
            }
            Check(entries.Where(entry => entry.Executable.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
                .All(entry => !entry.CanGroup), "共享命令解释器被错误地按软件合并");
            Check(StartupBackend.ApprovalState(new byte[1]) is null, "未知批准状态没有保留不确定性");
            Check(StartupBackend.ParseExecutable("\"C:\\Program Files\\Vendor\\app.exe\" --startup") ==
                @"C:\Program Files\Vendor\app.exe", "含空格的程序路径解析错误");

            var results = Change(entries, false);
            Check(results.All(result => result.Success), "禁用失败：" + string.Join(";", results.Where(result => !result.Success).Select(result => result.Message)));
            Check(run.GetValue(prefix) is null && once.GetValue(prefix + "-once") is null,
                "只修改了显示状态，没有移除生效的注册表启动值");
            Check(!File.Exists(file) && File.Exists(disabled), "启动文件没有真正移出生效目录");
            var off = Entries();
            Check(off.All(entry => entry.Enabled == false && entry.HasBackup), "禁用后的重新扫描不准确");
            Check(Change(off, false).All(result => result.Success), "重复禁用不幂等");

            run.SetValue(prefix, command + " altered", RegistryValueKind.ExpandString);
            var conflict = Change([off.Single(entry => entry.Name == prefix)], true);
            Check(conflict.All(result => !result.Success) && (string?)run.GetValue(prefix,
                null, RegistryValueOptions.DoNotExpandEnvironmentNames) == command + " altered", "恢复覆盖了软件重新注册的配置");
            Check(Entries().Single(entry => entry.Name == prefix).Enabled == true,
                "软件重新注册后仍误报为禁用");
            run.DeleteValue(prefix);

            var restored = Change(Entries(), true);
            Check(restored.All(result => result.Success), "恢复失败：" + string.Join(";", restored.Where(result => !result.Success).Select(result => result.Message)));
            Check(run.GetValueKind(prefix) == RegistryValueKind.ExpandString &&
                (string?)run.GetValue(prefix, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == command,
                "恢复没有保留原始注册表类型和环境变量");
            Check(File.ReadAllBytes(file).SequenceEqual(originalFile), "恢复改变了启动文件内容");
            Check(Entries().All(entry => entry.Enabled == true && !entry.HasBackup), "恢复未验证真实状态或未清理恢复记录");

            var unknownName = prefix + "-unknown";
            using (var approval = user.CreateSubKey(StartupBackend.RunPath + @"Explorer\StartupApproved\Run", true))
            {
                var unknownBytes = new byte[12];
                unknownBytes[0] = 1;
                approval.SetValue(unknownName, unknownBytes, RegistryValueKind.Binary);
                run.SetValue(unknownName, command, RegistryValueKind.ExpandString);
                var unknown = Entries().Single(entry => entry.Name == unknownName);
                Check(unknown.Enabled is null && unknown.CanDisable, "未知批准格式妨碍禁用真实存在的入口");
                Check(Change([unknown], false).All(result => result.Success) && run.GetValue(unknownName) is null,
                    "未知批准状态的启动值没有真正移除");
                var unknownRestore = Change([Entries().Single(entry => entry.Name == unknownName)], true);
                Check(unknownRestore.All(result => !result.Success) && run.GetValue(unknownName) is not null &&
                    ((byte[])approval.GetValue(unknownName)!).SequenceEqual(unknownBytes),
                    "恢复未知批准信息时错误承诺启用成功，或没有保留原配置");
            }

            var forged = entries[0] with { Location = @"SOFTWARE\DevUp\InvalidStartupTarget" };
            Check(Change([forged], false).All(result => !result.Success), "越界自启来源没有被拒绝");
            try
            {
                StartupBackend.Change(new("S-1-5-18", false, entries));
                throw new InvalidOperationException("跨账户请求未被拒绝");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("提权账户")) { checks++; }
            run.SetValue(prefix, command + " changed", RegistryValueKind.ExpandString);
            Check(Change([entries.Single(entry => entry.Name == prefix)], false).All(result => !result.Success),
                "旧扫描快照覆盖了新的启动配置");
        }
        finally
        {
            run.DeleteValue(prefix, false);
            run.DeleteValue(prefix + "-unknown", false);
            once.DeleteValue(prefix + "-once", false);
            using var backupKey = user.OpenSubKey(StartupBackend.BackupPath, true);
            using var approvalKey = user.OpenSubKey(StartupBackend.RunPath + @"Explorer\StartupApproved\Run", true);
            using var folderApproval = user.OpenSubKey(StartupBackend.RunPath + @"Explorer\StartupApproved\StartupFolder", true);
            foreach (var id in ids) backupKey?.DeleteValue(id, false);
            approvalKey?.DeleteValue(prefix, false);
            approvalKey?.DeleteValue(prefix + "-unknown", false);
            folderApproval?.DeleteValue(filename, false);
            if (taskName is not null && taskRoot is not null)
            {
                ((dynamic)taskRoot).DeleteTask(taskName, 0);
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var machineBackup = machine.OpenSubKey(StartupBackend.BackupPath, true);
                // 系统级恢复记录仅在有写权限时才会由测试产生。
                foreach (var id in ids) machineBackup?.DeleteValue(id, false);
            }
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(disabled)) File.Delete(disabled);
            if (!disabledExisted && Directory.Exists(disabledFolder) && !Directory.EnumerateFileSystemEntries(disabledFolder).Any())
                Directory.Delete(disabledFolder);
            if (!folderExisted && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            foreach (var com in new[] { taskRoot, scheduler, shortcut, shell })
                if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }
        Console.WriteLine($"自启管理检查通过：{checks} 项；仅修改唯一命名的测试入口，已清理。{schedulerNote}");
        Console.WriteLine("当前测试不创建或修改真实软件服务；服务提权和重启后的实际行为需在管理员测试环境验证。");
    }
}
