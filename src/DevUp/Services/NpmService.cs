using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevUp.Core;
using DevUp.Models;

namespace DevUp.Services;

/// <summary>
/// npm 全局包：探测可用的 npm、读取全局包清单、检查最新版本、升级与卸载。
/// 很多 AI 命令行工具（Codex、Claude Code 等）都是通过 npm 全局安装的，这里把它们单独管理。
/// </summary>
public sealed class NpmService
{
    private NpmEnvironment? _environment;

    public void ResetProbe() => _environment = null;

    // ---------------- 环境探测 ----------------

    public async Task<NpmEnvironment> ProbeAsync(CancellationToken ct = default)
    {
        if (_environment is { Available: true }) return _environment;

        var nodeVersion = await ReadFirstVersionAsync("node", "-v", ct);
        var candidates = FindCandidates();
        var errors = new List<string>();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var (code, output) = await RunAsync(candidate, new[] { "--version" }, TimeSpan.FromSeconds(20), ct);
            var version = FirstVersion(output);
            if (code == 0 && version is not null)
            {
                var (rootCode, rootOutput) = await RunAsync(candidate, new[] { "root", "-g" }, TimeSpan.FromSeconds(30), ct);
                var root = rootCode == 0 ? LastMeaningfulLine(rootOutput) : "";

                _environment = new NpmEnvironment
                {
                    Available = true,
                    NpmPath = candidate,
                    NpmVersion = version,
                    NodeVersion = nodeVersion,
                    GlobalRoot = root
                };
                Ui.Log($"npm 可用：{candidate}（npm {version} / node {nodeVersion}）");
                return _environment;
            }

            var reason = FirstMeaningfulLine(output);
            if (!string.IsNullOrWhiteSpace(reason)) errors.Add($"{candidate}：{reason}");
        }

