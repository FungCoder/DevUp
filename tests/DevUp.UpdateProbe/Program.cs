using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DevUp.Core;

// 仅在隔离测试目录运行，不加载主窗口、不扫描系统、不联网。
if (args.SequenceEqual(new[] { "--updated" }))
{
    using var current = Process.GetCurrentProcess();
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "restarted.json"),
        JsonSerializer.Serialize(new { current.Id, StartedUtcTicks = current.StartTime.ToUniversalTime().Ticks }));
    return;
}
if (!args.SequenceEqual(new[] { "--prepare" }))
    throw new ArgumentException("更新测试宿主只接受专用测试参数。");

PortablePaths.Initialize();
var candidate = Path.Combine(UpdateInstaller.CreateJobDirectory(), "DevUp.new.exe");
File.Copy(Environment.ProcessPath!, candidate);
await using var input = File.OpenRead(candidate);
var digest = Convert.ToHexString(await SHA256.HashDataAsync(input));
await UpdateInstaller.StartAsync(candidate, digest, Assembly.GetExecutingAssembly().GetName().Version!);
// 正常退出，真实 helper 随后备份、替换并以 --updated 重启此无界面测试宿主。
