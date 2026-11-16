using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DevUp.Core;

namespace DevUp.Services;

internal sealed record UpdateAsset(string Name, Uri Url, long Size, string Digest);
internal sealed record DevUpUpdateInfo(Version Version, Uri Page, string Notes,
    UpdateAsset? Program, UpdateAsset? Rules, bool RulesAvailable, string RuleNote);

/// <summary>只访问本项目正式 Release，程序与规则分别比较，网络检查不常驻。</summary>
internal sealed class DevUpUpdateService
{
    internal const string Repository = "FungCoder/DevUp";
    private readonly CleanupRuleStore _rules;
    private readonly Func<HttpClient> _createClient;
    private readonly Version _currentVersion;
    internal DevUpUpdateInfo? Available { get; private set; }
    internal string Status { get; private set; } = "尚未检查更新";
    internal bool IsChecking { get; private set; }
    internal bool HasProgramUpdate => Available is { Program: not null } info && info.Version > _currentVersion;
    internal bool HasUpdates => HasProgramUpdate || Available?.RulesAvailable == true;
    internal event EventHandler? Changed;

    internal DevUpUpdateService(CleanupRuleStore rules, Func<HttpClient>? createClient = null,
        Version? currentVersion = null)
    {
        _rules = rules;
        _createClient = createClient ?? (() => SoftwareNetwork.CreateClient(Ctx.Settings));
        _currentVersion = Normalize(currentVersion ?? typeof(DevUpUpdateService).Assembly.GetName().Version!);
    }

    internal async Task CheckAsync(CancellationToken ct = default)
    {
        if (IsChecking) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        ct = deadline.Token;
        IsChecking = true;
        Status = "正在检查 GitHub 正式发布版本…";
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            using var client = _createClient();
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), 256 * 1024, ct);
            using var json = JsonDocument.Parse(bytes);
            var release = json.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
                !Version.TryParse(release.GetProperty("tag_name").GetString()?.TrimStart('v'), out var version))
                throw new InvalidDataException("GitHub 正式版本信息无效。");
            version = Normalize(version);
            var program = FindAsset(release, "DevUp.exe", 32 * 1024 * 1024);
            var rules = FindAsset(release, CleanupRuleStore.AssetName, CleanupRuleStore.MaximumCompressedBytes);
            var ruleAvailable = false;
            var ruleNote = "此正式版本未附带独立规则包，继续使用现有规则";
            if (rules is not null)
            {
                if (rules.Digest.Equals(_rules.Digest, StringComparison.OrdinalIgnoreCase)) ruleNote = "规则已是当前发布版本";
                else
                {
                    try
                    {
                        var pack = CleanupRuleStore.Decode(await DownloadRulesAsync(rules, null, ct));
                        CleanupRuleStore.ValidatePack(pack);
                        ruleAvailable = Version.Parse(pack.Version) > Version.Parse(_rules.Current.Version);
                        ruleNote = ruleAvailable ? $"规则可更新至 {pack.Version}" : "规则已是当前发布版本";
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or HttpRequestException)
                    {
                        ruleNote = "规则包暂不可用或需要更新 DevUp：" + ex.Message;
                    }
                }
            }
            Available = new(version, new Uri($"https://github.com/{Repository}/releases/tag/" +
                Uri.EscapeDataString(release.GetProperty("tag_name").GetString()!)),
                release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                program, rules, ruleAvailable, ruleNote);
            Status = HasProgramUpdate ? $"发现 DevUp {version.ToString(3)}" :
                version < _currentVersion ? "当前 DevUp 版本较新，无需更新" : "DevUp 已是当前正式版本";
            if (version > _currentVersion && program is null) Status = "发现新版，但缺少可校验的程序附件，请打开发布页下载";
        }
        catch
        {
            Status = "更新检查未完成，现有程序和规则可继续使用";
            throw;
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task CheckOnStartupAsync()
    {
        if (!Ctx.Settings.AutoCheckDevUpUpdates ||
            DateTimeOffset.UtcNow - Ctx.Settings.LastDevUpUpdateCheckUtc < TimeSpan.FromDays(1)) return;
        try
        {
            Ctx.Settings.LastDevUpUpdateCheckUtc = DateTimeOffset.UtcNow;
            Ctx.Settings.Save();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await CheckAsync(timeout.Token);
        }
        catch (Exception ex) { Ui.Log("DevUp 更新检查未完成：" + ex.Message, "WARN"); }
    }

    internal async Task InstallRulesAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var asset = Available?.Rules ?? throw new InvalidOperationException("请先检查规则更新。");
        var data = await DownloadRulesAsync(asset, progress, ct);
        ct.ThrowIfCancellationRequested();
        _rules.Install(data, asset.Digest);
        Available = Available! with { RulesAvailable = false, RuleNote = "规则已更新，请重新扫描" };
        Status = HasProgramUpdate ? $"规则已更新；仍可更新 DevUp {Available.Version.ToString(3)}" : "规则更新完成";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal async Task<string> DownloadProgramAsync(string directory,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!HasProgramUpdate) throw new InvalidOperationException("没有可更新的 DevUp 正式版本。");
        PortablePaths.EnsureWritableDirectory(directory);
        var path = Path.Combine(directory, "DevUp.new.exe");
        try
        {
            await DownloadAsync(Available!.Program!, path, progress, ct);
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!Version.TryParse(version.FileVersion, out var fileVersion) || fileVersion != Normalize(Available.Version) ||
                version.ProductName != "DevUp 驱动与软件更新助手")
                throw new InvalidDataException("下载程序的版本或产品信息不匹配，已停止更新。");
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    private async Task<byte[]> DownloadRulesAsync(UpdateAsset asset, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var temporary = Path.Combine(PortablePaths.Temp, "rules-" + Guid.NewGuid().ToString("N"));
        PortablePaths.EnsureWritableDirectory(PortablePaths.Temp);
        try
        {
            await DownloadAsync(asset, temporary, progress, ct);
            return await File.ReadAllBytesAsync(temporary, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task DownloadAsync(UpdateAsset asset, string target,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var client = _createClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            throw new InvalidDataException("附件大小与 GitHub 发布信息不一致。");
        await using var source = await response.Content.ReadAsStreamAsync(limit.Token);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long downloaded = 0;
        var watch = Stopwatch.StartNew();
        long lastReport = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, limit.Token)) > 0)
        {
            downloaded += read;
            if (downloaded > asset.Size) throw new InvalidDataException("附件超出声明大小，已停止下载。");
            await output.WriteAsync(buffer.AsMemory(0, read), limit.Token);
            hash.AppendData(buffer, 0, read);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            if (watch.ElapsedMilliseconds - lastReport >= 100 || downloaded == asset.Size)
            {
                progress?.Report(new ProgressInfo { Status = "正在下载 " + asset.Name,
                    Percent = downloaded * 100d / asset.Size,
                    Detail = $"{Models.CleanupSize.Format(downloaded)} / {Models.CleanupSize.Format(asset.Size)}" });
                lastReport = watch.ElapsedMilliseconds;
            }
        }
        if (downloaded != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("附件 SHA256 或大小校验失败，保留现有版本。");
    }

    internal static UpdateAsset? FindAsset(JsonElement release, string name, long maximum)
    {
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
            if (size <= 0 || size > maximum || digest is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$") ||
                !Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "github.com" || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
            return new(name, uri, size, digest[7..]);
        }
        return null;
    }

    private static Version Normalize(Version version)
        => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        await using (stream)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + read > maximum) throw new InvalidDataException("更新信息超过大小限制。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }
}
