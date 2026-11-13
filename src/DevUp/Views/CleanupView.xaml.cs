using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

namespace DevUp.Views;

public partial class CleanupView : UserControl
{
    private readonly CleanupService _service;
    private readonly ObservableCollection<CleanupRow> _rows = new();
    private CleanupScan? _scan;
    private CancellationTokenSource? _cancellation;
    private int _generation;
    private long _targetBytes;
    private double _targetPercent;

    private static readonly DependencyProperty DisplayBytesProperty = DependencyProperty.Register(
        nameof(DisplayBytes), typeof(double), typeof(CleanupView),
        new PropertyMetadata(0d, (owner, args) =>
            ((CleanupView)owner).TotalText.Text = CleanupSize.Format((long)(double)args.NewValue)));

    private double DisplayBytes
    {
        get => (double)GetValue(DisplayBytesProperty);
        set => SetValue(DisplayBytesProperty, value);
    }

    public bool IsRunning => _cancellation is not null;

    public CleanupView()
        : this(new CleanupService()) { }

    internal CleanupView(CleanupService service)
    {
        _service = service;
        InitializeComponent();
        foreach (var category in _service.Categories)
        {
            var row = new CleanupRow(category);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CleanupRow.Selected)) UpdateSelection();
            };
            _rows.Add(row);
        }
        CategoryGrid.ItemsSource = _rows;
        IsVisibleChanged += (_, _) => UpdateActivityAnimation();
        Unloaded += (_, _) =>
        {
            Ctx.State.PropertyChanged -= OnAppStateChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemSettingsChanged;
            StopAnimations();
        };
        Loaded += (_, _) =>
        {
            Ctx.State.PropertyChanged += OnAppStateChanged;
            SystemParameters.StaticPropertyChanged += OnSystemSettingsChanged;
            UpdateButtons();
            UpdateActivityAnimation();
        };
    }

    /// <summary>请求取消；清理保留已完成部分的统计。</summary>
    public void Cancel()
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消…";
    }

    private async void Scan_Click(object sender, RoutedEventArgs args)
    {
        await RunAsync(false);
    }

    private async void Clean_Click(object sender, RoutedEventArgs args)
    {
        if (Ctx.State.IsBusy || _scan is null) return;
        var selected = _rows.Where(row => row.Selected).ToArray();
        var count = selected.Sum(row => row.Files.Count);
        if (count == 0) return;
        if (!Ui.Confirm("清理选中的垃圾文件",
                $"将永久删除 {count:N0} 个文件，合计 {CleanupSize.Format(selected.Sum(row => row.Bytes))}。\n\n" +
                "请先保存工作并退出相关软件。诊断资料与缓存的清理说明可在右侧查看。\n" +
                "文件不会放入回收站；取消只会停止后续删除。是否继续？")) return;
        await RunAsync(true);
    }

    private async Task RunAsync(bool cleaning)
    {
        if (Ctx.State.IsBusy) return;
        await Ctx.RunAsync(cleaning ? "正在清理垃圾文件…" : "正在扫描垃圾文件…", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var generation = ++_generation;
            CleanupOutcome? outcome = null;
            try
            {
                CancelButton.Content = "取消";
                CancelButton.IsEnabled = true;
                SummaryLabel.Text = cleaning ? "已清理的文件大小" : "已发现的可清理文件";
                CountText.Text = "正在准备…";
                AnimateBytes(0);
                AnimateProgress(0);
                if (!cleaning)
                {
                    _scan = null;
                    var categories = _service.Categories;
                    for (var index = 0; index < _rows.Count; index++)
                    {
                        var row = _rows[index];
                        row.SetResult(categories[index]);
                        row.Status = "等待扫描";
                    }
                    UpdateDetails();
                }
                UpdateButtons();
                UpdateActivityAnimation();
                var progress = new Progress<CleanupProgress>(update =>
                {
                    if (generation != _generation || !IsRunning) return;
                    ActivityText.Text = $"{(cleaning ? "正在清理" : "正在扫描")} · {update.Category}";
                    ActivityText.ToolTip = update.Detail;
                    CountText.Text = cleaning ? $"已删除 {update.Files:N0} 个文件"
                        : $"已发现 {update.Files:N0} 个文件 · 已检查 {update.Completed}/{update.Total} 类";
                    AnimateBytes(update.Bytes);
                    AnimateProgress(update.Total > 0 ? update.Completed * 100d / update.Total : 0);
                    var row = _rows.FirstOrDefault(item => item.Name == update.Category);
                    if (row is not null)
                    {
                        foreach (var previous in _rows.Where(item => item.IsActive && !ReferenceEquals(item, row)))
                            previous.Status = cleaning ? "已处理" : "扫描完成";
                        row.Status = cleaning ? "清理中" : "扫描中";
                        if (update.Result is not null) row.SetResult(update.Result);
                    }
                    if (ReferenceEquals(CategoryGrid.SelectedItem, row)) UpdateDetails();
                });
                if (cleaning)
                {
                    var scan = _scan ?? throw new InvalidOperationException("请先扫描。");
                    _scan = null;
                    outcome = await _service.CleanAsync(scan,
                        _rows.Where(row => row.Selected).Select(row => row.Name).ToHashSet(), progress, cancellation.Token);
                    AnimateBytes(outcome.Bytes);
                    CountText.Text = $"已删除 {outcome.Deleted:N0} 个文件 · 跳过 {outcome.Skipped:N0} 个";
                    ActivityText.Text = outcome.Cancelled ? "清理已取消，已删除的文件无法恢复" : "清理完成";
                    FooterText.Text = $"{ActivityText.Text}。请重新扫描以刷新剩余文件。";
                    if (outcome.Errors.Count > 0)
                    {
                        FooterText.Text += " 部分文件被占用、发生变化或权限不足，已安全跳过。";
                        foreach (var error in outcome.Errors) Ui.Log("清理跳过：" + error, "WARN");
                    }
                    foreach (var row in _rows) row.Status = "需重新扫描";
                    FileGrid.ItemsSource = null;
                    FileHint.Text = "此列表为清理前的扫描记录，请重新扫描查看当前文件。";
                    Ui.Log($"清理{(outcome.Cancelled ? "取消" : "完成")}：删除 {outcome.Deleted} 个，" +
                        $"{outcome.Bytes} 字节；跳过 {outcome.Skipped} 个。");
                }
                else
                {
                    _scan = await _service.ScanAsync(progress, cancellation.Token);
                    foreach (var category in _scan.Categories)
                        _rows.First(row => row.Name == category.Name).SetResult(category);
                    AnimateBytes(_scan.Bytes);
                    CountText.Text = $"共 {_scan.FileCount:N0} 个文件 · {_scan.Categories.Count} 类项目";
                    ActivityText.Text = _scan.FileCount > 0 ? "扫描完成，选择需要清理的项目" : "扫描完成，未发现可清理文件";
                    FooterText.Text = "扫描已完成。退出提示中正在运行的软件后重扫，可检查更多缓存；被占用的文件会在清理时跳过。";
                    CategoryGrid.SelectedItem ??= _rows.FirstOrDefault(row => row.Files.Count > 0) ?? _rows.First();
                    UpdateDetails();
                }
                if (outcome?.Cancelled != true) AnimateProgress(100);
                if (outcome?.Cancelled != true) PulseCompletion();
            }
            catch (OperationCanceledException)
            {
                _scan = null;
                ActivityText.Text = "扫描已取消，没有删除文件";
                FooterText.Text = "扫描未完成，重新扫描后才可清理。";
                foreach (var row in _rows) row.Status = "扫描未完成";
            }
            catch
            {
                _scan = null;
                ActivityText.Text = cleaning ? "清理中断，请重新扫描" : "扫描失败，请重试";
                FooterText.Text = cleaning ? "部分文件可能已删除，请重新扫描查看剩余内容。" : "本次没有删除文件。";
                throw;
            }
            finally
            {
                _generation++;
                _cancellation = null;
                StopActivityAnimation();
                UpdateButtons();
                UpdateSelection();
            }
        });
        Ctx.State.StatusText = ActivityText.Text;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ScanButton.IsEnabled = !Ctx.State.IsBusy;
        ScanButton.Content = _scan is null ? "开始扫描" : "重新扫描";
        CleanButton.IsEnabled = !Ctx.State.IsBusy && _scan is not null &&
            _rows.Any(row => row.Selected && row.Files.Count > 0);
        CategoryGrid.IsEnabled = !IsRunning;
        SelectionButtons.IsEnabled = !Ctx.State.IsBusy;
        CancelButton.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSelection()
    {
        var selected = _rows.Where(row => row.Selected).ToArray();
        SelectionText.Text = _scan is null ? "请完成扫描后再清理"
            : $"已选 {selected.Length} 类 · {selected.Sum(row => row.Files.Count):N0} 个文件 · " +
              CleanupSize.Format(selected.Sum(row => row.Bytes));
        UpdateButtons();
    }

    private void UpdateDetails()
    {
        if (CategoryGrid.SelectedItem is not CleanupRow row) return;
        DetailTitle.Text = row.Name;
        DetailDescription.Text = row.Description;
        DetailNote.Text = row.Status;
        DetailNote.Foreground = (System.Windows.Media.Brush)FindResource(
            row.Status.Contains("跳过") || row.Status.Contains("退出") ? "WarnBrush" : "SubTextBrush");
        FileGrid.ItemsSource = _scan is not null || IsRunning ? row.Files : null;
        FileHint.Text = _scan is not null ? "大小为文件逻辑大小；占用及变化的文件会在清理时跳过。"
            : "完成扫描后显示文件明细。";
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs args) => UpdateDetails();
    private void Cancel_Click(object sender, RoutedEventArgs args) => Cancel();
    private void SelectAll_Click(object sender, RoutedEventArgs args) { foreach (var row in _rows) row.Selected = true; }
    private void SelectNone_Click(object sender, RoutedEventArgs args) { foreach (var row in _rows) row.Selected = false; }

    private void AnimateBytes(long bytes)
    {
        _targetBytes = bytes;
        if (SystemParameters.ClientAreaAnimation && IsVisible)
            BeginAnimation(DisplayBytesProperty, new DoubleAnimation(DisplayBytes, bytes,
                TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() });
        else { BeginAnimation(DisplayBytesProperty, null); DisplayBytes = bytes; }
    }

    private void AnimateProgress(double percent)
    {
        _targetPercent = percent;
        var start = WorkProgress.Value;
        WorkProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        WorkProgress.Value = percent;
        if (SystemParameters.ClientAreaAnimation && IsVisible)
            WorkProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(start, percent, TimeSpan.FromMilliseconds(180)));
    }

    private void UpdateActivityAnimation()
    {
        StopActivityAnimation();
        if (!IsRunning || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
        OrbitRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
        Sweep.Visibility = Visibility.Visible;
        SweepPosition.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(-90, Math.Max(200, ActualWidth), TimeSpan.FromSeconds(2))
            { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopActivityAnimation()
    {
        OrbitRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        SweepPosition.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        Sweep.Visibility = Visibility.Collapsed;
    }

    private void PulseCompletion()
    {
        if (SystemParameters.ClientAreaAnimation && IsVisible)
            SummaryCard.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(380)));
    }

    private void OnSystemSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        Dispatcher.Invoke(() =>
        {
            StopAnimations();
            UpdateActivityAnimation();
        });
    }

    private void OnAppStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Models.ScanState.IsBusy)) UpdateButtons();
    }

    private void StopAnimations()
    {
        StopActivityAnimation();
        SummaryCard.BeginAnimation(OpacityProperty, null);
        BeginAnimation(DisplayBytesProperty, null);
        DisplayBytes = _targetBytes;
        WorkProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        WorkProgress.Value = _targetPercent;
    }
}
