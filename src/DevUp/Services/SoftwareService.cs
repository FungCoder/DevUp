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
        catch (Exception ex)
        {
            Ui.Log($"读取 MSIX 应用失败: {ex.Message}", "WARN");
        }
        return result;
    }

    /// <summary>winget 报告的可用更新。</summary>
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
        string Detail);

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
                CanRetryAsInstall: false, CanOpenInstaller: true, Detail: detail),

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
    /// 带下载进度的升级：仍然由 winget 负责选择安装器与静默参数（保证准确性），
    /// 我们只旁路观察 winget 的下载目录（%TEMP%\WinGet）增长量，换算成百分比与速度。
    /// 升级结束（无论成败）都会清理 winget 留在临时目录里的安装包。
    /// </summary>
    /// <param name="forceVersion">
    /// 传入版本号时改用 <c>winget install --version … --force</c>：绕过“已安装版本与清单不一致”的检查，
    /// 用于 winget upgrade 因安装范围 / 安装器类型不匹配而拒绝升级的软件。
    /// </param>
    public async Task<(int ExitCode, string Output)> InstallWithProgressAsync(
        string packageId, IProgress<ProgressInfo>? progress, CancellationToken ct = default,
        string? forceVersion = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "WinGet");
        // 只统计本次升级对应的包目录。%TEMP%\WinGet 里可能还有别的软件残留的安装包，
        // 统计整棵目录会让进度忽大忽小，甚至在别的文件增长时误判下载进度。
        var baseline = PackageDirSize(tempRoot, packageId);

        // 并行解析安装包体积：解析完成前先显示不确定进度，完成后变成百分比。
        var sizeTask = TryGetInstallerSizeAsync(packageId, ct, forceVersion);

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
        var wingetArgs = forceVersion is null
            ? new[]
            {
                "upgrade", "--id", packageId, "--exact",
                "--accept-source-agreements", "--accept-package-agreements",
                "--disable-interactivity"
            }
            : new[]
            {
                "install", "--id", packageId, "--exact", "--version", forceVersion, "--force",
                "--accept-source-agreements", "--accept-package-agreements",
                "--disable-interactivity"
            };
        foreach (var a in wingetArgs)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // winget 不在 PATH（通常是 App Installer 未安装或被卸载）
            throw new InvalidOperationException(
                "没有找到 winget。请先安装或更新「应用安装程序（App Installer）」，" +
                "或在 Microsoft Store 搜索并安装。", ex);
        }

        var output = new System.Text.StringBuilder();
        var markers = new WingetOutputMarkers();
        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            var data = e.Data;
            lock (output) output.AppendLine(data);

            // winget 的阶段标记比“文件多久没增长”可靠：下载慢、校验哈希、安装器启动前
            // 都可能出现长时间没有字节增长，不能据此判断已经进入安装阶段。
            if (IsWingetDownloadLine(data)) markers.DownloadStarted = true;
            if (IsWingetHashVerifiedLine(data)) markers.HashVerified = true;
            if (IsWingetInstallLine(data)) markers.InstallStarted = true;
        };
        var err = new System.Text.StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lock (err) err.AppendLine(e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var clock = Stopwatch.StartNew();
        long downloaded = 0, lastBytes = 0, lastGrowth = 0, lastTick = 0;
        double speed = 0;
        long total = 0;
        var hasTotal = false;
        var lastStage = -1;
        var cancelled = false;
        var code = 0;
        var text = "";

        try
        {
            while (!proc.HasExited)
            {
                await Task.Delay(300, ct);

                if (sizeTask.IsCompletedSuccessfully && sizeTask.Result > 0)
                {
                    total = sizeTask.Result;
                    hasTotal = true;
                }

                downloaded = Math.Max(0, PackageDirSize(tempRoot, packageId) - baseline);
                var now = clock.ElapsedMilliseconds;

                if (downloaded > lastBytes)
                {
                    var elapsed = Math.Max(0.05, (now - lastTick) / 1000.0);
                    var instant = (downloaded - lastBytes) / elapsed;
                    speed = speed <= 0 ? instant : speed * 0.7 + instant * 0.3;
                    lastBytes = downloaded;
                    lastTick = now;
                    lastGrowth = now;
                }

                // 0=准备，1=下载，2=校验通过/等待安装器，3=安装。
                // 只有 winget 明确输出安装阶段标记后才进入 3；下载字节数停滞不再触发安装状态。
                var stage = markers.InstallStarted ? 3
                    : markers.HashVerified ? 2
                    : markers.DownloadStarted || downloaded > 0 ? 1
                    : 0;

                var percent = stage switch
                {
                    3 => -1,
                    2 => hasTotal && total > 0 ? 99.0 : -1,
                    1 => hasTotal && total > 0 && downloaded > 0 ? Math.Min(99.0, downloaded * 100.0 / total) : -1,
                    _ => -1
                };

                var status = stage switch
                {
                    3 => "正在安装安装包…",
                    2 => "安装包校验通过，正在启动安装…",
                    1 => "正在下载安装包…",
                    _ => "正在准备升级…"
                };

                var stalled = stage == 1 && downloaded > 0 && now - lastGrowth > 5000;
                var detail = stage switch
                {
                    3 => "",
                    2 => "SHA256 校验通过",
                    1 => stalled
                        ? "下载暂时没有进展，正在等待网络…"
                        : hasTotal && total > 0
                            ? BuildDetail(downloaded, total, speed)
                            : downloaded > 0
                                ? BuildDetail(downloaded, null, speed)
                                : "winget 正在通过系统下载服务传输安装包…",
                    _ => ""
                };

                if (stage != lastStage)
                {
                    lastStage = stage;
                    Ui.Log($"winget {packageId} 阶段：{status}（已下载 {FmtBytes(downloaded)}" +
                           $"{(hasTotal ? " / " + FmtBytes(total) : "")}）");
                }

                progress?.Report(new ProgressInfo
                {
                    Percent = percent,
                    Status = status,
                    Detail = detail,
                    CanCancel = true
                });
            }

            proc.WaitForExit();
            code = proc.ExitCode;
            lock (output) text = output.ToString();
            lock (err) text += err.ToString();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            code = -1;
            lock (output) text = output.ToString();
            lock (err) text += err.ToString();
            Ui.Log($"winget {packageId} 升级已取消");
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { proc.WaitForExit(5000); } catch { }
            }

            // 无论成功、失败还是取消，都要清理 winget 留下的临时安装包
            CleanupWingetCache(packageId);
        }

        if (cancelled)
        {
            progress?.Report(new ProgressInfo
            {
                Percent = -1,
                Status = "已取消",
                Detail = "本次升级已停止，临时安装包已清理",
                CanCancel = false
            });
            throw new OperationCanceledException("升级已取消");
        }

        progress?.Report(new ProgressInfo
        {
            Percent = 100,
            Status = code == 0 ? "安装完成" : "安装未成功",
            Detail = "",
            CanCancel = false
        });

        return (code, text.Trim());
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

    private static long DirSize(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return 0;
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 只统计 winget 为指定包创建的目录（%TEMP%\WinGet\&lt;id&gt;.&lt;version&gt;）。
    /// 不统计整个 WinGet 目录，避免其他软件残留的安装包影响本次进度。
    /// </summary>
    private static long PackageDirSize(string root, string packageId)
    {
        try
        {
            if (!Directory.Exists(root)) return 0;
            long sum = 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith(packageId + ".", StringComparison.OrdinalIgnoreCase)) continue;
                sum += DirSize(dir);
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>winget 输出里的阶段标记；由输出线程写入、进度循环读取。</summary>
    private sealed class WingetOutputMarkers
    {
        public volatile bool DownloadStarted;
        public volatile bool HashVerified;
        public volatile bool InstallStarted;
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
               || line.Contains("正在安装程序包", StringComparison.Ordinal)
               || lower.Contains("starting package install", StringComparison.Ordinal)
               || lower.Contains("starting installer", StringComparison.Ordinal)
               || lower.Contains("starting installation", StringComparison.Ordinal);
    }

    /// <summary>用 HEAD 请求获取安装包大小（用于把下载量换算成百分比）。</summary>
    private async Task<long> TryGetInstallerSizeAsync(string packageId, CancellationToken ct, string? version = null)
    {
        try
        {
            var url = await ResolveInstallerUrlAsync(packageId, version, ct);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DevUp/1.0");
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return 0;
            return resp.Content.Headers.ContentLength ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>下载安装包到用户指定目录（复用 winget download）。</summary>
    public async Task<(int ExitCode, string Output, string Directory)> DownloadAsync(
        string packageId, CancellationToken ct = default)
    {
        var dir = AppSettings.EnsureDir(Ctx.Settings.DownloadRoot, AppSettings.DefaultDownloadRoot);
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

        var json = await PsBridge.RunAsync("installerUrl", args, elevated: false, ct: ct);

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
        progress?.Report(new ProgressInfo { Percent = -1, Status = "正在解析安装包地址…", Detail = displayName, CanCancel = false });
        var url = await ResolveInstallerUrlAsync(packageId, version, ct);

        var dir = AppSettings.EnsureDir(Ctx.Settings.DownloadRoot, AppSettings.DefaultDownloadRoot);
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".exe";

        var baseName = SafeFileName($"{displayName}_{version}");
        var target = UniquePath(Path.Combine(dir, baseName + ext));

        progress?.Report(new ProgressInfo { Percent = 0, Status = "正在下载安装包…", Detail = Path.GetFileName(target), CanCancel = true });

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DevUp/1.0");

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
            // 速度按最近 3 秒的滑动窗口计算：慢速网络下一个 80 KB 分块可能几秒才到，
            // 用“距上次回调这段时间”的瞬时值会让速度忽高忽低，剩余时间随之反复消失/出现。
            var samples = new Queue<(long Ms, long Bytes)>();
            samples.Enqueue((0L, 0L));

            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read <= 0) break;

                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                var nowMs = clock.ElapsedMilliseconds;
                samples.Enqueue((nowMs, received));
                while (samples.Count > 2 && nowMs - samples.Peek().Ms > 3000) samples.Dequeue();

                // 只在确实收到新数据、且距上次刷新 ≥300ms 时更新界面：空刷新会把速度拉到 0
                var due = nowMs - lastReportMs >= 300;
                var final = total.HasValue && received >= total.Value;
                if (!due && !final) continue;

                var oldest = samples.Peek();
                var spanMs = nowMs - oldest.Ms;
                if (spanMs >= 400)
                {
                    var measured = (received - oldest.Bytes) * 1000.0 / spanMs;
                    speed = speed <= 0 ? measured : speed * 0.6 + measured * 0.4;
                }

                lastReportMs = nowMs;

                var percent = total is > 0 ? received * 100.0 / total.Value : -1;
                progress?.Report(new ProgressInfo
                {
                    Percent = percent,
                    Status = "正在下载安装包…",
                    Detail = BuildDetail(received, total, speed),
                    CanCancel = true
                });
            }

            await dest.FlushAsync(ct);
            dest.Dispose();
            dest = null;

            // 服务器声明了长度就必须收全，避免把半截文件当成“下载完成”
            if (total is > 0 && received != total.Value)
                throw new IOException($"安装包下载不完整：应为 {FmtBytes(total.Value)}，实际收到 {FmtBytes(received)}。");

            progress?.Report(new ProgressInfo
            {
                Percent = 100,
                Status = "下载完成",
                Detail = $"{Path.GetFileName(target)} · {FmtBytes(received)}",
                CanCancel = false
            });

            return target;
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
    /// 把 winget 更新项关联到本机已安装条目（名称 + 版本双重匹配）。
    /// 返回无法关联的更新项（会作为“仅 winget 源”条目补充展示，保证每条更新都可执行）。
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
