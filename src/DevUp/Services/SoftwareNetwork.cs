using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;

namespace DevUp.Services;

/// <summary>软件更新专用的代理、连接测试及网络错误说明。</summary>
internal static class SoftwareNetwork
{
    internal const string DefaultMode = "default", DirectMode = "direct", CustomMode = "custom";
    internal const string Hint = "部分软件的安装包托管在境外，当前网络可能无法连接或速度较慢。" +
        "可到「备份与工具 → 网络与下载」测试连接、配置可用代理，或换一个网络后重试；" +
        "也可打开官方下载页获取安装包。仅凭超时无法确定是否为境外网络限制。";

    internal static string ValidateMode(string mode) => mode is DefaultMode or DirectMode or CustomMode
        ? mode : throw new ArgumentException("请选择有效的网络连接方式。");

    internal static Uri ValidateProxy(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("代理地址须为 http://主机:端口 或 https://主机:端口，不能包含账号、密码或路径。" +
                "本地代理软件请填写其 HTTP 代理端口。");
        return uri;
    }

    internal static HttpClient CreateClient(AppSettings settings)
    {
        var mode = ValidateMode(settings.SoftwareProxyMode);
        var handler = new HttpClientHandler { UseProxy = mode != DirectMode };
        if (mode == CustomMode) handler.Proxy = new WebProxy(ValidateProxy(settings.SoftwareProxyAddress));
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DevUp/1.2");
        return client;
    }

    internal static List<string> GetWingetArguments(AppSettings settings)
    {
        return ValidateMode(settings.SoftwareProxyMode) switch
        {
            DirectMode => new() { "--no-proxy" },
            CustomMode => new() { "--proxy", ValidateProxy(settings.SoftwareProxyAddress).AbsoluteUri },
            _ => new()
        };
    }

    internal static async Task<List<string>> PrepareWingetAsync(AppSettings settings, CancellationToken ct)
    {
        var args = GetWingetArguments(settings);
        if (args.Count == 0) return args;
        var start = new ProcessStartInfo("winget.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--help");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 winget，请更新系统的「应用安装程序」。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var help = await output + await error;
            if (process.ExitCode != 0 || !help.Contains(args[0], StringComparison.Ordinal))
                throw new InvalidOperationException("当前 winget 不支持所选代理方式，请先更新系统的「应用安装程序」，" +
                    "或在「网络与下载」中选择默认连接方式后重试。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("检查 winget 代理支持超时，请稍后重试。");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        return args;
    }

    internal static string Explain(Exception error, string? url = null)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"（{uri.Host}）" : "";
        var reason = error switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired } =>
                "代理服务器要求身份验证，请检查代理配置。",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                "下载服务器拒绝了访问（HTTP 403），可能存在访问策略或链接限制。",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                "下载地址不存在（HTTP 404），软件源中的链接可能已失效。",
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                "无法解析下载源或代理服务器的域名，请检查网络、DNS 和代理地址。",
            HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } =>
                "无法通过代理建立连接，请检查代理服务和端口。",
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
                "安全连接未能建立，请检查系统时间、证书及代理配置。",
            TimeoutException or OperationCanceledException => "连接超时或长时间未收到下载数据。",
            _ => "连接未能完成，下载源或代理服务可能暂时不可达。"
        };
        return $"无法连接或下载安装包{host}：{reason}\n\n{Hint}";
    }

    internal static async Task<string> TestAsync(AppSettings settings, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请输入不包含账号密码的 HTTPS 下载地址。");
        using var client = CreateClient(settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            return $"已连接 {uri.Host}（HTTP {(int)response.StatusCode}）。\n" +
                "测试仅检查该地址的响应，不下载完整安装包，也不代表所有软件或后续重定向均可访问。";
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Explain(ex, url);
        }
    }
}
