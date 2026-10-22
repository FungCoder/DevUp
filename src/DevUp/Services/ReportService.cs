using System.IO;
using System.Text;
using System.Text.Json;
using DevUp.Models;

namespace DevUp.Services;

/// <summary>报告导出（JSON / HTML）。</summary>
public static class ReportService
{
    public static void ExportJson(ScanState state, string path)
    {
        var payload = new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            machine = Environment.MachineName,
            summary = state.Summary,
            devices = state.Devices.Select(d => new
            {
                d.FriendlyName, d.ClassName, d.Status, d.ProblemCode, d.ProblemText,
                d.IsPresent, d.Manufacturer, d.HardwareIds, d.DriverVersion, d.DriverProvider, d.DriverDate, d.DriverInfPath
            }),
            driverPackages = state.DriverPackages.Select(p => new
            {
                p.PublishedName, p.OriginalName, p.Provider, p.ClassName, p.DriverVersion, p.DriverDate, p.Signature
            }),
            driverUpdates = state.DriverUpdates.Select(u => new
            {
                u.Title, u.DriverModel, u.DriverClass, u.DriverManufacturer, u.DriverVerDate, u.Source
            }),
            software = state.Software.Select(s => new
            {
                s.DisplayName, s.DisplayVersion, s.AvailableVersion, s.Publisher, s.Source, s.InstallDate
            })
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    public static void ExportHtml(ScanState state, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>DevUp 报告 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "</title>");
        sb.AppendLine("""
<style>
 body{font-family:"Microsoft YaHei UI",Segoe UI,sans-serif;margin:28px;color:#1f2933;background:#f4f6f9}
 h1{font-size:20px} h2{font-size:15px;margin-top:26px}
 .cards{display:flex;gap:12px;flex-wrap:wrap;margin:14px 0}
 .card{background:#fff;border:1px solid #e5e9ef;border-radius:10px;padding:12px 18px;min-width:120px}
 .card b{display:block;font-size:22px}
 .card span{font-size:12px;color:#7b8794}
 table{border-collapse:collapse;width:100%;background:#fff;border-radius:10px;overflow:hidden;font-size:12.5px}
 th,td{padding:7px 10px;border-bottom:1px solid #e5e9ef;text-align:left}
 th{background:#f7f9fc;color:#7b8794;font-weight:600}
 .ok{color:#16a34a}.warn{color:#d97706}.err{color:#dc2626}
</style></head><body>
""");

        sb.AppendLine("<h1>DevUp 驱动与软件更新报告</h1>");
        sb.AppendLine($"<p>生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　计算机：{Environment.MachineName}</p>");

        sb.AppendLine("<div class=\"cards\">");
        sb.AppendLine($"<div class=\"card\"><b>{state.Summary.DeviceTotal}</b><span>设备总数</span></div>");
        sb.AppendLine($"<div class=\"card\"><b class=\"err\">{state.Summary.DeviceProblem}</b><span>设备问题</span></div>");
        sb.AppendLine($"<div class=\"card\"><b>{state.Summary.DriverPackages}</b><span>驱动包</span></div>");
        sb.AppendLine($"<div class=\"card\"><b class=\"warn\">{state.Summary.DriverUpdates}</b><span>驱动更新</span></div>");
        sb.AppendLine($"<div class=\"card\"><b class=\"warn\">{state.Summary.SoftwareUpdates}</b><span>软件更新</span></div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>有问题的设备</h2><table><tr><th>设备</th><th>类别</th><th>问题</th><th>当前驱动</th><th>厂商</th></tr>");
        foreach (var d in state.Devices.Where(x => x.HasProblem))
        {
            sb.AppendLine($"<tr><td>{Esc(d.FriendlyName)}</td><td>{Esc(d.ClassName)}</td>" +
                          $"<td class=\"err\">{Esc(d.ProblemText)}</td><td>{Esc(d.DriverVersion)}</td>" +
                          $"<td>{Esc(d.DriverProvider)}</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>可用驱动更新</h2><table><tr><th>标题</th><th>型号</th><th>类别</th><th>版本日期</th><th>来源</th></tr>");
        foreach (var u in state.DriverUpdates)
        {
            sb.AppendLine($"<tr><td>{Esc(u.Title)}</td><td>{Esc(u.DriverModel)}</td><td>{Esc(u.DriverClass)}</td>" +
                          $"<td>{Esc(u.DriverVerDate)}</td><td>{Esc(u.Source)}</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>可更新的软件</h2><table><tr><th>名称</th><th>已安装</th><th>可用版本</th><th>来源</th></tr>");
        foreach (var s in state.Software.Where(x => x.HasUpdate))
        {
            sb.AppendLine($"<tr><td>{Esc(s.DisplayName)}</td><td>{Esc(s.DisplayVersion)}</td>" +
                          $"<td class=\"warn\">{Esc(s.AvailableVersion)}</td><td>{Esc(s.Source)}</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("</body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
