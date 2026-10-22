using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevUp.Core;

/// <summary>
/// PowerShell 后端桥接：把内嵌的脚本与开源模块释放到本地，然后以 JSON 契约调用。
/// 复用 PSWindowsUpdate / winget / Get-PnpDevice / Checkpoint-Computer 等系统与开源能力。
///
/// 权限模型：
///   · 普通权限的界面进程把运行时放在 %LOCALAPPDATA%\DevUp\runtime（用户自己拥有，无需管理员）。
///   · 需要管理员的动作（驱动搜索/安装、驱动备份还原、还原点）由“提权子进程”执行：
///     它以 runas 重新启动自身副本，运行时放在 %ProgramData%\DevUp\runtime\<随机目录>，
///     并用 ACL 收紧为仅 Administrators / SYSTEM 可写，避免普通用户预置同名文件造成本地提权。
/// </summary>
public static class PsBridge
{
    /// <summary>提权子进程标记：结果写文件、由父进程轮询取回。</summary>
    private const string ElevatedActionArg = "--elevated-action";
    private const string ElevatedArgsArg = "--elevated-args";
    private const string ElevatedOutArg = "--elevated-out";

    private static readonly object Gate = new();
    private static bool _ready;
    private static string? _runtimeDir;

    /// <summary>当前进程使用的运行时目录（管理员进程与普通用户进程不同）。</summary>
    public static string RuntimeDir => _runtimeDir ??= ResolveRuntimeDir();

    public static string ScriptPath => Path.Combine(RuntimeDir, "devup.ps1");

