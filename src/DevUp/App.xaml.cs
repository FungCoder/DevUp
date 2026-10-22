using System.Windows;
using DevUp.Core;
using DevUp.Services;
using DevUp.Views;

namespace DevUp;

public partial class App : Application
{
    /// <summary>启动时指定的首个页面（dashboard/drivers/software/tools）。</summary>
    public static string? StartupPage { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自提升子进程（驱动安装 / 还原点）走无界面模式
        if (CommandLine.TryHandleElevatedRun(e.Args))
        {
            // 结果文件已写好，直接硬退出：WPF 的 Shutdown 在无窗口场景下偶尔会卡住，
            // 会让父进程一直等待、界面永远停在“正在扫描”。
            Environment.Exit(0);
            return;
        }

        var pageIndex = Array.FindIndex(e.Args, a => a == "--page");
        if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length)
            StartupPage = e.Args[pageIndex + 1].Trim().ToLowerInvariant();

        DispatcherUnhandledException += (_, args) =>
        {
            Ui.Log("未处理异常: " + args.Exception, "ERROR");
            Ui.Error("发生未处理的异常", args.Exception.Message + "\n\n" + FirstLines(args.Exception.ToString(), 6));
            args.Handled = true;
        };

        // 启动权限选择：需要重启为管理员时本进程会退出
        if (ResolveStartupPermission(e.Args)) return;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 清理本次运行创建的临时运行时目录
        try { PsBridge.CleanupRuntime(); } catch { }
        base.OnExit(e);
    }

    /// <summary>
    /// 按设置决定启动方式。返回 true 表示本进程已结束（用户退出或已启动提权副本）。
    /// </summary>
    private bool ResolveStartupPermission(string[] args)
    {
        var alreadyElevated = Native.IsAdministrator();
        var launchedElevated = args.Any(a => string.Equals(a, "--elevated-main", StringComparison.OrdinalIgnoreCase));
        if (alreadyElevated || launchedElevated) return false;

        var mode = (Ctx.Settings.StartupMode ?? AppSettings.ModeAsk).Trim().ToLowerInvariant();

        if (mode == AppSettings.ModeAdmin)
            return TryRestartElevated(args);

        if (mode == AppSettings.ModeNormal) return false;

        // 默认：每次询问。先关闭“最后一个窗口即退出”，避免选择框关闭时整个程序被结束。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var dialog = new StartupDialog();
        if (dialog.ShowDialog() != true)
        {
            Shutdown();
            return true;
        }

        if (dialog.Remember)
        {
            Ctx.Settings.StartupMode = dialog.RunAsAdmin ? AppSettings.ModeAdmin : AppSettings.ModeNormal;
            Ctx.Settings.Save();
        }

        if (!dialog.RunAsAdmin) return false;

        var restarted = TryRestartElevated(args);
        if (restarted) return true;

        // 用户取消了 UAC：回落到普通权限启动，不打断使用
        return false;
    }

    /// <summary>以管理员身份重新启动自身（保留 --page 等参数）。</summary>
    private static bool TryRestartElevated(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath ?? "DevUp.exe";
            var forwarded = args
                .Where(a => !string.Equals(a, "--elevated-main", StringComparison.OrdinalIgnoreCase))
                .Select(Quote);
            var argLine = string.Join(" ", new[] { "--elevated-main" }.Concat(forwarded));

            if (!Native.StartElevated(exe, argLine))
            {
                Ui.Log("用户在 UAC 提示中选择了“否”，将以普通权限继续运行。", "WARN");
                return false;
            }

            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            Ui.Log("启动提权被取消或失败，将以普通权限继续: " + ex.Message, "WARN");
            return false;
        }
    }

    private static string Quote(string arg)
        => arg.Contains(' ') && !arg.StartsWith('"') ? "\"" + arg + "\"" : arg;

    private static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n');
        return string.Join("\n", lines.Take(count));
    }
}
