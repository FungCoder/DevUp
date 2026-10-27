using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DevUp.Core;

namespace DevUp.Views;

/// <summary>统一的进度窗口：支持确定/不确定进度、速度与剩余信息、可取消。</summary>
public partial class ProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DateTime _started = DateTime.Now;
    private Func<IProgress<ProgressInfo>, CancellationToken, Task>? _work;
    private Exception? _error;
    private bool _indeterminate;
    private double _percent;
    private bool _finished;

    public ProgressDialog()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && BtnCancel.IsEnabled) Cancel_Click(this, new RoutedEventArgs());
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (e.OriginalSource is System.Windows.Controls.Button) return;
            try { DragMove(); } catch { }
        };
        SizeChanged += (_, _) => ApplyPercent();
        _timer.Tick += (_, _) => TickElapsed();
        Closed += (_, _) => _timer.Stop();

        // Alt+F4 / 标题栏关闭：先取消任务，等任务真正收尾再关窗，
        // 避免后台任务继续跑、往已关闭窗口写 DialogResult。
        Closing += OnClosing;
    }

    /// <summary>显示进度窗口并执行 work；返回 false 表示用户取消。</summary>
    public static bool Run(string title, string status,
        Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
    {
        var dlg = new ProgressDialog { Owner = Application.Current?.MainWindow };
        if (dlg.Owner is null || !dlg.Owner.IsVisible)
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.TitleText.Text = title;
        dlg.StatusText.Text = status;
        dlg._work = work;
        dlg.Loaded += async (_, _) => await dlg.ExecuteAsync();

        var closed = dlg.ShowDialog();
        if (dlg._error is not null) throw dlg._error;
        return closed == true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished) return;

        // 第一次关闭：取消任务并拦截关闭窗口
        e.Cancel = true;
        RequestCancel("已取消");
    }

    private async Task ExecuteAsync()
    {
        var progress = new Progress<ProgressInfo>(Update);
        try
        {
            if (_work is not null) await _work(progress, _cts.Token);
            Finish(true);
        }
        catch (OperationCanceledException)
        {
            Finish(false);
        }
        catch (UserCancelledException)
        {
            // 用户在 UAC 提示里点了“否”：按取消处理，不当作错误弹窗
            Finish(false);
        }
        catch (Exception ex)
        {
            _error = ex;
            Finish(false);
        }
    }

    /// <summary>结束对话框：先标记完成再设置结果，避免 Closing 拦截。</summary>
    private void Finish(bool result)
    {
        _finished = true;
        _timer.Stop();
        try
        {
            if (IsVisible) DialogResult = result;
            else Close();
        }
        catch (InvalidOperationException)
        {
            // 窗口已被关闭：忽略
        }
    }

    private void Update(ProgressInfo info)
    {
        if (info.Status is not null && StatusText.Text != info.Status) StatusText.Text = info.Status;

        _indeterminate = info.Percent < 0;
        if (_indeterminate)
        {
            BarFill.Visibility = Visibility.Collapsed;
            IndeterminateBar.Visibility = Visibility.Visible;
            if (PercentText.Text.Length > 0) PercentText.Text = "";
            ElapsedText.Visibility = Visibility.Visible;
            TickElapsed();
            _timer.Start();
        }
        else
        {
            IndeterminateBar.Visibility = Visibility.Collapsed;
            BarFill.Visibility = Visibility.Visible;

            var pct = Math.Clamp(info.Percent, 0, 100);
            _percent = pct;
            ApplyPercent();
            var pctText = $"{pct:0}%";
            if (PercentText.Text != pctText) PercentText.Text = pctText;
            ElapsedText.Visibility = Visibility.Collapsed;
            _timer.Stop();
        }

        var detail = info.Detail ?? "";
        if (DetailText.Text != detail) DetailText.Text = detail;
        DetailText.ToolTip = info.DetailHint ?? detail;

        var hasMetric = !string.IsNullOrEmpty(info.Metric);
        MetricText.Text = info.Metric ?? "";
        MetricText.ToolTip = info.Metric;
        MetricText.Visibility = hasMetric ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = hasMetric ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        MetricColumn.Width = hasMetric ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        DetailText.MaxWidth = hasMetric ? 140 : double.PositiveInfinity;

        BtnCancel.IsEnabled = info.CanCancel && !_cts.IsCancellationRequested;
        var vis = info.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        if (BtnCancel.Visibility != vis) BtnCancel.Visibility = vis;
    }

    private void ApplyPercent()
    {
        if (_indeterminate) return;
        BarFill.Width = Math.Max(0, BarTrack.ActualWidth * _percent / 100.0);
    }

    /// <summary>不确定进度时在右侧显示“已用时间”（固定宽度格式），让用户知道程序没卡死。</summary>
    private void TickElapsed()
    {
        if (!_indeterminate) return;
        var sec = (int)(DateTime.Now - _started).TotalSeconds;
        var text = sec >= 3600
            ? $"已用 {sec / 3600}:{sec / 60 % 60:00}:{sec % 60:00}"
            : $"已用 {sec / 60:00}:{sec % 60:00}";
        if (ElapsedText.Text != text) ElapsedText.Text = text;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RequestCancel("正在取消…");
    }

    /// <summary>请求取消：停掉后台任务，任务收尾后由 Finish 关闭窗口。</summary>
    private void RequestCancel(string caption)
    {
        if (_cts.IsCancellationRequested) return;
        BtnCancel.IsEnabled = false;
        BtnCancel.Content = caption;
        try { _cts.Cancel(); } catch { }
    }
}
