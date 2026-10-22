using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace DevUp.Core;

/// <summary>日志、弹窗与状态提示。</summary>
public static class Ui
{
    /// <summary>
    /// 日志目录放在用户目录下：普通用户没有 %ProgramData%\DevUp 的写权限，
    /// 写到那里会静默失败，导致用户反馈问题时拿不到日志。
    /// </summary>
    public static string LogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevUp", "logs");

    public static event Action<string, string>? Logged;   // (level, message)

    public static void Log(string message, string level = "INFO")
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            File.AppendAllText(Path.Combine(LogDir, "devup.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* 日志失败不影响主流程 */ }
        Logged?.Invoke(level, message);
    }

    public static void Info(string title, string message) => Show(title, message, Views.DialogKind.Info);
    public static void Success(string title, string message) => Show(title, message, Views.DialogKind.Success);
    public static void Warn(string title, string message) => Show(title, message, Views.DialogKind.Warn);
    public static void Error(string title, string message) => Show(title, message, Views.DialogKind.Error);

    private static void Show(string title, string message, Views.DialogKind kind)
    {
        Log($"{title}: {message}", kind == Views.DialogKind.Error ? "ERROR" : "INFO");
        OnUi(() => Views.MessageDialog.Show(title, message, kind, confirm: false));
    }

    public static bool Confirm(string title, string message)
    {
        Log($"{title}: {message}", "ASK");
        var ok = false;
        OnUi(() => ok = Views.MessageDialog.Show(title, message, Views.DialogKind.Question, confirm: true));
        return ok;
    }

    /// <summary>三选一弹窗：主按钮 / 次按钮 / 取消。</summary>
    public static Views.DialogChoice Ask(string title, string message,
        string okText, string extraText, string cancelText = "关闭")
    {
        Log($"{title}: {message}", "ASK");
        var choice = Views.DialogChoice.Cancel;
        OnUi(() => choice = Views.MessageDialog.ShowChoice(
            title, message, Views.DialogKind.Question, okText, extraText, cancelText));
        return choice;
    }

    /// <summary>在 UI 线程上同步执行（弹窗必须由 UI 线程创建）。</summary>
    private static void OnUi(Action action)
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            Log("弹窗失败: " + ex.Message, "ERROR");
        }
    }

    /// <summary>写入一条状态消息（不弹窗）。</summary>
    public static void Toast(string message)
    {
        Log(message);
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Error("无法打开链接", "链接格式不正确：" + url);
                return;
            }
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Error("无法打开链接", ex.Message); }
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // 用 Unicode 转义避免引号转义问题
                var quoted = "\u0022" + path + "\u0022";
                Process.Start(new ProcessStartInfo("explorer.exe", quoted) { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) { Error("无法打开", ex.Message); }
    }
}
