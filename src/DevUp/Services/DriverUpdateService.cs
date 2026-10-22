using System.Text.Json;
using DevUp.Core;
using DevUp.Models;

namespace DevUp.Services;

public sealed class InstallOutcome
{
    public bool Success { get; set; }

    /// <summary>结果是否已被 Windows Update 明确确认（未确认通常意味着需要重启才生效）。</summary>
    public bool Confirmed { get; set; } = true;

    public string Message { get; set; } = "";
    public List<string> Installed { get; } = new();
    public List<string> Failed { get; } = new();
    public List<string> Pending { get; } = new();
}

/// <summary>驱动更新：复用 PSWindowsUpdate（Windows Update 官方 COM 通道，机器级精准匹配）。</summary>
public sealed class DriverUpdateService
{
    /// <summary>
    /// 搜索可用的驱动更新。PSWindowsUpdate 要求管理员权限，因此走提权子进程。
    /// includeMicrosoftUpdate=true 时额外注册 Microsoft Update 扩展源，可搜到显卡/网卡等第三方驱动。
    /// 该源在部分网络下会长时间无响应，因此带超时并在失败时自动回退到 Windows Update。
    /// </summary>
    public async Task<List<DriverUpdateItem>> SearchAsync(
        bool includeMicrosoftUpdate = false, CancellationToken ct = default)
    {
        LastWarning = "";

        if (!includeMicrosoftUpdate)
        {
            try
            {
                return await SearchOnceAsync(false, TimeSpan.FromSeconds(90), ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("Windows Update 在 90 秒内没有响应，请稍后重试。");
            }
        }

        try
        {
            return await SearchOnceAsync(true, TimeSpan.FromSeconds(150), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastWarning = "Microsoft Update 扩展源 2 分 30 秒内没有返回结果，已自动改用 Windows Update 源。";
            Ui.Log(LastWarning, "WARN");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            LastWarning = "Microsoft Update 扩展源暂时不可用，已自动改用 Windows Update 源。";
            Ui.Log($"{LastWarning} 原因：{ex.Message}", "WARN");
        }

        return await SearchOnceAsync(false, TimeSpan.FromSeconds(90), ct);
    }

    /// <summary>最近一次驱动搜索中需要提示给用户的非致命问题（如扩展源不可用）。</summary>
    public string LastWarning { get; private set; } = "";

    private async Task<List<DriverUpdateItem>> SearchOnceAsync(
        bool includeMicrosoftUpdate, TimeSpan timeout, CancellationToken ct)
    {
        var args = new Dictionary<string, string>();
        if (includeMicrosoftUpdate) args["MicrosoftUpdate"] = "1";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        JsonElement json;
        try
        {
            json = await PsBridge.RunAsync("driverUpdates", args, elevated: true, ct: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw; // 由调用方决定是否回退
        }

        var list = new List<DriverUpdateItem>();
        if (json.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("warning", out var w) && w.ValueKind == JsonValueKind.String)
            {
                var warn = (w.GetString() ?? "").Trim();
                if (warn.Length > 0)
                {
                    LastWarning = LastWarning.Length == 0 ? warn : $"{LastWarning} {warn}";
                    Ui.Log("[driverUpdates] " + warn, "WARN");
                }
            }

            if (data.TryGetProperty("updates", out var arr))
            {
                foreach (var e in arr.EnumerateArray())
                {
                    list.Add(new DriverUpdateItem
                    {
                        Title = Str(e, "title"),
                        UpdateId = Str(e, "updateId"),
                        Revision = Num(e, "revision"),
                        Kb = Str(e, "kb"),
                        DriverModel = Str(e, "driverModel"),
                        DriverClass = Str(e, "driverClass"),
                        DriverManufacturer = Str(e, "driverManufacturer"),
                        DriverVerDate = Str(e, "driverVerDate"),
                        Size = Str(e, "size"),
                        Source = includeMicrosoftUpdate ? "Windows Update + Microsoft Update" : "Windows Update"
                    });
                }
            }
        }

        // 同一个更新可能出现在不同通道，按 UpdateID 去重并保留信息最全的一条
        return list
            .GroupBy(x => x.UpdateId + ":" + x.Revision)
            .Select(g => g.OrderByDescending(x => x.DriverVerDate.Length).First())
            .OrderByDescending(x => x.DriverVerDate)
            .ToList();
    }

    /// <summary>按 UpdateID:Revision 安装驱动。</summary>
    public async Task<InstallOutcome> InstallAsync(
        IEnumerable<DriverUpdateItem> updates, CancellationToken ct = default)
    {
        var outcome = new InstallOutcome();
        var list = updates.ToList();
        if (list.Count == 0)
        {
            outcome.Success = false;
            outcome.Message = "没有选择任何更新";
            return outcome;
        }

        var ids = string.Join(",", list.Select(x => x.Key));
        var json = await PsBridge.RunAsync("installDriver",
            new Dictionary<string, string> { ["UpdateIds"] = ids },
            elevated: true, ct: ct);

        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("results", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                var ok = e.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                var confirmed = !e.TryGetProperty("confirmed", out var c) || c.ValueKind != JsonValueKind.False;
                var msg = Str(e, "message");
                var title = Str(e, "title");

                if (ok && confirmed) outcome.Installed.Add(string.IsNullOrEmpty(title) ? msg : title);
                else if (ok) outcome.Pending.Add(string.IsNullOrEmpty(title) ? msg : title);
                else outcome.Failed.Add(msg);
            }
        }

        outcome.Success = outcome.Failed.Count == 0 && (outcome.Installed.Count > 0 || outcome.Pending.Count > 0);
        outcome.Confirmed = outcome.Pending.Count == 0;
        outcome.Message = outcome.Failed.Count > 0
            ? $"{outcome.Installed.Count} 成功 / {outcome.Failed.Count} 失败"
            : outcome.Pending.Count > 0
                ? $"已安装 {outcome.Installed.Count} 个，{outcome.Pending.Count} 个待重启后确认"
                : $"已提交 {outcome.Installed.Count} 个驱动更新";
        return outcome;
    }

    /// <summary>通过 Microsoft Update Catalog 按硬件 ID 精确检索更新目录（复用 MSCatalog 模块）。</summary>
    public async Task<List<DriverUpdateItem>> SearchCatalogAsync(
        string hardwareId, CancellationToken ct = default)
    {
        var json = await PsBridge.RunAsync("catalogSearch",
            new Dictionary<string, string> { ["SearchText"] = hardwareId },
            elevated: false, ct: ct);

        var list = new List<DriverUpdateItem>();
        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("updates", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                list.Add(new DriverUpdateItem
                {
                    Title = Str(e, "title"),
                    UpdateId = Str(e, "updateId"),
                    Revision = Num(e, "revision"),
                    DriverModel = Str(e, "driverModel"),
                    DriverClass = Str(e, "driverClass"),
                    DriverManufacturer = Str(e, "driverManufacturer"),
                    DriverVerDate = Str(e, "driverVerDate"),
                    Size = Str(e, "size"),
                    Source = "Microsoft Update Catalog"
                });
            }
        }
        return list;
    }

    internal static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static int Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