        _environment = new NpmEnvironment
        {
            Available = false,
            NodeVersion = nodeVersion,
            NpmBroken = candidates.Count > 0,
            Error = candidates.Count == 0
                ? (string.IsNullOrWhiteSpace(nodeVersion)
                    ? "没有检测到 Node.js，也没有找到 npm。"
                    : "检测到 Node.js，但没有找到 npm。")
                : string.IsNullOrWhiteSpace(nodeVersion)
                    ? "检测到了残留的 npm 命令，但没有检测到 Node.js。建议重新安装官方 Node.js。"
                    : "检测到了 npm 命令，但它无法正常运行。常见原因是 Node.js 安装不完整，或 npm 文件被清理软件破坏。",
            Detail = string.Join("\n", errors.Take(3))
        };
        Ui.Log("npm 环境不可用：" + _environment.Error, "WARN");
        return _environment;
    }

    private static List<string> FindCandidates()
    {
        var list = new List<string>();

        void AddDir(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var path = Path.Combine(dir.Trim().Trim('"'), "npm.cmd");
                if (File.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                    list.Add(path);
            }
            catch { /* 非法 PATH 项直接跳过 */ }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            AddDir(dir);

        AddDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        AddDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
        AddDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"));

        return list;
    }

    private static async Task<string> ReadFirstVersionAsync(string fileName, string arg, CancellationToken ct)
    {
        try
        {
            var (code, output) = await RunAsync(fileName, new[] { arg }, TimeSpan.FromSeconds(15), ct);
            return code == 0 ? FirstVersion(output) ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    // ---------------- 全局包列表 ----------------

    public async Task<(List<NpmPackageItem> Packages, string Error)> GetPackagesAsync(
        NpmEnvironment env, CancellationToken ct = default)
    {
        if (!env.Available) return (new List<NpmPackageItem>(), env.Error);

        var (_, output) = await RunAsync(env.NpmPath,
            new[] { "ls", "-g", "--depth=0", "--json", "--no-color" },
            TimeSpan.FromSeconds(90), ct);

        using var doc = TryParseJson(output);
        if (doc is null)
            return (new List<NpmPackageItem>(), FriendlyOutput(output, "读取 npm 全局包列表失败。"));

        var packages = new List<NpmPackageItem>();
        if (!doc.RootElement.TryGetProperty("dependencies", out var deps) ||
            deps.ValueKind != JsonValueKind.Object)
            return (packages, "");

        foreach (var prop in deps.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;

            var name = prop.Name;
            var version = GetString(prop.Value, "version");
            var location = CombineLocation(env.GlobalRoot, name);
            var (description, homepage) = ReadPackageManifest(location);

            packages.Add(new NpmPackageItem
            {
                Name = name,
                InstalledVersion = string.IsNullOrWhiteSpace(version) ? "—" : version,
                Location = location,
                Description = description,
                Homepage = homepage
            });
        }

        packages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return (packages, "");
    }

    /// <summary>
    /// 向 npm 源检查全局包最新版本。npm outdated 有更新时退出码为 1，
    /// 但 stdout 仍是合法 JSON，因此这里只以 JSON 是否可解析为准。
    /// </summary>
    public async Task<(bool Checked, string Error)> CheckOutdatedAsync(
        NpmEnvironment env, IEnumerable<NpmPackageItem> packages, CancellationToken ct = default)
    {
        if (!env.Available) return (false, env.Error);

        var (_, output) = await RunAsync(env.NpmPath,
            new[] { "outdated", "-g", "--json", "--no-color" },
            TimeSpan.FromSeconds(150), ct);

        using var doc = TryParseJson(output);
        if (doc is null)
            return (false, FriendlyOutput(output, "连接 npm 源检查更新失败。"));

        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var value = GetString(prop.Value, "latest");
                if (string.IsNullOrWhiteSpace(value)) value = GetString(prop.Value, "wanted");
                if (!string.IsNullOrWhiteSpace(value)) latest[prop.Name] = value;
            }
        }

        foreach (var package in packages)
        {
            package.LatestVersion = latest.TryGetValue(package.Name, out var version)
                ? version
                : package.InstalledVersion;
            package.Checked = true;
        }

        return (true, "");
    }

    // ---------------- 升级 / 卸载 ----------------

    public async Task<(int Code, string Output)> UpdateAsync(
        NpmEnvironment env, string packageName, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        Ui.Log($"npm 升级：{packageName}");
        var started = Stopwatch.StartNew();
        var result = await RunWithProgressAsync(env.NpmPath,
            new[] { "install", "-g", $"{packageName}@latest", "--no-fund", "--no-audit", "--no-color" },
            TimeSpan.FromMinutes(15), ct, progress,
            $"正在升级 {packageName}…", "正在连接 npm 源并准备安装…");

        if (progress is not null)
        {
            progress.Report(new ProgressInfo
            {
                Percent = 100,
                Status = result.Code == 0 ? $"{packageName} 升级完成" : $"{packageName} 升级未完成",
                Detail = $"已用 {FormatElapsed(started.Elapsed)}",
                CanCancel = false
            });
        }

        return result;
    }

    public async Task<(int Code, string Output)> UninstallAsync(
        NpmEnvironment env, string packageName, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        Ui.Log($"npm 卸载：{packageName}");
        var started = Stopwatch.StartNew();
        var result = await RunWithProgressAsync(env.NpmPath,
            new[] { "uninstall", "-g", packageName, "--no-fund", "--no-audit", "--no-color" },
            TimeSpan.FromMinutes(5), ct, progress,
            $"正在卸载 {packageName}…", "正在删除全局包文件…");

        if (progress is not null)
        {
            progress.Report(new ProgressInfo
            {
                Percent = 100,
                Status = result.Code == 0 ? $"{packageName} 卸载完成" : $"{packageName} 卸载未完成",
                Detail = $"已用 {FormatElapsed(started.Elapsed)}",
                CanCancel = false
            });
        }

        return result;
    }

    /// <summary>
    /// npm 本身不提供可解析的百分比，这里用“已用时间 + 阶段”给出平滑的确定进度，
    /// 并同时把已用时间写进详情行，避免进度条长时间停着不动。
    /// </summary>
    private static async Task<(int Code, string Output)> RunWithProgressAsync(
        string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
        IProgress<ProgressInfo>? progress, string status, string initialDetail)
    {
        var started = Stopwatch.StartNew();
        var gate = new object();
        var percent = 1.0;
        var detail = initialDetail;
        var lastLineTicks = 0L;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = progress is null ? Task.CompletedTask : Task.Run(async () =>
        {
            while (!heartbeatCts.IsCancellationRequested)
            {
                var elapsed = started.Elapsed;
                // 前 40 秒推进较快，之后逐渐放慢，最高停在 94%，完成时再由调用方置为 100%
                var pct = 3 + 91 * (elapsed.TotalSeconds / (elapsed.TotalSeconds + 40));
                if (pct > 94) pct = 94;

                string current;
                lock (gate)
                {
                    percent = pct;
                    current = detail;
                }

                progress.Report(new ProgressInfo
                {
                    Percent = pct,
                    Status = status,
                    Detail = $"已用 {FormatElapsed(elapsed)} · {current}",
                    CanCancel = true
                });

                try { await Task.Delay(500, heartbeatCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });

        try
        {
            progress?.Report(new ProgressInfo
            {
                Percent = 1,
                Status = status,
                Detail = $"已用 00:00 · {initialDetail}",
                CanCancel = true
            });

            return await RunAsync(fileName, args, timeout, ct, line =>
            {
                var clean = Truncate(Clean(line), 120);
                if (clean.Length == 0) return;

                lock (gate) detail = clean;

                // 输出行可能非常密集，限制刷新频率，避免刷爆 UI 线程
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref lastLineTicks);
                if (now - last < 150 || Interlocked.CompareExchange(ref lastLineTicks, now, last) != last)
                    return;

                double pct;
                lock (gate) pct = percent;
                progress?.Report(new ProgressInfo
                {
                    Percent = pct,
                    Status = status,
                    Detail = $"已用 {FormatElapsed(started.Elapsed)} · {clean}",
                    CanCancel = true
                });
            });
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; } catch { /* 取消心跳时的正常异常 */ }
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    public static NpmFailure ExplainFailure(string output)
    {
        var text = StripAnsi(output ?? "");
        var lower = text.ToLowerInvariant();
        var detail = LastErrorLines(text);

        if (lower.Contains("eacces") || lower.Contains("eperm") || lower.Contains("permission denied"))
            return new NpmFailure
            {
                Reason = "没有写入 npm 全局目录的权限。",
                Hint = "请以管理员身份重启 DevUp 后再试；或把 npm 全局目录改到当前用户目录。",
                Detail = detail
            };

        if (lower.Contains("enotfound") || lower.Contains("etimedout") || lower.Contains("econnreset") ||
            lower.Contains("network") || lower.Contains("socket hang up"))
            return new NpmFailure
            {
                Reason = "无法连接 npm 软件源。",
                Hint = "请检查网络或代理设置，稍后点「检查更新」重试。",
                Detail = detail
            };

        if (lower.Contains("e404") || lower.Contains("404 not found"))
            return new NpmFailure
            {
                Reason = "npm 源里找不到这个包或版本。",
                Hint = "包可能已改名或下架，可打开 npm 包主页确认后再试。",
                Detail = detail
            };

        if (lower.Contains("ebusy") || lower.Contains("resource busy") || lower.Contains("being used by another process"))
            return new NpmFailure
            {
                Reason = "相关文件正被其他程序占用。",
                Hint = "请关闭正在使用这个包的程序（例如对应的 AI 命令行工具）后重试。",
                Detail = detail
            };

        return new NpmFailure
        {
            Reason = "npm 没有完成这次操作。",
            Hint = "可稍后重试；若反复失败，请检查 Node.js / npm 安装是否完整，或打开 npm 包主页查看说明。",
            Detail = detail
        };
    }

    // ---------------- 进程执行 ----------------

    private static async Task<(int Code, string Output)> RunAsync(
        string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
        Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var output = new StringBuilder();
        var gate = new object();

        void Append(string? line)
        {
            if (line is null) return;
            lock (gate) output.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line)) onLine?.Invoke(line);
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, "无法启动 npm：" + ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(3000); } catch { }

            if (ct.IsCancellationRequested) throw;

            lock (gate) output.AppendLine("操作超时，已停止 npm 进程。");
            return (-1, output.ToString());
        }

        // 确保异步输出事件已经全部派发完
        try { process.WaitForExit(); } catch { }

        var text = output.ToString();
        return (process.ExitCode, text);
    }

    // ---------------- 解析辅助 ----------------

    private static JsonDocument? TryParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try { return JsonDocument.Parse(text[start..(end + 1)]); }
        catch { return null; }
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string CombineLocation(string globalRoot, string packageName)
    {
        if (string.IsNullOrWhiteSpace(globalRoot)) return "";

        try
        {
            return Path.Combine(globalRoot,
                packageName.Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            return "";
        }
    }

    private static (string Description, string Homepage) ReadPackageManifest(string packageDir)
    {
        if (string.IsNullOrWhiteSpace(packageDir)) return ("", "");
        try
        {
            var manifest = Path.Combine(packageDir, "package.json");
            if (!File.Exists(manifest)) return ("", "");

            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            return (GetString(root, "description"), GetString(root, "homepage"));
        }
        catch
        {
            return ("", "");
        }
    }

    private static readonly Regex VersionRegex =
        new(@"(?<!\d)(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?)(?!\d)", RegexOptions.Compiled);

    private static string? FirstVersion(string text)
    {
        var match = VersionRegex.Match(text ?? "");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static readonly Regex AnsiRegex =
        new("\u001B\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    private static string StripAnsi(string text) => AnsiRegex.Replace(text, "");

    private static string Clean(string line) => StripAnsi(line).Replace("\r", "").Trim();

    private static string FirstMeaningfulLine(string text)
        => (text ?? "").Split('\n')
            .Select(Clean)
            .FirstOrDefault(x => x.Length > 0) ?? "";

    private static string LastMeaningfulLine(string text)
        => (text ?? "").Split('\n')
            .Select(Clean)
            .LastOrDefault(x => x.Length > 0) ?? "";

    private static string FriendlyOutput(string output, string fallback)
    {
        var text = Clean(output);
        if (text.Length == 0) return fallback;

        var error = LastErrorLines(text);
        return string.IsNullOrWhiteSpace(error) ? fallback : fallback + "\n" + error;
    }

    private static string LastErrorLines(string text)
    {
        var lines = text.Split('\n')
            .Select(Clean)
            .Where(x => x.Length > 0)
            .Where(x => x.Contains("npm error", StringComparison.OrdinalIgnoreCase)
                        || x.Contains("npm ERR", StringComparison.OrdinalIgnoreCase)
                        || x.Contains("ERR!", StringComparison.OrdinalIgnoreCase)
                        || x.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(5)
            .ToArray();

        if (lines.Length == 0)
        {
            lines = text.Split('\n').Select(Clean).Where(x => x.Length > 0).TakeLast(3).ToArray();
        }

        return Truncate(string.Join("\n", lines), 600);
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}

public sealed class NpmFailure
{
    public string Reason { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Detail { get; init; } = "";
}
