using System.Text;
using System.Text.Json;
using FluentCleaner.Models;

namespace DevUp.Services;

internal sealed record CleanupRulePack(int Format, string Version, string MinimumAppVersion,
    string SourceCommit, string License, string Rules);

/// <summary>只加载程序集内嵌规则，规则与程序作为一个文件发布。</summary>
internal sealed class CleanupRuleStore
{
    internal CleanupRulePack Current { get; }
    internal IReadOnlyList<CleanerEntry> Rules { get; }

    internal CleanupRuleStore()
    {
        Current = ReadBuiltIn();
        Rules = ValidatePack(Current);
    }

    internal static IReadOnlyList<CleanerEntry> ValidatePack(CleanupRulePack pack)
    {
        if (pack.Format != 1 || !System.Version.TryParse(pack.Version, out _) ||
            !System.Version.TryParse(pack.MinimumAppVersion, out var minimum) ||
            minimum > typeof(CleanupRuleStore).Assembly.GetName().Version ||
            pack.License != "CC-BY-SA-4.0" || pack.SourceCommit is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(pack.SourceCommit, "^[0-9a-f]{40}$") || pack.Rules is null)
            throw new InvalidDataException("内嵌规则元数据无效。");
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
