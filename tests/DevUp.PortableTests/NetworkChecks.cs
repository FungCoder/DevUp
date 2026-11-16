using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevUp.Services;
using DevUp.Views;

internal static class NetworkChecks
{
    public static void RunUi()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new DevUp.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                app.InitializeComponent();
                var view = new ToolsView();
                var modes = (ComboBox)view.FindName("CmbSoftwareProxy");
                var address = (TextBox)view.FindName("SoftwareProxyAddress");
                modes.SelectedIndex = 2;
                if (!address.IsEnabled) throw new InvalidOperationException("自定义代理输入框没有启用");
                modes.SelectedIndex = 1;
                if (address.IsEnabled) throw new InvalidOperationException("直连仍允许填写代理");
                modes.SelectedIndex = 0;
                view.Measure(new Size(950, 800));
                view.Arrange(new Rect(0, 0, 950, 800));
                view.UpdateLayout();
                var bitmap = new RenderTargetBitmap(950, 800, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var preview = Path.Combine(Path.GetTempPath(), "devup-network-preview.png");
                using var output = File.Create(preview);
                encoder.Save(output);
                Console.WriteLine("软件网络界面检查通过，预览：" + preview);
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("网络设置界面检查失败", failure);
    }

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }

        foreach (var address in new[] { "socks5://localhost:1080", "http://user:password@localhost:8888",
                     "http://localhost:8888/path", "http://localhost:8888/?token=secret", "invalid" })
        {
            var rejected = false;
            try { SoftwareNetwork.ValidateProxy(address); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "代理边界校验未拒绝凭据、路径或无效协议");
        }
        var old = JsonSerializer.Deserialize<AppSettings>("{\"DownloadRoot\":\"downloads\"}")!;
        Check(SoftwareNetwork.GetWingetArguments(old).Count == 0, "旧配置改变了默认连接行为");
        var settings = new AppSettings { SoftwareProxyMode = "custom", SoftwareProxyAddress = "http://127.0.0.1:7890" };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Check(restored.SoftwareProxyAddress == settings.SoftwareProxyAddress && restored.SoftwareProxyMode == "custom",
            "代理配置无法序列化往返");

        var dns = new HttpRequestException(HttpRequestError.NameResolutionError, "raw DNS error");
        var message = SoftwareNetwork.Explain(dns, "https://example.invalid/setup.exe?token=secret");
        Check(message.Contains("域名") && message.Contains("example.invalid") && !message.Contains("secret"),
            "域名诊断缺失或泄露 URL 查询参数");
        Check(SoftwareNetwork.Explain(new HttpRequestException("403", null, HttpStatusCode.Forbidden)).Contains("403"),
            "服务器拒绝未被识别");
        Check(SoftwareNetwork.Explain(new TimeoutException()).Contains("仅凭超时无法确定"), "超时被错误断定为境外限制");
        foreach (var code in new uint[] { 0x80072EE2, 0x80072EE7, 0x80072EFD, 0x80190193, 0x8A150008 })
        {
            var failure = SoftwareService.ExplainFailure(unchecked((int)code), "raw error");
            Check(failure.CanRetryDownload && failure.Hint.Contains("网络与下载"), "winget 网络故障缺少可操作提示");
        }
        Check(!SoftwareService.ExplainFailure(unchecked((int)0x8A15002D), "hash mismatch").CanRetryDownload,
            "哈希错误被错误当成网络错误");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        settings.SoftwareProxyAddress = $"http://127.0.0.1:{port}";
        var requestLine = "";
        async Task RespondAsync()
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            requestLine = await reader.ReadLineAsync(timeout.Token) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), timeout.Token);
        }
        var server = RespondAsync();
        using (var client = SoftwareNetwork.CreateClient(settings))
            Check(await client.GetStringAsync("http://example.invalid/setup.exe", timeout.Token) == "OK", "自定义代理下载未成功");
        await server;
        Check(requestLine.StartsWith("GET http://example.invalid/setup.exe "), "安装包请求没有实际经过配置的代理");

        settings.SoftwareProxyMode = "direct";
        settings.SoftwareProxyAddress = "invalid proxy ignored in direct mode";
        server = RespondAsync();
        using (var client = SoftwareNetwork.CreateClient(settings))
            Check(await client.GetStringAsync($"http://127.0.0.1:{port}/setup.exe", timeout.Token) == "OK", "直连仍使用了代理");
        await server;
        Check(requestLine.StartsWith("GET /setup.exe "), "直连请求格式错误");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationObserved = false;
        try { await SoftwareNetwork.TestAsync(settings, "https://example.invalid/", cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved, "取消连接测试被错误显示为网络故障");

        settings.SoftwareProxyMode = "custom";
        settings.SoftwareProxyAddress = $"http://127.0.0.1:{port}";
        server = RespondAsync();
        var testFailure = await SoftwareNetwork.TestAsync(settings, "https://example.invalid/setup.exe", timeout.Token);
        await server;
        Check(requestLine.StartsWith("CONNECT example.invalid:443 ") && testFailure.Contains("代理"),
            "HTTPS 代理隧道故障未显示友好提醒");
        Console.WriteLine($"软件网络检查通过：{checks} 项。");
    }
}
