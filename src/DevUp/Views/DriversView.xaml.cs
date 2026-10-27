using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

namespace DevUp.Views;

public partial class DriversView : UserControl
{
    public DriversView()
    {
        InitializeComponent();
        Loaded += (_, _) => { ApplyFilter(); ApplyUpdateFilter(); };
        // 首次扫描是异步的：设备集合变化后要刷新计数，否则会一直停在 0 / 0
        Ctx.State.Devices.CollectionChanged += (_, _) => ApplyFilter();
        // 扫描会重建驱动更新列表：重新套用“显示已忽略 / 隐藏已忽略”的筛选
        Ctx.State.DriverUpdates.CollectionChanged += (_, _) => ApplyUpdateFilter();
        UpdateHint.Text = "";
    }

    // ---------------- 设备列表 ----------------

    private void ApplyFilter()
    {
        // XAML 加载期间 ComboBox 的 SelectionChanged 可能先于字段赋值触发
        if (CmbFilter is null || DeviceCountText is null) return;

        var view = CollectionViewSource.GetDefaultView(Ctx.State.Devices);
        if (view is null) return;

        var filter = (CmbFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        view.Filter = o =>
        {
            if (o is not DeviceItem d) return false;
            return filter switch
            {
                "problem" => d.HasProblem && d.IsPresent && !d.Ignored,
                "driver" => d.NeedsDriver && d.IsPresent && !d.Ignored,
                "absent" => !d.IsPresent,
                "ignored" => d.Ignored,
                _ => !d.Ignored
            };
        };
        view.Refresh();
        DeviceCountText.Text = $"{view.Cast<object>().Count()} / {Ctx.State.Devices.Count}";
    }

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    /// <summary>供概览页跳转使用：切到“仅异常设备”视图。</summary>
    public void ShowProblemDevices() => SelectFilter("problem");

    /// <summary>供概览页跳转使用：恢复“全部设备”。</summary>
    public void ShowAllDevices() => SelectFilter("all");

    private void SelectFilter(string tag)
    {
        foreach (var item in CmbFilter.Items)
        {
            if (item is ComboBoxItem c && (c.Tag as string) == tag)
            {
                CmbFilter.SelectedItem = c;
                break;
            }
        }
        ApplyFilter();
    }

    private void DeviceGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 窗口未激活时，第一次点击常被系统吞掉（只激活窗口）。
        // 这里显式命中行并赋值，保证“点一下就选中”符合直觉。
        var row = ItemsControl.ContainerFromElement(DeviceGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row is not null && !row.IsSelected) DeviceGrid.SelectedItem = row.Item;
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is not DeviceItem device)
        {
            ResetDetail();
            return;
        }
        ShowDetail(device);
    }

    private static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void ResetDetail()
    {
        DetailBadge.Visibility = Visibility.Collapsed;
        DetailPanel.Children.Clear();
        DetailEmpty.Visibility = Visibility.Visible;
        DetailPanel.Children.Add(DetailEmpty);
    }

    private void ShowDetail(DeviceItem device)
    {
        DetailPanel.Children.Clear();

        // 右上角状态标签
        DetailBadge.Visibility = Visibility.Visible;
        DetailBadgeText.Text = device.Ignored ? device.ProblemText + " · 已忽略" : device.ProblemText;
        if (device.Ignored)
        {
            DetailBadge.Background = B("#F1F3F7");
            DetailBadgeText.Foreground = B("#7B8794");
        }
        else if (!device.HasProblem)
        {
            DetailBadge.Background = B("#EAF8EF");
            DetailBadgeText.Foreground = B("#15803D");
        }
        else if (device.ProblemCode == 45)
        {
            DetailBadge.Background = B("#F1F3F7");
            DetailBadgeText.Foreground = B("#7B8794");
        }
        else
        {
            DetailBadge.Background = B("#FDECEC");
            DetailBadgeText.Foreground = B("#C62F2F");
        }

        // 名称 + 类别/厂商
        DetailPanel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(device.FriendlyName) ? "未命名设备" : device.FriendlyName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = B("#1F2933"),
            TextWrapping = TextWrapping.Wrap
        });

        var sub = string.Join("  ·  ", new[] { device.ClassName, device.Manufacturer }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (sub.Length > 0)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11.5,
                Foreground = B("#7B8794"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        // 异常说明
        if (device.HasProblem && !string.IsNullOrWhiteSpace(device.ProblemDescription))
        {
            var box = new Border
            {
                Background = device.ProblemCode == 45 ? B("#F5F7FA") : B("#FEF5F5"),
                BorderBrush = device.ProblemCode == 45 ? B("#E7EBF1") : B("#F7DCDC"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 10, 0, 10)
            };
            box.Child = new TextBlock
            {
                Text = device.ProblemDescription.Trim(),
                FontSize = 11.5,
                Foreground = device.ProblemCode == 45 ? B("#5B6675") : B("#A03030"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17
            };
            DetailPanel.Children.Add(box);
        }
        else
        {
            DetailPanel.Children.Add(new Border { Height = 10 });
        }

        // 键值区（两列，标签在上、值在下，自动对齐）——空值不占位
        var pairs = new (string Label, string Value)[]
        {
            ("驱动版本", device.DriverVersion),
            ("驱动日期", device.DriverDate),
            ("驱动提供者", device.DriverProvider),
            ("INF 文件", device.DriverInfPath)
        };

        var fields = new UniformGrid { Columns = 2 };
        foreach (var p in pairs)
            if (!string.IsNullOrWhiteSpace(p.Value)) AddField(fields, p.Label, p.Value);

        if (fields.Children.Count > 0) DetailPanel.Children.Add(fields);
        else
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "该设备没有驱动版本信息（常见于未连接的设备或系统内置组件）。",
                FontSize = 11.5,
                Foreground = B("#7B8794"),
                TextWrapping = TextWrapping.Wrap
            });

        // 硬件 ID：可能很长，单独占满宽度
        AddLongField("硬件 ID", device.HardwareIds);
        AddLongField("服务名", device.Service);
    }

    private void AddField(Panel host, string label, string value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = B("#7B8794"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 12.5,
            Foreground = B("#1F2933"),
            TextWrapping = TextWrapping.Wrap
        });
        host.Children.Add(stack);
    }

    private void AddLongField(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = B("#7B8794"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 11.5,
            Foreground = B("#4A5563"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16
        });
        DetailPanel.Children.Add(stack);
    }


    // ---------------- 操作 ----------------

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunScanAsync("正在刷新设备列表…", async (_, ct) =>
        {
            await Scanner.ScanDevicesAsync(ct);
            ApplyFilter();
        });

    private async void BtnScanUpdates_Click(object sender, RoutedEventArgs e)
        => await Ctx.RunScanAsync("正在查询 Windows Update 驱动…", async (_, ct) =>
        {
            await Scanner.ScanDriverUpdatesAsync(false, ct);
            _hasScannedUpdates = true;
            _showIgnoredUpdates = false;
            ApplyUpdateFilter();
        });

    private void BtnEnableMu_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("启用 Microsoft Update 扩展源",
            "将注册 Microsoft Update 服务，以便搜索显卡、网卡等第三方官方驱动。\n该操作需要管理员权限，是否继续？"))
            return;

        Ctx.State.IsBusy = true;
        Ctx.State.StatusText = "正在注册 Microsoft Update 并搜索驱动…";
        Ui.Log(Ctx.State.StatusText);
        try
        {
            // Microsoft Update 源在部分网络下会重试很久：用进度窗口显示已用时间并允许取消
            var completed = ProgressDialog.Run(
                "正在搜索 Microsoft Update 驱动",
                "首次启用会先注册 Microsoft Update 源，通常需要 1–3 分钟。",
                async (progress, ct) =>
                {
                    progress.Report(new ProgressInfo
                    {
                        Percent = -1,
                        Status = "正在注册 Microsoft Update 并搜索驱动…",
                        Detail = "该官方源响应较慢，可随时取消",
                        CanCancel = true
                    });
                    await Scanner.ScanDriverUpdatesAsync(true, ct);
                    progress.Report(new ProgressInfo { Percent = 100, Status = "搜索完成", Detail = "", CanCancel = false });
                });

            if (!completed)
            {
                Ctx.State.StatusText = "已取消搜索";
                return;
            }

            _hasScannedUpdates = true;
            _showIgnoredUpdates = false;
            ApplyUpdateFilter();
            Ctx.State.StatusText = "完成 · " + DateTime.Now.ToString("HH:mm:ss");

            var warning = Ctx.DriverUpdates.LastWarning;
            if (!string.IsNullOrWhiteSpace(warning))
                Ui.Warn("Microsoft Update 扩展源不可用", warning);
        }
        catch (Exception ex)
        {
            Ctx.State.StatusText = "失败";
            Ui.Log($"Microsoft Update 搜索失败: {ex}", "ERROR");
            Ui.Error("操作失败", ex.Message);
        }
        finally
        {
            Ctx.State.IsBusy = false;
        }
    }

    private void BtnDevMgr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception ex) { Ui.Error("无法打开设备管理器", ex.Message); }
    }

    // ---------------- 忽略 / 恢复 ----------------

    private void DeviceIgnore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceItem device) return;

        var name = string.IsNullOrWhiteSpace(device.FriendlyName) ? "该设备" : device.FriendlyName;
        if (device.Ignored)
        {
            IgnoreStore.RestoreDevice(device.InstanceId);
            device.Ignored = false;
            Ui.Toast($"已恢复设备提醒：{name}");
        }
        else
        {
            IgnoreStore.IgnoreDevice(device.InstanceId);
            device.Ignored = true;
            Ui.Toast($"已忽略设备提醒：{name}，可在筛选「已忽略的设备」中恢复");
        }

        Scanner.RefreshSummary();
        ApplyFilter();
    }

    /// <summary>
    /// 按设备的硬件 ID 检索 Microsoft Update Catalog。
    /// Windows Update 没给出可用驱动时，这里是官方渠道的兜底。
    /// </summary>
    private async void DeviceCatalog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceItem device) return;

        var hardwareIds = DeviceService.GetHardwareIds(device.InstanceId);
        if (hardwareIds.Count == 0)
        {
            Ui.Info("没有硬件 ID", "该设备没有可用的硬件 ID，无法在更新目录中精确检索。");
            return;
        }

        var hardwareId = hardwareIds[0];
        List<DriverUpdateItem> found = new();

        try
        {
            await Ctx.RunAsync("正在检索 Microsoft Update Catalog…", async () =>
            {
                found = await Ctx.DriverUpdates.SearchCatalogAsync(hardwareId);
            });
        }
        catch (UserCancelledException)
        {
            return;
        }

        if (found.Count == 0)
        {
            Ui.Info("更新目录中没有匹配项",
                $"硬件 ID {hardwareId} 在 Microsoft Update Catalog 中没有找到驱动包。\n" +
                "该目录主要收录 Windows 与服务端更新，桌面显卡/网卡驱动通常需要去厂商官网获取。");
            return;
        }

        var lines = string.Join("\n", found.Take(8).Select(u => "· " + u.Title));
        if (found.Count > 8) lines += $"\n… 其余 {found.Count - 8} 项";
        Ui.Info("更新目录检索结果", $"{hardwareId}\n\n{lines}\n\n" +
            "更新目录只提供下载链接，需要手动下载后安装。");
    }

    // ---------------- 驱动更新：勾选与安装 ----------------

    private bool _hasScannedUpdates;
    private bool _showIgnoredUpdates;

    /// <summary>重新应用驱动更新筛选：默认隐藏已忽略项，可切换到「已忽略」视图。</summary>
    private void ApplyUpdateFilter()
    {
        if (UpdateGrid is null || EmptyPanel is null) return;

        var view = CollectionViewSource.GetDefaultView(Ctx.State.DriverUpdates);
        if (view is null) return;

        var showIgnored = _showIgnoredUpdates;
        view.Filter = o => o is DriverUpdateItem u && u.Ignored == showIgnored;
        view.Refresh();

        var shown = view.Cast<object>().Count();
        var ignoredCount = Ctx.State.DriverUpdates.Count(i => i.Ignored);

        if (BtnShowIgnoredUpdates is not null)
        {
            BtnShowIgnoredUpdates.Visibility =
                ignoredCount > 0 || showIgnored ? Visibility.Visible : Visibility.Collapsed;
            BtnShowIgnoredUpdates.Content = showIgnored ? "返回更新列表" : $"已忽略 {ignoredCount}";
        }

        EmptyPanel.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown == 0)
        {
            if (showIgnored)
            {
                EmptyTitle.Text = "没有已忽略的驱动更新";
                EmptyDesc.Text = "在更新列表中选中一项后，点左下角「忽略此更新」";
            }
            else if (_hasScannedUpdates)
            {
                EmptyTitle.Text = "没有发现可用的驱动更新";
                EmptyDesc.Text = ignoredCount > 0
                    ? $"有 {ignoredCount} 个更新已被忽略，可点右上角查看并恢复"
                    : "系统当前没有适用于本机的官方驱动更新";
            }
            else
            {
                EmptyTitle.Text = "还没有扫描";
                EmptyDesc.Text = "点上方「扫描驱动更新」查询官方驱动";
            }
        }

        UpdateSelectionState();
    }

    /// <summary>刷新勾选状态：全选按钮、安装按钮文案/可用性、底部提示。</summary>
    private void UpdateSelectionState()
    {
        if (UpdateHint is null || BtnInstallSelected is null || BtnCheckAll is null || BtnInstallAll is null) return;

        var all = Ctx.State.DriverUpdates;
        var actionable = all.Where(i => !i.Ignored).ToList();
        var total = actionable.Count;
        var selected = actionable.Count(i => i.Select);
        var ignoredCount = all.Count(i => i.Ignored);

        BtnCheckAll.IsEnabled = total > 0 && !_showIgnoredUpdates;
        BtnInstallAll.IsEnabled = total > 0 && !_showIgnoredUpdates;
        BtnInstallSelected.IsEnabled = selected > 0 && !_showIgnoredUpdates;
        BtnInstallSelected.Content = selected > 0 ? $"安装选中的 {selected} 项" : "安装选中";

        var allChecked = total > 0 && selected == total;
        BtnCheckAll.Content = allChecked ? "取消全选" : "全选";

        if (BtnIgnoreUpdate is not null)
        {
            var row = UpdateGrid?.SelectedItem as DriverUpdateItem;
            BtnIgnoreUpdate.IsEnabled = row is not null;
            BtnIgnoreUpdate.Content = row?.Ignored == true ? "恢复此更新" : "忽略此更新";
        }

        if (total == 0)
        {
            UpdateHint.Text = _showIgnoredUpdates
                ? (ignoredCount > 0 ? $"正在查看 {ignoredCount} 个已忽略的更新 · 选中后可恢复" : "")
                : _hasScannedUpdates
                    ? (ignoredCount > 0
                        ? $"没有待安装的更新 · {ignoredCount} 个已忽略，可点右上角查看并恢复"
                        : "没有发现可用的驱动更新")
                    : "";
            return;
        }

        UpdateHint.Text = _showIgnoredUpdates
            ? $"正在查看 {ignoredCount} 个已忽略的更新 · 选中后可恢复"
            : selected == 0
                ? $"共 {total} 个更新 · 勾选左侧方框后，点「安装选中」"
                : $"已勾选 {selected} / {total} 个更新";
    }

    private void UpdateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void BtnShowIgnoredUpdates_Click(object sender, RoutedEventArgs e)
    {
        _showIgnoredUpdates = !_showIgnoredUpdates;
        ApplyUpdateFilter();
    }

    private void BtnIgnoreUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateGrid?.SelectedItem is not DriverUpdateItem item)
        {
            Ui.Info("请选择一条更新", "先在上方列表中点击一行，再点「忽略此更新」。");
            return;
        }

        if (item.Ignored)
        {
            IgnoreStore.RestoreDriver(item.Key);
            item.Ignored = false;
            item.Select = false;
            Ui.Toast($"已恢复驱动更新：{item.Title}");
            // 恢复后自动回到正常列表，方便确认它已经重新出现
            _showIgnoredUpdates = false;
        }
        else
        {
            IgnoreStore.IgnoreDriver(item.Key);
            item.Ignored = true;
            item.Select = false;
            Ui.Toast($"已忽略驱动更新：{item.Title}，可在「已忽略」中恢复");
        }

        Scanner.RefreshSummary();
        ApplyUpdateFilter();
    }

    private void UpdateCheck_Click(object sender, RoutedEventArgs e) => UpdateSelectionState();

    private void BtnCheckAll_Click(object sender, RoutedEventArgs e)
    {
        var all = Ctx.State.DriverUpdates.Where(i => !i.Ignored).ToList();
        var checkAll = all.Any(i => !i.Select); // 有未勾选的就全选，否则全部取消
        foreach (var item in all) item.Select = checkAll;
        UpdateSelectionState();
    }

    private async void BtnInstallSelected_Click(object sender, RoutedEventArgs e)
        => await InstallAsync(Ctx.State.DriverUpdates.Where(i => i.Select && !i.Ignored).ToList());

    private async void BtnInstallAll_Click(object sender, RoutedEventArgs e)
        => await InstallAsync(Ctx.State.DriverUpdates.Where(i => !i.Ignored).ToList());

    private async Task InstallAsync(List<DriverUpdateItem> items)
    {
        if (items.Count == 0)
        {
            Ui.Info("还没有可安装的驱动",
                    Ctx.State.DriverUpdates.Count == 0
                        ? "当前没有扫描到驱动更新。\n请先点上方「扫描驱动更新」，扫描完成后勾选要安装的驱动。"
                        : "请先勾选右下方「可用驱动更新」列表左侧的方框，再点「安装选中」。");
            return;
        }

        var lines = string.Join("\n", items.Take(8).Select(i => "· " + i.Title));
        if (items.Count > 8) lines += $"\n… 其余 {items.Count - 8} 项";

        if (!Ui.Confirm("确认安装驱动", $"即将安装 {items.Count} 个驱动更新：\n{lines}\n\n安装过程由 Windows Update 代理完成。"))
            return;

        await Ctx.RunAsync("正在安装驱动…", async () =>
        {
            // 1) 备份将被替换的驱动包（按类别/厂商匹配）
            var names = items
                .SelectMany(i => Ctx.State.DriverPackages.Where(p =>
                    p.IsThirdParty &&
                    (!string.IsNullOrEmpty(i.DriverClass) && p.ClassName.Contains(i.DriverClass, StringComparison.OrdinalIgnoreCase) ||
                     !string.IsNullOrEmpty(i.DriverManufacturer) && p.Provider.Contains(i.DriverManufacturer, StringComparison.OrdinalIgnoreCase))))
                .Select(p => p.PublishedName)
                .Distinct()
                .ToList();

            if (names.Count > 0)
            {
                try
                {
                    var backup = await Ctx.Backups.BackupDriversAsync(names);
                    Ui.Log($"已备份 {backup.PackageCount} 个相关驱动包到 {backup.Path}");
                }
                catch (Exception ex)
                {
                    Ui.Log("驱动备份失败（继续安装）: " + ex.Message, "WARN");
                }
            }

            // 2) 还原点（可选）
            if (Ui.Confirm("创建系统还原点", "建议在安装驱动前创建还原点，出问题可一键回滚。\n是否创建？"))
            {
                try
                {
                    await Ctx.Backups.CreateRestorePointAsync("DevUp 安装驱动前");
                    Ui.Log("已创建系统还原点");
                }
                catch (Exception ex)
                {
                    Ui.Log("创建还原点失败（继续安装）: " + ex.Message, "WARN");
                }
            }

            // 3) 安装
            var result = await Ctx.DriverUpdates.InstallAsync(items);
            Ui.Log(result.Message);

            // 4) 校验
            await Scanner.ScanDevicesAsync();

            var msg = result.Success
                ? result.Confirmed
                    ? $"已安装 {result.Installed.Count} 个驱动更新。请重启后再次扫描以确认生效。"
                    : $"已提交 {result.Installed.Count + result.Pending.Count} 个驱动更新，" +
                      $"其中 {result.Pending.Count} 个需要重启后确认是否生效。\n" +
                      string.Join("\n", result.Pending.Take(3))
                : $"成功 {result.Installed.Count} 个，失败 {result.Failed.Count} 个。\n" +
                  string.Join("\n", result.Failed.Take(5));

            // 安装完成的条目取消勾选，避免重复安装
            foreach (var item in items) item.Select = false;
            UpdateSelectionState();

            Ui.Info("安装结果", msg);
        });
    }
}
