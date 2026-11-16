using System.Security.Principal;
using System.Text.Json;
using DevUp.Core;
using DevUp.Models;

namespace DevUp.Services;

/// <summary>复用应用的提权、忙碌状态与日志；后台返回逐项真实结果。</summary>
internal sealed class StartupService
{
    internal const string Action = "startupChange";

    internal Task<StartupScan> ScanAsync() => Task.Run(StartupBackend.Scan);

    internal async Task<List<StartupChangeResult>> ChangeAsync(
        IEnumerable<StartupEntry> entries, bool enable)
    {
        var request = new StartupChangeRequest(
            WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("无法识别当前用户。"),
            enable, entries.DistinctBy(entry => entry.Id).ToList());
        if (request.Entries.Count == 0) return [];
        List<StartupChangeResult> results;
        if (!Native.IsAdministrator() && request.Entries.Any(entry =>
                entry.Machine || entry.Source is StartupSource.Task or StartupSource.Service))
        {
            var json = await PsBridge.RunAsync(Action, new Dictionary<string, string>
            {
                ["Request"] = JsonSerializer.Serialize(request)
            }, elevated: true);
            results = json.GetProperty("data").Deserialize<List<StartupChangeResult>>()
                ?? throw new InvalidOperationException("自启管理未返回操作结果。");
        }
        else results = await Task.Run(() => StartupBackend.Change(request));
        foreach (var result in results)
            Ui.Log($"自启管理：{result.Name}：{result.Message}", result.Success ? "INFO" : "ERROR");
        return results;
    }

    internal static JsonElement HandleElevated(Dictionary<string, string> args)
    {
        if (args.Count != 1 || !args.TryGetValue("Request", out var json) || json.Length > 1_000_000)
            throw new ArgumentException("自启操作参数无效。");
        var request = JsonSerializer.Deserialize<StartupChangeRequest>(json)
            ?? throw new ArgumentException("自启操作请求为空。");
        return JsonSerializer.SerializeToElement(new { ok = true, data = StartupBackend.Change(request) });
    }
}
