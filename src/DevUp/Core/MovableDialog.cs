using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace DevUp.Core;

/// <summary>等待弹窗关闭期间只锁定页面内容，保留其他窗口的标题栏操作。</summary>
internal static class MovableDialog
{
    public static void Show(Window dialog)
    {
        dialog.Dispatcher.VerifyAccess();
        var frame = new DispatcherFrame();
        var blocked = new List<(Window Window, UIElement? Content, bool WasEnabled)>();
        CancelEventHandler preventClose = (_, e) => e.Cancel = true;
        EventHandler onClosed = (_, _) => frame.Continue = false;
        dialog.Closed += onClosed;

        try
        {
            // 包括已有进度窗，保证嵌套提示关闭后仍由外层弹窗控制页面。
            var windows = Application.Current.Windows.OfType<Window>()
                .Where(window => window.IsVisible && !ReferenceEquals(window, dialog))
                .ToArray();
            foreach (var window in windows)
            {
                var content = window.Content as UIElement;
                blocked.Add((window, content, content?.IsEnabled ?? false));
                content?.SetCurrentValue(UIElement.IsEnabledProperty, false);
                // 页面被锁定时，防止关闭主窗口导致正在执行的任务被中断。
                window.Closing += preventClose;
            }

            dialog.Show();
            if (frame.Continue) Dispatcher.PushFrame(frame);
        }
        finally
        {
            dialog.Closed -= onClosed;
            foreach (var (window, content, wasEnabled) in blocked)
            {
                window.Closing -= preventClose;
                content?.SetCurrentValue(UIElement.IsEnabledProperty, wasEnabled);
            }
        }
    }
}
