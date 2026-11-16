using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

namespace DevUp.Views;

public partial class StartupView : UserControl
{
    private readonly StartupService _service = new();
    private List<StartupEntry> _entries = [];

    public StartupView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Ctx.State.PropertyChanged += State_Changed; UpdateButtons(); };
        Unloaded += (_, _) => Ctx.State.PropertyChanged -= State_Changed;
    }

    private void State_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Models.ScanState.IsBusy)) UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = EntryGrid.SelectedItems.Cast<StartupEntry>().ToArray();
        var idle = !Ctx.State.IsBusy;
        ScanButton.IsEnabled = idle;
        DisableButton.IsEnabled = idle && selected.Any(entry => entry.CanDisable);
        RestoreButton.IsEnabled = idle && selected.Any(entry => entry.CanRestore);
        RelatedButton.IsEnabled = idle && selected.Length == 1 && selected[0].CanGroup;
        EntryGrid.IsEnabled = idle;
    }

    private async Task RefreshAsync()
    {
        var scan = await _service.ScanAsync();
        _entries = scan.Entries;
        SummaryText.Text = $"{_entries.Count} 个入口 · 已启用 {_entries.Count(entry => entry.Enabled == true)} · " +
            $"已禁用 {_entries.Count(entry => entry.Enabled == false)} · 无法确认 {_entries.Count(entry => entry.Enabled is null)}";
        WarningsPanel.Visibility = scan.Warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        WarningsText.Text = string.Join("\n", scan.Warnings);
        Filter();
    }

    private async void Scan_Click(object sender, RoutedEventArgs args)
        => await Ctx.RunAsync("正在扫描自启动入口…", RefreshAsync);

    private void Filter_Changed(object sender, RoutedEventArgs args)
    {
        if (EntryGrid is not null) Filter();
    }

    private void Filter()
    {
        var source = (SourceFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "apps";
        var text = SearchBox.Text.Trim();
        EntryGrid.ItemsSource = _entries.Where(entry =>
            (ShowSystem.IsChecked == true || !entry.Protected) &&
            (source == "all" || (entry.Source == StartupSource.Service) == (source == "services")) &&
            (text.Length == 0 || entry.Details.Contains(text, StringComparison.CurrentCultureIgnoreCase))).ToList();
        UpdateButtons();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs args)
    {
        DetailsText.Text = (EntryGrid.SelectedItem as StartupEntry)?.Details ?? "选择入口查看来源、命令与影响。";
        UpdateButtons();
    }

    private async void Disable_Click(object sender, RoutedEventArgs args)
        => await ChangeAsync(EntryGrid.SelectedItems.Cast<StartupEntry>().Where(entry => entry.CanDisable).ToList(), false);

    private async void Restore_Click(object sender, RoutedEventArgs args)
        => await ChangeAsync(EntryGrid.SelectedItems.Cast<StartupEntry>().Where(entry => entry.CanRestore).ToList(), true);

    private async void Related_Click(object sender, RoutedEventArgs args)
    {
        if (EntryGrid.SelectedItem is not StartupEntry selected || !selected.CanGroup) return;
        // 只关联同一个确切可执行文件，不按相似名称或共享安装目录猜测归属。
        var related = _entries.Where(entry => entry.GroupKey == selected.GroupKey && entry.CanDisable).ToList();
        await ChangeAsync(related, false);
    }

    private async Task ChangeAsync(List<StartupEntry> entries, bool enable)
    {
        if (Ctx.State.IsBusy || entries.Count == 0) return;
        var verb = enable ? "恢复" : "禁用";
        var details = string.Join("\n", entries.Select(entry => $"{entry.Name} · {entry.SourceText} · {entry.ScopeText}"));
        var services = entries.Any(entry => entry.Source == StartupSource.Service);
        if (!Ui.Confirm(verb + "自启动入口", $"将{verb}以下 {entries.Count} 个入口：\n\n{details}\n\n" +
                (services ? "包含服务，禁用后可能影响软件功能。\n" : "") +
                "会修改真实系统配置，并重新扫描验证。是否继续？")) return;
        await Ctx.RunAsync($"正在{verb}自启动入口…", async () =>
        {
            List<StartupChangeResult>? results = null;
            try
            {
                results = await _service.ChangeAsync(entries, enable);
            }
            finally { await RefreshAsync(); }
            if (results is not null)
            {
                var failures = results.Where(result => !result.Success).ToList();
                ResultText.Text = $"{verb}结果：成功 {results.Count - failures.Count} 项，失败 {failures.Count} 项。" +
                    (failures.Count > 0 ? " 失败明细见右侧。" : " 系统配置复核通过；重启后的实际启动情况待验证。");
                DetailsText.Text = string.Join("\n\n", results.Select(result => $"{result.Name}\n{result.Message}"));
            }
        });
    }
}
