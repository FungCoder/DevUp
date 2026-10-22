using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DevUp.Core;

/// <summary>命令行入口解析（含提权子进程模式）。</summary>
public static class CommandLine
{
    /// <summary>
    /// 若以提权子进程方式启动（--elevated-action），执行对应后端动作并写结果文件，返回 true。
    /// </summary>
    public static bool TryHandleElevatedRun(string[] args)
    {
        // 以管理员身份重新启动主界面（不做动作处理）
        if (args.Length >= 1 && args[0] == "--elevated-main") return false;

        if (args.Length < 2 || args[0] != "--elevated-action") return false;

        var action = args[1];
        var argsFile = "";
        var outFile = "";

        // 新格式：--elevated-action <action> --elevated-args <file> --elevated-out <file>
        for (var i = 2; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--elevated-args", StringComparison.OrdinalIgnoreCase))
                argsFile = args[i + 1];
            else if (string.Equals(args[i], "--elevated-out", StringComparison.OrdinalIgnoreCase))
                outFile = args[i + 1];
        }

        // 兼容旧格式：--elevated-action <action> <argsFile> <outFile>
        if (string.IsNullOrWhiteSpace(outFile) && args.Length >= 4)
        {
            argsFile = args[2];
            outFile = args[3];
        }

        if (string.IsNullOrWhiteSpace(outFile)) return false;

        try
        {
            Dictionary<string, string> extra = new();
            if (File.Exists(argsFile))
            {
                var json = File.ReadAllText(argsFile);
                if (!string.IsNullOrWhiteSpace(json))
                    extra = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }

            // 提权子进程没有消息循环，不能在 UI 线程上同步等待异步方法：
            // 那样会捕获 WPF 的 SynchronizationContext 并永久挂起（进程不退出）。
            // 放到线程池执行，避免死锁。
            var result = Task.Run(() => PsBridge.RunAsync(action, extra, elevated: false)).GetAwaiter().GetResult();
            File.WriteAllText(outFile, result.GetRawText(), new System.Text.UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    ok = false,
                    action,
                    error = ex.Message,
                    data = (object?)null
                });
                File.WriteAllText(outFile, payload, new System.Text.UTF8Encoding(false));
            }
            catch { }
            Ui.Log($"提权动作 {action} 失败: {ex.Message}", "ERROR");
            return true;
        }
        finally
        {
            // 提权子进程使用独立的临时运行时目录，用完即删
            try { PsBridge.CleanupRuntime(); } catch { }
        }
    }
}