    /// <summary>提权子进程结束后删除其临时运行时目录。</summary>
    public static void CleanupRuntime()
    {
        var dir = _runtimeDir;
        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            // 只清理自己创建的“随机子目录”，不碰用户级目录
            if (!Path.GetFileName(dir).StartsWith("s-", StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* 清理失败不影响主流程 */ }
    }

    private static string ResolveRuntimeDir()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (Native.IsAdministrator())
        {
            // 管理员：每次运行使用独立随机目录，并立即收紧 ACL。
            // 随机名可防止普通用户事先创建同名目录投放恶意模块（本地提权）。
            var root = Path.Combine(programData, "DevUp", "runtime");
            Directory.CreateDirectory(root);
            EnsureNotReparsePoint(root);

            var dir = Path.Combine(root, "s-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            EnsureNotReparsePoint(dir);

            LockDown(dir);
            return dir;
        }

        // 普通用户：放在用户目录下，无需管理员，也不会被其他用户读写
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevUp", "runtime");
    }

    /// <summary>拒绝使用被替换为符号链接/联接点的目录（防止普通用户劫持管理员进程）。</summary>
    private static void EnsureNotReparsePoint(string dir)
    {
        try
        {
            var attrs = new DirectoryInfo(dir).Attributes;
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"运行时目录 {dir} 是链接，已拒绝使用。");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"无法校验运行时目录 {dir}：{ex.Message}");
        }
    }

    /// <summary>把目录 ACL 收紧为仅 Administrators / SYSTEM 可写（仅管理员进程可用）。</summary>
    private static void LockDown(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // *S-1-5-18 = SYSTEM，*S-1-5-32-544 = Administrators
            foreach (var a in new[]
                     {
                         dir,
                         "/inheritance:r",
                         "/grant:r", "*S-1-5-18:(OI)(CI)F",
                         "/grant:r", "*S-1-5-32-544:(OI)(CI)F",
                         "/T", "/C", "/Q"
                     })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
        }
        catch (Exception ex)
        {
            Ui.Log("收紧运行时目录权限失败（已按普通权限继续）: " + ex.Message, "WARN");
        }
    }

    /// <summary>释放脚本与内嵌模块；脚本与模块每次都重新写入，避免残留被篡改。</summary>
    public static void EnsureRuntime()
    {
        lock (Gate)
        {
            if (_ready) return;

            var dir = RuntimeDir;
            Directory.CreateDirectory(dir);

            // 释放后端脚本（资源名 devup.ps1 没有目录前缀，需单独处理）
            WriteResource("devup.ps1", Path.Combine(dir, "devup.ps1"));

            // 释放内嵌模块：PSWindowsUpdate / MSCatalog 保留原有目录结构
            foreach (var resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                var slash = resource.IndexOf('/');
                if (slash <= 0) continue;

                var prefix = resource[..slash];
                if (prefix is not ("PSWindowsUpdate" or "MSCatalog")) continue;

                var relative = resource[(slash + 1)..].Replace('\\', '/');
                var target = Path.Combine(dir, prefix, relative.Replace('/', Path.DirectorySeparatorChar));
                WriteResource(resource, target);
            }

            _ready = true;
            Ui.Log($"运行时已就绪: {dir}");
        }
    }

    /// <summary>把一个内嵌资源写到磁盘（覆盖旧文件）。</summary>
    private static void WriteResource(string resourceName, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"内嵌资源缺失: {resourceName}");

        using var fs = File.Create(target);
        stream.CopyTo(fs);
    }

    /// <summary>执行一个后端动作，返回脚本写出的 JSON 根对象。</summary>
    public static async Task<JsonElement> RunAsync(
        string action,
        IDictionary<string, string>? args = null,
        bool elevated = false,
        CancellationToken ct = default)
    {
        if (elevated) return await RunElevatedAsync(action, args, ct);
        return await RunLocalAsync(action, args, ct);
    }

    /// <summary>在当前进程内执行动作（普通权限）。</summary>
    private static async Task<JsonElement> RunLocalAsync(
        string action, IDictionary<string, string>? args, CancellationToken ct)
    {
        EnsureRuntime();

        var outFile = Path.Combine(Path.GetTempPath(), $"devup_{action}_{Guid.NewGuid():N}.json");
        try
        {
            await RunProcessAsync(action, BuildArgs(action, args, outFile), outFile, ct);
            return await ReadResultAsync(outFile, ct);
        }
        finally
        {
            try { File.Delete(outFile); } catch { }
        }
    }

    /// <summary>以管理员身份重新启动自身执行动作（UAC 由系统弹出）。</summary>
    private static async Task<JsonElement> RunElevatedAsync(
        string action, IDictionary<string, string>? args, CancellationToken ct)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("无法定位当前可执行文件路径。");

        var outFile = Path.Combine(Path.GetTempPath(), $"devup_{action}_{Guid.NewGuid():N}.json");
        var argsFile = Path.Combine(Path.GetTempPath(), $"devup_args_{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(argsFile, JsonSerializer.Serialize(args ?? new Dictionary<string, string>()),
                Encoding.UTF8, ct);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true, // 必须配合 Verb 才能触发 UAC
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            psi.Arguments =
                $"{ElevatedActionArg} {action} " +
                $"{ElevatedArgsArg} \"{argsFile}\" " +
                $"{ElevatedOutArg} \"{outFile}\"";

            Process proc;
            try
            {
                proc = Process.Start(psi)
                       ?? throw new InvalidOperationException("无法启动提权子进程。");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = ERROR_CANCELLED：用户在 UAC 对话框点了“否”
                throw new UserCancelledException("已取消管理员权限提升，未执行该操作。");
            }

            using (proc)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));

                try
                {
                    await proc.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new InvalidOperationException($"提权动作 '{action}' 超过 10 分钟未完成，已终止。");
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw;
                }
            }

            if (!File.Exists(outFile))
            {
                throw new InvalidOperationException(
                    $"提权进程未返回结果（退出码 {proc.ExitCode}）。请查看日志了解详情。");
            }

            return await ReadResultAsync(outFile, ct);
        }
        finally
        {
            try { File.Delete(outFile); } catch { }
            try { File.Delete(argsFile); } catch { }
        }
    }

    private static async Task<JsonElement> ReadResultAsync(string outFile, CancellationToken ct)
    {
        if (!File.Exists(outFile))
            throw new InvalidOperationException("后端动作未产生结果文件。");

        var json = await File.ReadAllTextAsync(outFile, Encoding.UTF8, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();

        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : "未知错误";
            throw new InvalidOperationException(err ?? "后端执行失败");
        }
        return root;
    }

    private static List<string> BuildArgs(string action, IDictionary<string, string>? args, string outFile)
    {
        var list = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", ScriptPath,
            "-Action", action,
            "-OutFile", outFile,
            "-ModulePath", Path.Combine(RuntimeDir, "PSWindowsUpdate", "PSWindowsUpdate.psd1"),
            "-CatalogModulePath", Path.Combine(RuntimeDir, "MSCatalog", "MSCatalog.psd1")
        };
        if (args is null) return list;

        foreach (var kv in args)
        {
            if (string.Equals(kv.Key, "ModulePath", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "CatalogModulePath", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add("-" + kv.Key);
            list.Add(kv.Value ?? "");
        }
        return list;
    }

    private static string ResolvePowerShell()
    {
        // 不依赖 PATH：部分用户的环境变量被改坏后，powershell.exe 会找不到，
        // 导致所有驱动/软件扫描直接失败。
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidate = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }

    private static async Task RunProcessAsync(string action, List<string> args, string outFile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePowerShell(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 脚本运行后把控制台编码设为 UTF-8，因此 stdout 按 UTF-8 解码；
            // stderr 承载的是 PowerShell 自身的诊断（此时脚本尚未执行，输出走系统 ANSI 代码页）。
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
        await proc.WaitForExitAsync(ct);

        var log = sb.ToString().Trim();
        if (proc.ExitCode != 0 && !File.Exists(outFile))
            throw new InvalidOperationException($"PowerShell 退出码 {proc.ExitCode}: {Truncate(log, 800)}");
        if (!string.IsNullOrWhiteSpace(log)) Ui.Log($"[{action}] {Truncate(log, 400)}", "DEBUG");
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + " …");
}
