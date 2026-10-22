using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

namespace DevUp.Core;
public static class Scanner
{
    /// <summary>软件清单或其可用版本发生变化（视图据此刷新筛选与计数）。</summary>
    public static event Action? SoftwareChanged;

    private static void RaiseSoftwareChanged() => SoftwareChanged?.Invoke();

    public static async Task ScanDevicesAsync(CancellationToken ct = default)
    {
        var devices = await Ctx.Devices.ScanAsync(ct);

        // 只有“存在且有异常”的设备才会带上忽略标记：
        // 设备恢复正常后应当正常显示，而不是继续被旧记录隐藏。
        foreach (var d in devices)
            d.Ignored = d.HasProblem && d.IsPresent && IgnoreStore.IsDeviceIgnored(d.InstanceId);

        Ctx.State.Devices.Clear();
        foreach (var d in devices) Ctx.State.Devices.Add(d);

        Ctx.State.Summary = new ScanSummary
        {
            DeviceTotal = devices.Count,
            DevicePresent = devices.Count(d => d.IsPresent),
            DeviceProblem = devices.Count(d => d.HasProblem && d.IsPresent && !d.Ignored),
            DeviceUnknown = devices.Count(d => d.IsUnknownDevice && d.IsPresent && !d.Ignored),
            DriverPackages = Ctx.State.DriverPackages.Count,
            DriverUpdates = Ctx.State.DriverUpdates.Count(u => !u.Ignored),
            SoftwareTotal = Ctx.State.Software.Count,
            SoftwareUpdates = Ctx.State.Software.Count(i => i.HasUpdate && !i.Ignored),
            ScannedAt = DateTime.Now
        };
    }

    public static Task ScanDriverPackagesAsync(CancellationToken ct = default)
    {
        var packages = Ctx.DriverStore.GetPackages();
        Ctx.State.DriverPackages.Clear();
        foreach (var p in packages) Ctx.State.DriverPackages.Add(p);
        RefreshSummary();
        return Task.CompletedTask;
    }

    public static async Task ScanSoftwareAsync(CancellationToken ct = default)
    {
        var registry = SoftwareService.ScanRegistry();
        var msix = await Ctx.Software.ScanMsixAsync(ct);

        Ctx.State.Software.Clear();
        foreach (var s in registry) Ctx.State.Software.Add(s);
        foreach (var s in msix) Ctx.State.Software.Add(s);

        // 清单重建后重新关联上次的 winget 结果，避免“可用版本”被清空
        if (Ctx.State.SoftwareUpdates.Count > 0) CorrelateUpdates();

        RefreshSummary();
        RaiseSoftwareChanged();
    }

    public static async Task ScanDriverUpdatesAsync(bool includeMicrosoftUpdate, CancellationToken ct = default)
    {
        var updates = await Ctx.DriverUpdates.SearchAsync(includeMicrosoftUpdate, ct);
        foreach (var u in updates) u.Ignored = IgnoreStore.IsDriverIgnored(u.Key);

        Ctx.State.DriverUpdates.Clear();
        foreach (var u in updates) Ctx.State.DriverUpdates.Add(u);
        RefreshSummary();
    }

    public static async Task ScanSoftwareUpdatesAsync(CancellationToken ct = default)
    {
        var updates = await Ctx.Software.GetUpdatesAsync(ct);
        Ctx.State.SoftwareUpdates.Clear();
        foreach (var u in updates) Ctx.State.SoftwareUpdates.Add(u);

        CorrelateUpdates();
        RefreshSummary();
        RaiseSoftwareChanged();
    }

    /// <summary>把 winget 结果贴回已装清单（含无法关联的“仅 winget 源”条目）。</summary>
    private static void CorrelateUpdates()
    {
        // 清空上一次的关联状态
        foreach (var s in Ctx.State.Software)
        {
            s.WingetId = null;
            s.AvailableVersion = null;
            s.Ignored = false;
        }

        // 移除上一次补充的 winget-only 条目
        for (var i = Ctx.State.Software.Count - 1; i >= 0; i--)
            if (Ctx.State.Software[i].Source == "winget 源")
                Ctx.State.Software.RemoveAt(i);

        var extras = SoftwareService.Correlate(Ctx.State.Software.ToList(), Ctx.State.SoftwareUpdates.ToList());
        foreach (var extra in extras)
            Ctx.State.Software.Add(extra);

        // 关联完成后才知道每条软件对应的“可用版本”，此时再套用忽略记录
        foreach (var s in Ctx.State.Software)
            s.Ignored = s.HasUpdate && IgnoreStore.IsSoftwareIgnored(s.WingetId, s.AvailableVersion);
    }

    public static void RefreshSummary()
    {
        var s = Ctx.State.Summary;
        Ctx.State.Summary = new ScanSummary
        {
            DeviceTotal = Ctx.State.Devices.Count,
            DevicePresent = Ctx.State.Devices.Count(d => d.IsPresent),
            DeviceProblem = Ctx.State.Devices.Count(d => d.HasProblem && d.IsPresent && !d.Ignored),
            DeviceUnknown = Ctx.State.Devices.Count(d => d.IsUnknownDevice && d.IsPresent && !d.Ignored),
            DriverPackages = Ctx.State.DriverPackages.Count,
            DriverUpdates = Ctx.State.DriverUpdates.Count(u => !u.Ignored),
            SoftwareTotal = Ctx.State.Software.Count,
            SoftwareUpdates = Ctx.State.Software.Count(s => s.HasUpdate && !s.Ignored),
            ScannedAt = s.ScannedAt
        };
    }
}
