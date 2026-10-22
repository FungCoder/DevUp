using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DevUp.Core;
using DevUp.Models;

namespace DevUp.Services;

/// <summary>
/// 驱动仓库（DriverStore）清单：复用 Nefarius 的 DriverStore 封装做权威枚举，
/// INF 元数据用轻量解析器补齐（Provider / 类别 / 版本 / 日期），并用 WinVerifyTrust 验签。
/// </summary>
public sealed class DriverStoreService
{
    private readonly Dictionary<string, string> _oemByHash = new(StringComparer.OrdinalIgnoreCase);

    public List<DriverPackageItem> GetPackages(bool verifySignature = true)
    {
        var packages = new List<DriverPackageItem>();
        _oemByHash.Clear();

        foreach (var inf in EnumerateStoreInfs())
        {
            var info = InfReader.Read(inf);
            var published = ResolvePublishedName(inf);
            var item = new DriverPackageItem
            {
                InfPath = inf,
                PublishedName = published,
                OriginalName = Path.GetFileName(inf),
                Provider = info.Provider,
                ClassName = info.ClassName,
                DriverVersion = info.DriverVersion,
                DriverDate = info.DriverDate
            };
            item.Signature = verifySignature && item.IsThirdParty
                ? VerifyPackageSignature(inf)
                : (item.IsThirdParty ? "未检测" : "系统内置");
            packages.Add(item);
        }

        return packages
            .OrderByDescending(p => p.IsThirdParty)
            .ThenBy(p => p.Provider, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.ClassName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateStoreInfs()
    {
        try
        {
            var list = Nefarius.Utilities.DeviceManagement.Drivers.DriverStore.ExistingDrivers.ToList();
            if (list.Count > 0) return list;
        }
        catch (Exception ex)
        {
            Ui.Log($"读取 DriverStore 失败，改用兜底扫描: {ex.Message}", "WARN");
        }

        // 兜底：扫描 C:\Windows\INF\oem*.inf
        var infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        if (!Directory.Exists(infDir)) return Array.Empty<string>();
        return Directory.GetFiles(infDir, "oem*.inf");
    }

    /// <summary>把 DriverStore 中的原始 INF 映射到 C:\Windows\INF 下的已发布名 (oemN.inf)。</summary>
    private string ResolvePublishedName(string infPath)
    {
        if (_oemByHash.Count == 0) BuildOemIndex();

        if (_oemByHash.TryGetValue(HashFile(infPath), out var oem)) return oem;

        var name = Path.GetFileName(infPath);
        return name.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ? name : name;
    }

    private void BuildOemIndex()
    {
        var infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        if (!Directory.Exists(infDir)) return;

        foreach (var file in Directory.GetFiles(infDir, "oem*.inf"))
        {
            try { _oemByHash[HashFile(file)] = Path.GetFileName(file); }
            catch { }
        }
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static string VerifyPackageSignature(string infPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(infPath);
            if (dir is null || !Directory.Exists(dir)) return "无签名文件";

            var sys = Directory.GetFiles(dir, "*.sys").FirstOrDefault();
            if (sys is null) return "无驱动二进制";
            return Native.VerifySignature(sys);
        }
        catch (Exception ex)
        {
            return "校验失败: " + ex.Message;
        }
    }
}

/// <summary>极简 INF 解析：只取 [Version] 段需要的字段。</summary>
public sealed class InfReader
{
    public string Provider { get; private set; } = "";
    public string ClassName { get; private set; } = "";
    public string DriverVersion { get; private set; } = "";
    public string DriverDate { get; private set; } = "";

    public static InfReader Read(string path)
    {
        var reader = new InfReader();
        try
        {
            var text = ReadText(path);
            string section = "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }
                if (!section.Equals("Version", StringComparison.OrdinalIgnoreCase)) continue;

                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "provider":
                        reader.Provider = value;
                        break;
                    case "class":
                        reader.ClassName = value;
                        break;
                    case "driverver":
                        var parts = value.Split(',');
                        reader.DriverVersion = parts.Length > 1 ? parts[1].Trim() : "";
                        if (parts.Length > 0)
                        {
                            var rawDate = parts[0].Trim();
                            if (DateTime.TryParseExact(rawDate, new[] { "MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy" },
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                                reader.DriverDate = dt.ToString("yyyy-MM-dd");
                            else if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture,
                                         DateTimeStyles.None, out var dt2))
                                reader.DriverDate = dt2.ToString("yyyy-MM-dd");
                        }
                        break;
                }
            }
        }
        catch { }
        return reader;
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            // 非 UTF-8 的 INF（常见为 GBK）
            try { return Encoding.GetEncoding("gb18030").GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }
    }
}
