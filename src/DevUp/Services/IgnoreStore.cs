using DevUp.Core;

namespace DevUp.Services;

/// <summary>
/// 忽略项的持久化。键刻意带上版本/修订号：
/// 一旦出现更新的版本，旧记录不会再命中，项目会自动重新出现。
/// </summary>
public static class IgnoreStore
{
    // ---------------- 驱动更新 ----------------

    public static bool IsDriverIgnored(string key)
        => Contains(DriverList, key);

    public static void IgnoreDriver(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var list = DriverList;
        // 同一个 UpdateID 只保留最新一次忽略记录，避免修订号变化后越积越多
        var updateId = key.Split(':')[0];
        list.RemoveAll(x => x.StartsWith(updateId + ":", StringComparison.OrdinalIgnoreCase));
        list.Add(key);
        Ctx.Settings.Save();
    }

    public static void RestoreDriver(string key)
        => Remove(DriverList, key);

    // ---------------- 异常设备 ----------------

    public static bool IsDeviceIgnored(string instanceId)
        => Contains(DeviceList, instanceId);

    public static void IgnoreDevice(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return;
        Add(DeviceList, instanceId);
    }

    public static void RestoreDevice(string instanceId)
        => Remove(DeviceList, instanceId);

    // ---------------- 软件更新 ----------------

    public static string SoftwareKey(string wingetId, string version) => $"{wingetId}@{version}";

    public static bool IsSoftwareIgnored(string? wingetId, string? version)
    {
        if (string.IsNullOrWhiteSpace(wingetId) || string.IsNullOrWhiteSpace(version)) return false;
        return Contains(SoftwareList, SoftwareKey(wingetId!, version!));
    }

    public static void IgnoreSoftware(string? wingetId, string? version)
    {
        if (string.IsNullOrWhiteSpace(wingetId) || string.IsNullOrWhiteSpace(version)) return;

        var list = SoftwareList;
        // 同一个 winget 包只保留当前版本的忽略记录
        list.RemoveAll(x => x.StartsWith(wingetId + "@", StringComparison.OrdinalIgnoreCase));
        list.Add(SoftwareKey(wingetId!, version!));
        Ctx.Settings.Save();
    }

    public static void RestoreSoftware(string? wingetId, string? version)
    {
        if (string.IsNullOrWhiteSpace(wingetId) || string.IsNullOrWhiteSpace(version)) return;
        Remove(SoftwareList, SoftwareKey(wingetId!, version!));
    }

    // ---------------- 基础操作 ----------------

    private static List<string> DriverList => Ctx.Settings.IgnoredDriverUpdates ??= new List<string>();
    private static List<string> DeviceList => Ctx.Settings.IgnoredDevices ??= new List<string>();
    private static List<string> SoftwareList => Ctx.Settings.IgnoredSoftware ??= new List<string>();

    private static bool Contains(List<string> list, string key)
        => !string.IsNullOrWhiteSpace(key)
           && list.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));

    private static void Add(List<string> list, string key)
    {
        if (Contains(list, key)) return;
        list.Add(key);
        Ctx.Settings.Save();
    }

    private static void Remove(List<string> list, string key)
    {
        if (list.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)) > 0)
            Ctx.Settings.Save();
    }
}
