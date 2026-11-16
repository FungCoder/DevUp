using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DevUp.Core;
using FluentCleaner.Models;

namespace DevUp.Services;

internal sealed record CleanupRulePack(int Format, string Version, string MinimumAppVersion,
    string SourceCommit, string License, string Rules);

/// <summary>规则包只包含数据，保留内嵌版本与一份上次可用版本。</summary>
internal sealed class CleanupRuleStore
{
    internal const string AssetName = "DevUp.rules.json.gz";
    internal const int MaximumCompressedBytes = 512 * 1024;
    private readonly string _directory;
    private bool _currentFileValid;
    internal CleanupRulePack Current { get; private set; }
    internal IReadOnlyList<CleanerEntry> Rules { get; private set; }
    internal string Digest { get; private set; } = "";
    internal event EventHandler? Changed;

    internal CleanupRuleStore() : this(Path.Combine(PortablePaths.StorageRoot, "rules"), ReadBuiltIn()) { }

    internal CleanupRuleStore(string directory, CleanupRulePack builtIn)
    {
        _directory = directory;
        Current = builtIn;
        Rules = ValidatePack(builtIn);
        foreach (var name in new[] { "current.gz", "previous.gz" })
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > MaximumCompressedBytes) throw new InvalidDataException("本地规则文件过大。");
                var bytes = File.ReadAllBytes(path);
                var pack = Decode(bytes);
                if (System.Version.Parse(pack.Version) < System.Version.Parse(builtIn.Version)) continue;
                Rules = ValidatePack(pack);
                Current = pack;
                Digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                _currentFileValid = name == "current.gz";
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                Ui.Log("本地清理规则不可用，回退到可用版本：" + ex.Message, "WARN");
            }
        }
    }

    internal void Install(byte[] data, string expectedDigest)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        if (!digest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("规则包 SHA256 不一致，保留现有规则。");
        var pack = Decode(data);
        var rules = ValidatePack(pack);
        if (System.Version.Parse(pack.Version) < System.Version.Parse(Current.Version))
            throw new InvalidDataException("规则版本较旧，已拒绝降级。");
        var current = Path.Combine(_directory, "current.gz");
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        PortablePaths.EnsureWritableDirectory(_directory);
        try
        {
            File.WriteAllBytes(temporary, data);
            if (File.Exists(current) && _currentFileValid) File.Replace(temporary, current, Path.Combine(_directory, "previous.gz"));
            else File.Move(temporary, current, overwrite: true);
            Current = pack;
            Rules = rules;
            Digest = digest;
            _currentFileValid = true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static CleanupRulePack Decode(byte[] data)
    {
        if (data.Length > MaximumCompressedBytes) throw new InvalidDataException("规则包过大。");
        using var source = new MemoryStream(data, writable: false);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (decoded.Length + read > 1_500_000) throw new InvalidDataException("规则解压大小超过限制。");
            decoded.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<CleanupRulePack>(decoded.ToArray())
            ?? throw new InvalidDataException("规则包为空。");
    }

    internal static IReadOnlyList<CleanerEntry> ValidatePack(CleanupRulePack pack)
    {
        if (pack.Format != 1 || !System.Version.TryParse(pack.Version, out _) ||
            !System.Version.TryParse(pack.MinimumAppVersion, out var minimum) ||
            minimum > typeof(CleanupRuleStore).Assembly.GetName().Version ||
            pack.License != "CC-BY-SA-4.0" || pack.SourceCommit is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(pack.SourceCommit, "^[0-9a-f]{40}$") || pack.Rules is null)
            throw new InvalidDataException("规则包格式不兼容，需要更新 DevUp 或使用旧版规则。");
        return CleanupRulePolicy.Validate(pack.Rules);
    }

    private static CleanupRulePack ReadBuiltIn()
    {
        var assembly = typeof(CleanupRuleStore).Assembly;
        using var stream = assembly.GetManifestResourceStream("winapp2-safe.ini")
            ?? throw new InvalidDataException("内嵌扩展清理规则缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var metadata = assembly.GetManifestResourceStream("rulepack-metadata.json")
            ?? throw new InvalidDataException("内嵌规则版本信息缺失。");
        var pack = JsonSerializer.Deserialize<CleanupRulePack>(metadata)
            ?? throw new InvalidDataException("内嵌规则版本信息为空。");
        return pack with { Rules = reader.ReadToEnd() };
    }
}
