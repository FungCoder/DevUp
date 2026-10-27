using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevUp.Core;
using DevUp.Models;
using Microsoft.Win32;

namespace DevUp.Services;

/// <summary>已安装软件清单（注册表 ARP + MSIX）与更新（复用官方 winget）。</summary>
public sealed class SoftwareService
{
    /// <summary>扫描注册表“应用和功能”条目。</summary>
    public static List<SoftwareItem> ScanRegistry()
    {
        var result = new List<SoftwareItem>();
        var views = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32)
        };

        foreach (var (hive, view) in views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        if (key is null) continue;

                        var displayName = key.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        // 过滤补丁/更新条目与系统组件
                        if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent)) continue;
                        if (displayName.Contains("Update for", StringComparison.OrdinalIgnoreCase) &&
                            displayName.Contains("KB", StringComparison.OrdinalIgnoreCase)) continue;

                        var installDate = key.GetValue("InstallDate") as string ?? "";
                        if (installDate.Length == 8 && int.TryParse(installDate, out var n))
                        {
                            try
                            {
                                installDate = new DateTime(n / 10000, (n / 100 % 100), n % 100).ToString("yyyy-MM-dd");
                            }
                            catch { installDate = ""; }
                        }

                        result.Add(new SoftwareItem
                        {
                            DisplayName = displayName.Trim(),
                            DisplayVersion = (key.GetValue("DisplayVersion") as string ?? "").Trim(),
                            Publisher = (key.GetValue("Publisher") as string ?? "").Trim(),
                            InstallDate = installDate,
                            UninstallString = (key.GetValue("UninstallString") as string ?? "").Trim(),
                            Source = hive == RegistryHive.CurrentUser ? "注册表(当前用户)" : "注册表(计算机)",
                            Scope = view == RegistryView.Registry32 ? "32 位" : "64 位",
                            Key = $"{hive}|{view}|{name}"
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        return result
            .GroupBy(x => x.DisplayName + "|" + x.DisplayVersion)
            .Select(g => g.First())
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>扫描 Microsoft Store / MSIX 应用（复用系统 Get-AppxPackage）。</summary>
    public async Task<List<SoftwareItem>> ScanMsixAsync(CancellationToken ct = default)
    {
        var result = new List<SoftwareItem>();
        try
        {
            var json = await PsBridge.RunAsync("msixApps", null, false, ct);
            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("apps", out var arr))
            {
                foreach (var e in arr.EnumerateArray())
                {
                    result.Add(new SoftwareItem
                    {
                        DisplayName = DriverUpdateService.Str(e, "name"),
                        DisplayVersion = DriverUpdateService.Str(e, "version"),
                        Publisher = DriverUpdateService.Str(e, "publisher"),
                        Source = "Microsoft Store",
                        Key = "msix|" + DriverUpdateService.Str(e, "fullName")
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"读取 Microsoft Store 应用失败：{ex.Message}", ex);
        }
        return result;
    }

    /// <summary>winget 已识别的软件及其可用更新，保留无更新条目的包 ID。</summary>
    public async Task<List<SoftwareUpdateItem>> GetUpdatesAsync(CancellationToken ct = default)
    {
        var result = new List<SoftwareUpdateItem>();
        var json = await PsBridge.RunAsync("softwareUpdates", null, false, ct);
        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("updates", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                result.Add(new SoftwareUpdateItem
                {
                    Name = DriverUpdateService.Str(e, "name"),
                    Id = DriverUpdateService.Str(e, "id"),
                    InstalledVersion = DriverUpdateService.Str(e, "installedVersion"),
                    AvailableVersion = DriverUpdateService.Str(e, "availableVersion"),
                    Source = DriverUpdateService.Str(e, "source")
                });
            }
        }
        return result;
    }

    /// <summary>
    /// 读取 winget 官方清单中指定版本的元数据（发行说明、发布日期、安装包地址、SHA256）。
    /// 标签随系统语言变化，解析时同时兼容中英文；取值缺失就留空，不伪造。
    /// </summary>
    public async Task<WingetPackageDetails?> GetPackageDetailsAsync(
        string packageId, string? version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return null;

        var text = await ShowAsync(packageId, version, ct);
        var fallback = false;
        if (LooksNotFound(text) && !string.IsNullOrWhiteSpace(version))
        {
            // 清单里没有这个版本（例如刚被下架）时，退回读取最新版本，并在界面标注。
            text = await ShowAsync(packageId, null, ct);
            fallback = true;
        }
        if (LooksNotFound(text) || string.IsNullOrWhiteSpace(text))
        {
            var reason = string.IsNullOrWhiteSpace(text)
                ? "无输出或超时"
                : text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (reason.Length > 160) reason = reason[..160] + " …";
            Ui.Log($"读取 winget 清单失败（{packageId} {version}）：{reason}", "WARN");
            return null;
        }

        var map = ParseShowFields(text);
        string? Get(params string[] keys)
        {
            foreach (var key in keys)
                if (map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
            return null;
        }

        var details = new WingetPackageDetails
        {
            Id = packageId,
            Version = Get("版本", "Version") ?? version ?? "",
            Publisher = Get("发布者", "Publisher") ?? "",
            InstallerType = Get("安装程序类型", "Installer Type") ?? "",
            InstallerUrl = Get("安装程序 URL", "安装程序 Url", "Installer Url", "Installer URL") ?? "",
            Sha256 = Get("安装程序 SHA256", "Installer SHA256") ?? "",
            ReleaseDate = Get("发布日期", "Release Date") ?? "",
            ReleaseNotes = Get("发行说明", "Release Notes") ?? "",
            ReleaseNotesUrl = Get("发行说明 URL", "发行说明 Url", "Release Notes Url", "Release Notes URL") ?? "",
            Homepage = Get("主页", "Homepage") ?? ""
        };

        if (fallback)
            Ui.Log($"winget 清单中没有 {packageId} {version}，已改为读取最新版本 {details.Version}", "WARN");

        return details;
    }

    private static async Task<string> ShowAsync(string packageId, string? version, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in new[] { "show", "--id", packageId, "--exact", "--accept-source-agreements", "--disable-interactivity" })
            psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(version))
        {
            psi.ArgumentList.Add("--version");
            psi.ArgumentList.Add(version!);
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
            var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errorTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // 超时或用户切换了选中项：都要结束子进程，避免 winget 残留在后台
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return "";
            }

            var text = await outputTask;
            var err = await errorTask;
            return string.IsNullOrWhiteSpace(text) ? err : text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Ui.Log($"读取 winget 清单失败（{packageId}）: {ex.Message}", "WARN");
            return "";
        }
    }

    private static bool LooksNotFound(string text)
        => text.Contains("找不到与输入条件匹配", StringComparison.Ordinal)
           || text.Contains("未找到与输入条件匹配", StringComparison.Ordinal)
           || text.Contains("No package found", StringComparison.OrdinalIgnoreCase)
           || text.Contains("No installed package found", StringComparison.OrdinalIgnoreCase);

    /// <summary>把 `winget show` 的文本输出解析为“标签 → 值”。安装程序是缩进小节，也一并展开。</summary>
    private static Dictionary<string, string> ParseShowFields(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var labels = new[]
        {
            "版本", "Version", "发布者", "Publisher", "作者", "Author",
            "安装程序类型", "Installer Type", "安装程序 URL", "安装程序 Url", "Installer Url", "Installer URL",
            "安装程序 SHA256", "Installer SHA256", "发布日期", "Release Date",
            "发行说明", "Release Notes", "发行说明 URL", "发行说明 Url", "Release Notes Url", "Release Notes URL",
            "主页", "Homepage", "许可证", "License", "描述", "Description"
        };

        string? pendingKey = null;
        var pending = new System.Text.StringBuilder();

        void Commit()
        {
            if (pendingKey is null) return;
            var value = pending.ToString().Trim();
            if (value.Length > 0) map[pendingKey] = value;
            pendingKey = null;
            pending.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimStart('\uFEFF');

            // 发行说明常以“发行说明:”开头，正文是缩进的多个段落，直到下一个顶层字段
            if (pendingKey is not null)
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                {
                    Commit();
                }
                else
                {
                    pending.AppendLine(line.Trim());
                    continue;
                }
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0
                || trimmed.StartsWith("已找到", StringComparison.Ordinal)
                || trimmed.StartsWith("Found ", StringComparison.Ordinal)) continue;

            foreach (var label in labels)
            {
                if (!trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = trimmed[label.Length..].TrimStart();
                if (rest.Length == 0 || rest[0] is not (':' or '：')) break;

                var value = rest[1..].Trim();
                if (value.Length > 0)
                    map[label] = value;
                else if (label is "发行说明" or "Release Notes")
                    pendingKey = label;
                break;
            }
        }

        Commit();
        return map;
    }

    /// <summary>计算本地安装包的 SHA256 与 MD5，并与官方清单值比对（忽略大小写与连字符）。</summary>
    public static async Task<FileHashResult> ComputeFileHashesAsync(
        string path, string? officialSha256, CancellationToken ct = default)
    {
        var result = new FileHashResult { Path = path };

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var shaBytes = await sha.ComputeHashAsync(stream, ct);
        result.Sha256 = Convert.ToHexString(shaBytes);

        stream.Position = 0;
        using var md5 = MD5.Create();
        var md5Bytes = await md5.ComputeHashAsync(stream, ct);
        result.Md5 = Convert.ToHexString(md5Bytes);

        if (!string.IsNullOrWhiteSpace(officialSha256))
        {
            var official = NormalizeHex(officialSha256);
            result.MatchesOfficial = official.Length > 0
                                     && string.Equals(official, result.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string NormalizeHex(string value)
        => new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>把 winget 的退出码翻译成用户能看懂的原因与下一步建议。</summary>
    public sealed record WingetFailureInfo(
        long Code,
        string CodeText,
        string Title,
        string Reason,
        string Hint,
        bool CanRetryAsInstall,
        bool CanOpenInstaller,
        string Detail)
    {
        public bool CanRetryDownload { get; init; }
    }

    /// <summary>
    /// 解析 winget 失败原因（错误码来自 AppInstallerErrors.h）。
    /// 原始输出只作为次要详情保留，避免把 winget 的英文/命令行日志直接甩给用户。
    /// </summary>
    public static WingetFailureInfo ExplainFailure(int exitCode, string output)
    {
        var raw = unchecked((uint)exitCode);
        var codeText = $"0x{raw:X8}";
        var detail = FirstMeaningfulLines(output);

        var info = raw switch
        {
            0xFFFFFFFE => new WingetFailureInfo(exitCode, codeText, "更新长时间无进展",
                "winget 在下载或准备阶段长时间没有可观察到的进展，已停止本次操作。",
                "请检查网络或代理后重试；若下载源仍不可达，可打开官方下载页手动获取安装包。",
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail)
                { CanRetryDownload = true },

            0xFFFFFFFD => new WingetFailureInfo(exitCode, codeText, "安装阶段超时",
                "安装包已校验或安装器已启动，但长时间没有返回完成结果。",
                "请先检查软件是否已安装成功，再决定是否重新升级，避免重复运行安装程序。",
                CanRetryAsInstall: false, CanOpenInstaller: false, Detail: detail),

            // 清单里的新版本与已安装版本类型/范围不一致，winget 拒绝升级
            0x8A15008E => new WingetFailureInfo(exitCode, codeText, "winget 无法直接升级",
                "新版本的安装包与当前已安装的版本不匹配：常见原因是安装范围（所有用户 / 当前用户）" +
                "或安装方式（MSI / 便携版 / 独立安装程序）发生了变化。winget 为了避免装出第二份程序或破坏现有安装，" +
                "这次没有执行升级。",
                "这通常是软件方提交给 winget 的清单标注问题，不是你的操作失误。可以改用『重试安装』按新版本的安装包重新安装；" +
                "如果新版本改成了便携版，更建议打开官方安装包手动安装。",
                CanRetryAsInstall: true, CanOpenInstaller: true, Detail: detail),

            // 存在更高版本，但清单里没有可用于当前系统的更新（常见于商店应用/架构不匹配）
            0x8A15002B => new WingetFailureInfo(exitCode, codeText, "没有可用的升级包",
                "winget 找到了这个软件，但清单里当前系统可用的版本并不比你已装的版本更新，" +
                "或者该版本只提供给了其他架构/其他安装范围。",
                "可以稍后重新检查更新；如果软件方已经发布新版本，通常是源还没同步，" +
                "也可以打开官方安装包下载地址手动安装最新版。",
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail),

            // ShellExecute 启动安装程序失败
            0x8A150006 => new WingetFailureInfo(exitCode, codeText, "安装程序没能启动",
                "winget 已经下载好安装包，但安装程序启动失败。常见原因：安装包被安全软件拦截、" +
                "安装程序需要交互界面，或系统的 Windows Installer 服务正忙。",
                "可以稍后重试，或打开官方安装包手动安装。",
                CanRetryAsInstall: true, CanOpenInstaller: true, Detail: detail),

            // 安装包安全校验 / 哈希不符
            0x8A15002D or 0x8A150011 => new WingetFailureInfo(exitCode, codeText, "安装包校验未通过",
                "下载到的安装包与 winget 清单记录的哈希不一致，可能是下载不完整、源缓存过期，或被网络代理篡改。",
                "请稍后重试；如果仍然失败，请打开官方安装包手动安装并核对文件校验值。",
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail),

            // 下载失败 / 大小不符
            0x8A150008 or 0x8A15002E => new WingetFailureInfo(exitCode, codeText, "安装包下载失败",
                "安装包没有下载完整，或下载源在传输过程中断开了连接。",
                "检查网络后重试，或打开官方安装包手动下载。",
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail)
                { CanRetryDownload = true },

            // 需要管理员权限
            0x8A150019 => new WingetFailureInfo(exitCode, codeText, "需要管理员权限",
                "这个安装程序要求管理员权限，但当前权限不足，winget 无法继续。",
                "请在设置中改为『以管理员身份启动』后重新升级，或打开官方安装包右键以管理员身份运行。",
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail),

            // 被系统策略阻止
            0x8A15003A or 0x8A15001B or 0x8A15001C => new WingetFailureInfo(exitCode, codeText, "被系统策略阻止",
                "系统策略或应用控制（如 AppLocker、Smart App Control）阻止了这次安装。",
                "请联系管理员调整策略，或使用软件自带的更新功能。",
                CanRetryAsInstall: false, CanOpenInstaller: false, Detail: detail),

            // 批量升级中个别失败
            0x8A15002C => new WingetFailureInfo(exitCode, codeText, "部分软件升级失败",
                "winget 在批量升级时遇到了无法处理的安装包。",
                "请逐个重试失败的软件，或打开官方安装包手动安装。",
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail),

            // 没有适用的安装程序
            0x8A150010 => new WingetFailureInfo(exitCode, codeText, "没有适用的安装包",
                "winget 源中这个版本没有适配当前系统（架构或系统版本）的安装包。",
                "可以打开软件官网，选择适合当前系统的版本手动安装。",
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail),

            _ => new WingetFailureInfo(exitCode, codeText, "升级未完成",
                $"winget 返回了错误（{codeText}）。",
                "可以重试，或打开官方安装包手动安装。",
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail)
        };

        return info;
    }

    /// <summary>取原始输出的前几行，并过滤掉与本次失败无关的“更新源”告警。</summary>
    private static string FirstMeaningfulLines(string output, int maxLines = 4)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";

        var lines = output
            .Replace("\r", "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Where(l => !l.StartsWith("尝试更新源失败", StringComparison.Ordinal))
            .Where(l => !l.Contains("Failed to update source", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("正在尝试更新源", StringComparison.Ordinal))
            .Take(maxLines);

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 升级仍由 winget 负责选择安装器与静默参数。
    /// 使用隐藏伪终端保留 winget 实时下载计数，正确处理系统传递优化下载。
    /// 升级结束（无论成败）都会清理 winget 留在临时目录里的安装包。
    /// </summary>
    /// <param name="forceVersion">
    /// 传入版本号时改用 <c>winget install --version … --force</c>：绕过“已安装版本与清单不一致”的检查，
    /// 用于 winget upgrade 因安装范围 / 安装器类型不匹配而拒绝升级的软件。
    /// </param>
    public Task<(int ExitCode, string Output)> InstallWithProgressAsync(
        string packageId, IProgress<ProgressInfo>? progress, CancellationToken ct = default,
        string? forceVersion = null)
    {
        var arguments = new List<string>
        {
            forceVersion is null ? "upgrade" : "install", "--id", packageId, "--exact",
            "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity", "--no-vt"
        };
        if (forceVersion is not null) arguments.AddRange(new[] { "--version", forceVersion, "--force" });
        return RunWingetWithProgressAsync(packageId, arguments, progress, ct, cleanupInstallerCache: true);
    }

    /// <summary>升级和只下载验证共用同一个实时进度入口，不改变 winget 的下载与安装策略。</summary>
    internal static async Task<(int ExitCode, string Output)> RunWingetWithProgressAsync(
        string packageId, IReadOnlyList<string> arguments, IProgress<ProgressInfo>? progress,
        CancellationToken ct, bool cleanupInstallerCache = false)
    {
        ct.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var output = new System.Text.StringBuilder();
        var markers = new WingetOutputMarkers();
        var download = new WingetDownloadProgress();
        var packageSelected = false;

        void ObserveOutput(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 1 && "-\\|/".Contains(trimmed, StringComparison.Ordinal)) return;
            Interlocked.Exchange(ref markers.LastOutputMs, clock.ElapsedMilliseconds);
            if (line.Contains("[" + packageId + "]", StringComparison.OrdinalIgnoreCase)) packageSelected = true;
            var isDownloadHeader = packageSelected && IsWingetDownloadLine(line);
            if (isDownloadHeader)
            {
                markers.DownloadStarted = true;
                markers.HashVerified = false;
                markers.InstallStarted = false;
                download.Reset(clock.ElapsedMilliseconds);
            }
            var isProgress = !isDownloadHeader && markers.DownloadStarted && !markers.HashVerified && !markers.InstallStarted &&
                download.Observe(line, clock.ElapsedMilliseconds);
            if (packageSelected && IsWingetHashVerifiedLine(line)) markers.HashVerified = true;
            if (packageSelected && IsWingetInstallLine(line)) markers.InstallStarted = true;
            if (!isProgress)
            {
                lock (output)
                {
                    output.AppendLine(line);
                    if (output.Length > 128 * 1024) output.Remove(0, output.Length - 64 * 1024);
                }
            }
        }

        progress?.Report(new ProgressInfo { Status = "正在准备升级…", Detail = "正在连接软件源…" });
        PseudoConsoleProcess session;
        try { session = PseudoConsoleProcess.Start("winget.exe", arguments, ObserveOutput); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            throw new InvalidOperationException("没有找到 winget，请安装或更新系统的「应用安装程序」。", ex);
        }
        using (session)
        {
            var process = session.Process;
            var lastStage = -1;
            var cancelled = false;
            var code = 0;
            var text = "";
            var failure = "";
            try
            {
                while (!process.HasExited)
                {
                    session.ThrowIfOutputFailed();
                    await Task.Delay(250, ct);
                    var now = clock.ElapsedMilliseconds;
                    var stage = markers.InstallStarted ? 3 : markers.HashVerified ? 2 : markers.DownloadStarted ? 1 : 0;
                    var idle = now - Interlocked.Read(ref markers.LastOutputMs);
                    if (idle > (stage >= 2 ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5)).TotalMilliseconds)
                    {
                        code = stage >= 2 ? -3 : -2;
                        throw new TimeoutException(stage >= 2 ? "安装阶段连续 30 分钟没有进展。" : "下载或准备阶段连续 5 分钟没有进展，请检查网络后重试。");
                    }
                    var transfer = download.GetProgress(now, stage == 1 && idle > 30000, completed: stage >= 2);
                    var status = stage switch
                    {
                        3 => "正在运行安装程序…",
                        2 => "安装包校验通过，正在准备安装…",
                        1 => transfer.Status!,
                        _ => "正在准备升级…"
                    };
                    if (stage != lastStage)
                    {
                        lastStage = stage;
                        Ui.Log($"winget {packageId} 阶段：{status}" +
                            (download.HasDownloadData ? $"（{transfer.Metric}）" : ""));
                    }
                    progress?.Report(stage == 1 ? transfer : new ProgressInfo
                    {
                        Percent = stage == 2 ? 100 : -1,
                        Status = status,
                        Detail = stage == 3 ? "安装中" : stage == 2 ? "SHA256 校验通过" : "正在连接软件源…",
                        Metric = stage < 2 ? null : download.HasDownloadData ? transfer.Metric :
                            markers.DownloadStarted ? "下载已完成" : "使用已缓存安装包",
                        CanCancel = true
                    });
                }
                await session.CompleteOutputAsync();
                code = process.ExitCode;
            }
            catch (TimeoutException ex)
            {
                if (code == 0) code = -2;
                failure = ex.Message;
                Ui.Log($"winget {packageId} 超时：{ex.Message}", "ERROR");
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                code = -1;
                Ui.Log($"winget {packageId} 已取消");
            }
            finally
            {
                try
                {
                    if (!process.HasExited) await session.StopAsync();
                    await session.CompleteOutputAsync();
                }
                finally
                {
                    if (cleanupInstallerCache) CleanupWingetCache(packageId);
                    lock (output) text = output.ToString().Trim();
                }
            }
            if (failure.Length > 0) text += Environment.NewLine + failure;
            if (cancelled) throw new OperationCanceledException("操作已取消", ct);
            progress?.Report(new ProgressInfo
            {
                Percent = code == 0 ? 100 : -1, Status = code == 0 ? "操作完成" : "操作未成功",
                Metric = download.HasDownloadData ? download.GetProgress(clock.ElapsedMilliseconds, false, code == 0).Metric : null,
                CanCancel = false
            });
            return (code, text);
        }
    }


    /// <summary>清理 winget 为某个包下载的临时安装包（%TEMP%\WinGet\&lt;id&gt;.&lt;version&gt;）。</summary>
    public static void CleanupWingetCache(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return;

        try
        {
            var root = Path.Combine(Path.GetTempPath(), "WinGet");
            if (!Directory.Exists(root)) return;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith(packageId + ".", StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { }
    }

    /// <summary>winget 输出里的阶段标记；由输出线程写入、进度循环读取。</summary>
    private sealed class WingetOutputMarkers
    {
        public volatile bool DownloadStarted;
        public volatile bool HashVerified;
        public volatile bool InstallStarted;
        public long LastOutputMs;
    }

    private static bool IsWingetDownloadLine(string line)
    {
        var lower = line.ToLowerInvariant();
        return line.Contains("正在下载", StringComparison.Ordinal)
               || lower.Contains("downloading", StringComparison.Ordinal);
    }

    private static bool IsWingetHashVerifiedLine(string line)
    {
        var lower = line.ToLowerInvariant();
        return line.Contains("已成功验证安装程序哈希", StringComparison.Ordinal)
               || (line.Contains("安装程序哈希", StringComparison.Ordinal) &&
                   line.Contains("成功", StringComparison.Ordinal))
               || lower.Contains("successfully verified installer hash", StringComparison.Ordinal)
               || lower.Contains("verified installer hash", StringComparison.Ordinal);
    }

    private static bool IsWingetInstallLine(string line)
    {
        var lower = line.ToLowerInvariant();
        return line.Contains("正在启动程序包安装", StringComparison.Ordinal)
               || line.Contains("正在启动包安装程序", StringComparison.Ordinal)
               || line.Contains("正在启动安装程序", StringComparison.Ordinal)
               || lower.Contains("starting package install", StringComparison.Ordinal)
               || lower.Contains("starting installer", StringComparison.Ordinal);
    }

    /// <summary>下载安装包到用户指定目录（复用 winget download）。</summary>
    public async Task<(int ExitCode, string Output, string Directory)> DownloadAsync(
        string packageId, CancellationToken ct = default)
    {
        var dir = AppSettings.EnsureDir(Ctx.Settings.DownloadRoot);
        var args = new Dictionary<string, string>
        {
            ["PackageId"] = packageId,
            ["DownloadDirectory"] = dir
        };

        var json = await PsBridge.RunAsync("downloadSoftware", args, elevated: false, ct: ct);
        var code = 0;
        var output = "";
        if (json.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number)
                code = ec.GetInt32();
            output = DriverUpdateService.Str(data, "output");
            var actual = DriverUpdateService.Str(data, "directory");
            if (!string.IsNullOrWhiteSpace(actual)) dir = actual;
        }
        return (code, output, dir);
    }

    /// <summary>从 winget 清单里解析安装包的直链（用于自建下载器显示进度与速度）。</summary>
    public async Task<string> ResolveInstallerUrlAsync(string packageId, string? version = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, string> { ["PackageId"] = packageId };
        if (!string.IsNullOrWhiteSpace(version)) args["Version"] = version!;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        JsonElement json;
        try
        {
            json = await PsBridge.RunAsync("installerUrl", args, elevated: false, ct: timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("查询安装包地址超过 90 秒，请检查网络或 winget 源后重试。");
        }

        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("urls", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                var url = e.GetString();
                if (!string.IsNullOrWhiteSpace(url)) return url!;
            }
        }
        throw new InvalidOperationException("该软件没有可直接下载的安装包（可能来自 Microsoft Store 或使用私有分发）。");
    }

    /// <summary>
    /// 下载安装包到用户指定目录：自己实现 HTTP 流式下载，因此可以显示百分比、速度与剩余时间。
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        string packageId, string displayName, string version,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        progress?.Report(new ProgressInfo { Percent = -1, Status = "正在解析安装包地址…", Detail = displayName });
        var url = await ResolveInstallerUrlAsync(packageId, version, ct);

        var dir = AppSettings.EnsureDir(Ctx.Settings.DownloadRoot);
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".exe";

        var baseName = SafeFileName($"{displayName}_{version}");
        var target = UniquePath(Path.Combine(dir, baseName + ext));

        await DownloadWithRetryAsync(url, target, progress, ct);
        return target;
    }

    private static async Task DownloadWithRetryAsync(
        string url, string target, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        const int maxAttempts = 3;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DevUp/1.1");
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo
            {
                Status = "正在连接下载源…",
                Detail = $"第 {attempt}/{maxAttempts} 次 · {Path.GetFileName(target)}"
            });
            try
            {
                await DownloadOnceAsync(http, url, target, attempt, maxAttempts, progress, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
            {
                if (!IsTransientDownloadError(ex))
                    throw new InvalidOperationException($"下载源拒绝了请求：{ex.Message}", ex);

                Ui.Log($"安装包下载第 {attempt} 次失败：{ex.Message}", "WARN");
                if (attempt == maxAttempts)
                    throw new InvalidOperationException(
                        $"安装包下载失败，已自动重试 {maxAttempts} 次。请检查网络或代理后重试。\n{ex.Message}", ex);

                progress?.Report(new ProgressInfo
                {
                    Status = "下载中断，正在重试…",
                    Detail = $"第 {attempt}/{maxAttempts} 次失败：{ex.Message}"
                });
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }

        throw new InvalidOperationException("安装包下载未完成。");
    }

    private static bool IsTransientDownloadError(Exception error)
    {
        if (error is TimeoutException or OperationCanceledException) return true;
        if (error is not HttpRequestException httpError) return false;
        if (!httpError.StatusCode.HasValue) return true;
        var status = (int)httpError.StatusCode.Value;
        return status is 408 or 429 or >= 500;
    }

    private static async Task DownloadOnceAsync(
        HttpClient http, string url, string target, int attempt, int maxAttempts,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);

        FileStream? dest = null;
        try
        {
            dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long received = 0;
            var clock = Stopwatch.StartNew();
            var lastReportMs = -1000L;
            var speed = 0d;
            var samples = new Queue<(long Ms, long Bytes)>();
            samples.Enqueue((0L, 0L));

            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                int read;
                try
                {
                    var readTask = source.ReadAsync(buffer, readTimeout.Token).AsTask();
                    var warningTask = Task.Delay(TimeSpan.FromSeconds(10), readTimeout.Token);
                    if (await Task.WhenAny(readTask, warningTask) == warningTask &&
                        !ct.IsCancellationRequested)
                    {
                        speed = 0;
                        samples.Clear();
                        samples.Enqueue((clock.ElapsedMilliseconds, received));
                        progress?.Report(new ProgressInfo
                        {
                            Percent = total is > 0 ? Math.Min(99, received * 100.0 / total.Value) : -1,
                            Status = "下载暂停，等待网络响应…",
                            Detail = $"第 {attempt}/{maxAttempts} 次 · 已收到 {FmtBytes(received)}"
                        });
                    }
                    read = await readTask;
                    readTimeout.Cancel();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("45 秒未收到下载数据。");
                }
                catch (IOException ex)
                {
                    throw new HttpRequestException("下载连接中断。", ex);
                }
                if (read <= 0) break;

                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                var nowMs = clock.ElapsedMilliseconds;
                samples.Enqueue((nowMs, received));
                while (samples.Count > 2 && nowMs - samples.Peek().Ms > 3000) samples.Dequeue();
                var due = nowMs - lastReportMs >= 300;
                var final = total is > 0 && received >= total.Value;
                if (!due && !final) continue;

                var oldest = samples.Peek();
                var spanMs = nowMs - oldest.Ms;
                if (spanMs >= 400)
                {
                    var measured = (received - oldest.Bytes) * 1000.0 / spanMs;
                    speed = speed <= 0 ? measured : speed * 0.6 + measured * 0.4;
                }
                lastReportMs = nowMs;
                progress?.Report(new ProgressInfo
                {
                    Percent = total is > 0 ? Math.Min(99, received * 100.0 / total.Value) : -1,
                    Status = "正在下载安装包…",
                    Detail = $"第 {attempt}/{maxAttempts} 次 · {BuildDetail(received, total, speed)}"
                });
            }

            await dest.FlushAsync(ct);
            dest.Dispose();
            dest = null;
            if (total is > 0 && received != total.Value)
                throw new HttpRequestException(
                    $"安装包下载不完整：应为 {FmtBytes(total.Value)}，实际收到 {FmtBytes(received)}。");

            progress?.Report(new ProgressInfo
            {
                Percent = 100,
                Status = "下载完成",
                Detail = $"{Path.GetFileName(target)} · {FmtBytes(received)}",
                CanCancel = false
            });
        }
        catch
        {
            dest?.Dispose();
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            throw;
        }
    }

    private static string BuildDetail(long received, long? total, double speed)
    {
        var left = total is > 0
            ? $"{FmtBytes(received)} / {FmtBytes(total.Value)}"
            : FmtBytes(received);

        var speedText = speed > 0 ? $"{FmtBytes((long)speed)}/s" : "";

        var eta = "";
        if (total is > 0 && speed > 1024)
        {
            var seconds = (int)Math.Max(0, (total.Value - received) / speed);
            // 固定成 mm:ss（超过 1 小时才加 h:）：数字长度不再变化，文字不会左右/上下抖动
            eta = seconds >= 3600
                ? $"剩余 {seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
                : $"剩余 {seconds / 60:00}:{seconds % 60:00}";
        }

        return string.Join("　·　", new[] { left, speedText, eta }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public static string FmtBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private static string SafeFileName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? "installer" : (s.Length > 120 ? s[..120] : s);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    /// <summary>
    /// 把 winget 软件关联到本机已安装条目（名称 + 版本双重匹配），无更新时也保留包 ID。
    /// 返回无法关联的可更新项，作为“仅 winget 源”条目补充展示。
    /// </summary>
    public static List<SoftwareItem> Correlate(IList<SoftwareItem> inventory, IList<SoftwareUpdateItem> updates)
    {
        var extra = new List<SoftwareItem>();
        var used = new HashSet<SoftwareItem>();

        foreach (var update in updates)
        {
            var match = FindMatch(inventory, update, used);
            if (match is null)
            {
                if (string.IsNullOrWhiteSpace(update.AvailableVersion)) continue;
                extra.Add(new SoftwareItem
                {
                    DisplayName = update.Name,
                    DisplayVersion = update.InstalledVersion,
                    Publisher = "winget",
                    Source = "winget 源",
                    Scope = "系统级",
                    WingetId = update.Id,
                    AvailableVersion = update.AvailableVersion
                });
                continue;
            }

            match.WingetId = update.Id;
            match.AvailableVersion = update.AvailableVersion;
            used.Add(match);
        }

        return extra;
    }

    private static SoftwareItem? FindMatch(IList<SoftwareItem> inventory, SoftwareUpdateItem update, HashSet<SoftwareItem> used)
    {
        var key = Normalize(update.Name);
        var installed = Normalize(update.InstalledVersion);
        if (installed.Length == 0) return null;

        // 先锁定已装版本，避免把同名但不同版本的更新贴到错误的软件上。
        var candidates = inventory
            .Where(s => !used.Contains(s) && Normalize(s.DisplayVersion) == installed)
            .ToList();

        // 1) 名称一致 + 已装版本一致（同名多版本共存时最可靠）
        if (key.Length > 0)
        {
            var byName = candidates.FirstOrDefault(s => Normalize(s.DisplayName) == key);
            if (byName is not null) return byName;
        }

        // 2) winget 包 ID 与显示名互相包含（"Windows 终端" ↔ Microsoft.WindowsTerminal）
        var byId = candidates.FirstOrDefault(s => NameMatches(s.DisplayName, update.Id));
        if (byId is not null) return byId;

        // 3) 名称归一化后互相包含（"Python 3.13.1 (64-bit)" ↔ "Python.Python.3.13" 之外的长名场景）
        if (key.Length > 3)
        {
            return candidates.FirstOrDefault(s =>
            {
                var n = Normalize(s.DisplayName);
                return n.Length > 3 && (n.Contains(key) || key.Contains(n));
            });
        }

        return null;
    }

    private static bool NameMatches(string displayName, string wingetId)
    {
        var dn = Normalize(displayName);
        var idn = Normalize(wingetId);
        if (dn.Length < 3 || idn.Length < 3) return false;
        if (idn.Contains(dn) || dn.Contains(idn)) return true;

        // 去掉版本号/架构后缀再比一次： "Node.js (64-bit)" -> "Node"
        var cleaned = Regex.Replace(displayName, @"[\(\[\]\d].*$", "").Trim();
        var cn = Normalize(cleaned);
        return cn.Length >= 3 && idn.Contains(cn);
    }

    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
