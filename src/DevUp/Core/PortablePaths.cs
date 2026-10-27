using System.IO;

namespace DevUp.Core;

/// <summary>应用自有数据统一存入 exe 旁的 DevUpData，不受启动工作目录影响。</summary>
public static class PortablePaths
{
    public static string Root { get; } = Path.GetFullPath(AppContext.BaseDirectory);
    public static string StorageRoot => Path.Combine(Root, "DevUpData");
    public static string Data => Path.Combine(StorageRoot, "data");
    public static string Logs => Path.Combine(StorageRoot, "logs");
    public static string Runtime => Path.Combine(StorageRoot, "runtime");
    public static string Temp => Path.Combine(StorageRoot, "temp");

    /// <summary>程序目录内的路径保存为相对路径，移动整个文件夹后仍跟随程序。</summary>
    public static string ToStoredPath(string path)
    {
        var absolute = Path.GetFullPath(path, Root);
        var relative = Path.GetRelativePath(Root, absolute);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)
            || Path.IsPathRooted(relative) ? absolute : relative;
    }

    /// <summary>验证目录可写并返回绝对路径；失败时抛出异常，不更换保存位置。</summary>
    public static string EnsureWritableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("保存目录不能为空。", nameof(path));

        var directory = Path.GetFullPath(path, Root);
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".devup-write-{Guid.NewGuid():N}");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法写入目录：{directory}。请将程序放在可写目录，或重新选择保存位置。", ex);
        }
    }

    /// <summary>检查应用自有目录，不修改系统或第三方工具的环境变量。</summary>
    public static void Initialize()
    {
        foreach (var directory in new[] { Data, Logs, Runtime, Temp })
            EnsureWritableDirectory(directory);
    }
}
