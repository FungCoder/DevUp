using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FluentCleaner.Models;
using FluentCleaner.Services;

namespace DevUp.Services;

/// <summary>限制第三方规则为 AppData 中明确的缓存或日志子目录。</summary>
internal static class CleanupRulePolicy
{
    private static readonly HashSet<string> SafeFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Caches", "GPUCache", "Code Cache", "CachedData", "cache2", "DawnCache",
        "TempState", "Logs", "Log", "Crashpad", "CrashReports"
    };
    private static readonly Regex Variable = new(@"^%(LocalAppData|AppData|CommonAppData|ProgramData|LocalLowAppData)%$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Pattern = new(@"^[^\\/:|<>\x00-\x1F]+$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> PrivateFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "IndexedDB", "Local Storage", "Session Storage", "Backups", "FileStorage",
        "MsgAttach", "Downloads", "Documents", "ChatHistory", "Offline", "OneNote",
        "Outlook", "olk", "Windows Mail", "Mailbird", "TokenBroker", "IdentityCache",
        "Credentials", "Session", "Sessions", "Authentication", "Accounts"
    };

    internal static bool IsSafeKey(FileKeyEntry key)
    {
        var parts = key.Path.Split('\\');
        if (parts.Length < 3 || !Variable.IsMatch(parts[0]) ||
            parts.Skip(1).Any(part => part.Length == 0 || part is "." or ".." || PrivateFolders.Contains(part) ||
                part.IndexOfAny(['%', ':', '/', '|', '<', '>', '\r', '\n']) >= 0)) return false;
        // 第一层厂商/应用目录不可通配，避免把整个 AppData 扫成缓存目录。
        if (parts[1].Contains('*') || parts[1].Contains('?') ||
            !SafeFolders.Contains(parts[^1]) || !Pattern.IsMatch(key.Pattern)) return false;
        if (parts.Any(part => part.Contains("OneNote", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("Microsoft.Outlook", StringComparison.OrdinalIgnoreCase))) return false;
        if (parts[1].Equals("Packages", StringComparison.OrdinalIgnoreCase) &&
            (parts.Length < 4 || !parts[2].StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))) return false;
        return key.Flag is FileKeyFlag.None or FileKeyFlag.Recurse;
    }

    internal static IReadOnlyList<CleanerEntry> Validate(string content)
    {
        if (content.Length > 1_000_000) throw new InvalidDataException("规则文本超过大小限制。");
        var rules = new Winapp2Parser().Parse(content);
        if (rules.Count is < 1 or > 2000 || rules.Select(rule => rule.Name).Distinct().Count() != rules.Count ||
            rules.Any(rule => rule.Name.Length > 160 || rule.FileKeys.Count == 0 ||
                !rule.Name.StartsWith("扩展 · ", StringComparison.Ordinal) || !SupportedSyntax(rule.RawText, false) ||
                rule.RegKeys.Count != 0 || rule.Default || rule.FileKeys.Any(key => !IsSafeKey(key)) ||
                rule.ExcludeKeys.Any(exclusion => !ValidExclusion(exclusion))))
            throw new InvalidDataException("规则包包含不受支持或超出缓存范围的操作。");
        return rules;
    }

    internal static string Adapt(string upstream)
    {
        var output = new StringBuilder("; Adapted from Winapp2, CC-BY-SA-4.0. File-cache subsets only.\n");
        var privateData = new Regex(@"password|cookie|bookmark|autofill|history|unsaved|saved session|credentials|recent files",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var rule in new Winapp2Parser().Parse(upstream))
        {
            if (privateData.IsMatch(rule.Name) || !SupportedSyntax(rule.RawText, true) ||
                rule.ExcludeKeys.Any(key => !ValidExclusion(key))) continue;
            var keys = rule.FileKeys.Select(key => new FileKeyEntry
            {
                Path = key.Path, Pattern = key.Pattern,
                Flag = key.Flag == FileKeyFlag.RemoveSelf ? FileKeyFlag.Recurse : key.Flag
            }).Where(IsSafeKey).ToArray();
            if (keys.Length == 0) continue;
            output.AppendLine($"\n[扩展 · {rule.Name}]");
            output.AppendLine(keys.Any(key => key.Path.Contains(@"\Packages\Microsoft.", StringComparison.OrdinalIgnoreCase))
                ? "Section=Windows 应用缓存" : "Section=第三方软件缓存");
            output.AppendLine("DetectFile=" + keys[0].Path);
            output.AppendLine("Default=False");
            output.AppendLine("Warning=仅清理七天前的缓存和日志；保留个人文件和登录数据。请先退出相关软件。");
            for (var index = 0; index < keys.Length; index++)
                output.AppendLine($"FileKey{index + 1}={keys[index].Path}|{keys[index].Pattern}|" +
                    (keys[index].Flag == FileKeyFlag.Recurse ? "RECURSE" : ""));
            for (var index = 0; index < rule.ExcludeKeys.Count; index++)
            {
                var key = rule.ExcludeKeys[index];
                if (key.Type == ExcludeType.Reg) continue;
                output.AppendLine($"ExcludeKey{index + 1}={key.Type.ToString().ToUpperInvariant()}|{key.Path}" +
                    (key.Pattern is null ? "" : "|" + key.Pattern));
            }
        }
        var adapted = output.ToString();
        Validate(adapted);
        return adapted;
    }

    private static bool ValidExclusion(ExcludeKeyEntry exclusion)
    {
        if (exclusion.Type == ExcludeType.Reg) return true;
        return exclusion.Path.Length is > 0 and < 1000 && !exclusion.Path.Contains('|') &&
            !Regex.Replace(exclusion.Path, "%[^%]+%", "").Contains('%') &&
            (exclusion.Path.StartsWith('%') || Path.IsPathFullyQualified(exclusion.Path)) &&
            !exclusion.Path.Split('\\', '/').Any(segment => segment == "..") &&
            Regex.Matches(exclusion.Path, "%([^%]+)%").All(match =>
                Regex.IsMatch(match.Groups[1].Value,
                    "^(LocalAppData|AppData|CommonAppData|ProgramData|LocalLowAppData|UserProfile|Documents|ProgramFiles|ProgramFilesX86|ProgramFiles\\(x86\\)|WinDir|SystemRoot|Temp|SystemDrive)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) &&
            (exclusion.Pattern is null || Pattern.IsMatch(exclusion.Pattern));
    }

    private static bool SupportedSyntax(string raw, bool adapting)
    {
        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('[')) continue;
            var split = line.IndexOf('=');
            if (split < 1) return false;
            var key = line[..split].Trim();
            if (!Regex.IsMatch(key, @"^(Section|LangSecRef|Detect\d*|DetectFile\d*|SpecialDetect|Warning|Default|FileKey\d+|ExcludeKey\d+|RegKey\d+)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            var parts = line[(split + 1)..].Split('|');
            if (key.StartsWith("FileKey", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length is < 2 or > 3) return false;
                if (parts.Length == 3 && parts[2].Trim().Length > 0 &&
                    !parts[2].Trim().Equals("RECURSE", StringComparison.OrdinalIgnoreCase) &&
                    !(adapting && parts[2].Trim().Equals("REMOVESELF", StringComparison.OrdinalIgnoreCase))) return false;
            }
            if (key.StartsWith("ExcludeKey", StringComparison.OrdinalIgnoreCase) &&
                (parts.Length is < 2 or > 3 || !new[] { "FILE", "PATH", "REG" }.Contains(parts[0].Trim(), StringComparer.OrdinalIgnoreCase))) return false;
        }
        return true;
    }
}
