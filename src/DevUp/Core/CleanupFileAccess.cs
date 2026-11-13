using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DevUp.Models;
using Microsoft.Win32.SafeHandles;

namespace DevUp.Core;

/// <summary>通过独占句柄验证并删除扫描到的同一个文件，不跟随链接。</summary>
internal static class CleanupFileAccess
{
    private const uint ReadAttributes = 0x80, DeleteAccess = 0x10000;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
        public System.Runtime.InteropServices.ComTypes.FILETIME Access;
        public System.Runtime.InteropServices.ComTypes.FILETIME Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int informationClass, ref byte information, uint size);

    public static void DeleteVerified(CleanupFile candidate)
    {
        using var handle = CreateFileW(candidate.Path, ReadAttributes | DeleteAccess, 0,
            IntPtr.Zero, 3, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint |
                               FileAttributes.ReadOnly)) != 0 || info.Links != 1)
            throw new IOException("文件为链接、只读文件或类型已改变，已跳过。");

        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity)
            throw new IOException("无法验证文件实际路径，已跳过。");
        var actual = path.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, candidate.Path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("文件实际路径与扫描路径不一致，已跳过。");
        var bytes = ((long)info.SizeHigh << 32) | info.SizeLow;
        if (bytes != candidate.Length || ToFileTime(info.Write) != candidate.LastWriteUtc.ToFileTimeUtc()
            || ToFileTime(info.Creation) != candidate.CreationUtc.ToFileTimeUtc())
            throw new IOException("扫描后文件已发生变化，请重新扫描。");

        byte delete = 1;
        // FILE_DISPOSITION_INFO 的 DeleteFile 是单字节 BOOLEAN；关闭同一句柄时删除。
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static long ToFileTime(System.Runtime.InteropServices.ComTypes.FILETIME time)
        => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
}
