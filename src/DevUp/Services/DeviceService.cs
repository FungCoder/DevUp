using System.Text.Json;
using DevUp.Core;
using DevUp.Models;
using Nefarius.Utilities.DeviceManagement.Extensions;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace DevUp.Services;

/// <summary>设备与驱动现状扫描：Get-PnpDevice + Nefarius 原生封装。</summary>
public sealed class DeviceService
{
    public async Task<List<DeviceItem>> ScanAsync(CancellationToken ct = default)
    {
        var json = await PsBridge.RunAsync("devices", null, false, ct);
        var devices = new List<DeviceItem>();

        if (json.TryGetProperty("data", out var data) &&
            data.TryGetProperty("devices", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                var item = new DeviceItem
                {
                    InstanceId = Str(e, "instanceId"),
                    FriendlyName = Str(e, "friendlyName"),
                    ClassName = Str(e, "className"),
                    Status = Str(e, "status"),
                    Problem = Str(e, "problem"),
                    ProblemDescription = Str(e, "problemDesc"),
                    IsPresent = Bool(e, "isPresent"),
                    Manufacturer = Str(e, "manufacturer"),
                    Service = Str(e, "service"),
                    HardwareIds = Str(e, "hardwareIds"),
                    CompatibleIds = Str(e, "compatibleIds"),
                    ClassGuid = Str(e, "classGuid"),
                    ProblemCode = ParseProblemCode(e)
                };
                devices.Add(item);
            }
        }

        // 复用 Nefarius：原生读取当前驱动包信息（版本 / 厂商 / INF 路径），比 WMI 更快更准
        await Task.Run(() =>
        {
            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                if (!device.IsPresent || string.IsNullOrEmpty(device.InstanceId)) continue;
                try
                {
                    var nativeDevice = PnPDevice.GetDeviceByInstanceId(
                        device.InstanceId, DeviceLocationFlags.Normal);
                    var driver = nativeDevice.GetCurrentDriver();
                    if (driver is null) continue;
                    device.DriverVersion = driver.DriverVersion?.ToString() ?? "";
                    device.DriverProvider = driver.ProviderName ?? "";
                    device.DriverDate = driver.DriverDate == default
                        ? "" : driver.DriverDate.ToString("yyyy-MM-dd");
                    device.DriverInfPath = driver.InfPath ?? "";
                }
                catch
                {
                    // 没有驱动或已移除的设备忽略
                }
            }
        }, ct).WaitAsync(ct);
        return devices;
    }

    /// <summary>取指定设备实例的硬件 ID 列表（用于更新目录精确匹配）。</summary>
    public static List<string> GetHardwareIds(string instanceId)
    {
        var result = new List<string>();
        try
        {
            var dev = PnPDevice.GetDeviceByInstanceId(instanceId, DeviceLocationFlags.Normal);
            var ids = dev.HardwareIds;
            if (ids is null) return result;

            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            }
        }
        catch { }
        return result;
    }

    private static int ParseProblemCode(JsonElement e)
    {
        if (e.TryGetProperty("problemCode", out var code) && code.ValueKind == JsonValueKind.Number)
            return code.GetInt32();

        var name = Str(e, "problem");
        return name switch
        {
            "CM_PROB_NO_CONFIG" => 1,
            "CM_PROB_DISABLED" => 22,
            "CM_PROB_FAILED_INSTALL" => 28,
            "CM_PROB_NEEDS_RESTART" => 14,
            "CM_PROB_NOT_STARTED" => 10,
            "CM_PROB_FAILED_DRIVER_ENTRY" => 19,
            "CM_PROB_HARDWARE_ERROR" => 43,
            "CM_PROB_FAILED_DRIVER_LOAD" => 45,
            "CM_PROB_DEVICE_NOT_CONNECTED" => 16,
            _ => 0
        };
    }

    internal static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
