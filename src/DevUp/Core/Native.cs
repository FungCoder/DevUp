using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DevUp.Core;

/// <summary>原生 API 封装：管理员判定 + Authenticode 签名校验。</summary>
public static class Native
{
    // 注意：ShellExecuteExW 只接收一个参数（指向 SHELLEXECUTEINFOW 的指针）。
    // 之前误写成 (IntPtr hwnd, ref info)，导致指针被放进第二个参数、第一个参数为 NULL，
    // shell32 解引用空指针抛 AccessViolationException (0xC0000005)，进程启动即崩溃。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFOW info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const int SW_SHOWNORMAL = 1;

    /// <summary>以管理员身份启动自身，成功返回 true；用户取消 UAC 时返回 false。</summary>
    public static bool StartElevated(string exePath, string arguments)
    {
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = SEE_MASK_NOCLOSEPROCESS,
            hwnd = IntPtr.Zero,
            lpVerb = "runas",
            lpFile = exePath,
            lpParameters = arguments,
            lpDirectory = null,
            nShow = SW_SHOWNORMAL
        };

        if (!ShellExecuteEx(ref info))
        {
            int error = Marshal.GetLastWin32Error();
            if (info.hProcess != IntPtr.Zero) CloseHandle(info.hProcess);
            // 1223 = ERROR_CANCELLED：用户在 UAC 对话框点了“否”
            if (error == 1223) return false;
            throw new InvalidOperationException($"ShellExecuteEx 失败，错误代码 {error}。");
        }

        // 句柄只用于确认进程已创建，随即释放；子进程独立运行
        if (info.hProcess != IntPtr.Zero) CloseHandle(info.hProcess);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---------- WinVerifyTrust ----------

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    private const uint WTD_LIFETIME_SIGNING_FLAG = 0x00000800;
    private const uint WTD_SAFER_FLAG = 0x00000100;

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>校验文件的 Authenticode 签名，返回中文状态描述。</summary>
    public static string VerifySignature(string filePath)
    {
        if (!System.IO.File.Exists(filePath)) return "文件不存在";

        IntPtr fileInfoPtr = IntPtr.Zero;
        IntPtr filePathPtr = IntPtr.Zero;
        IntPtr wvtDataPtr = IntPtr.Zero;
        var actionGuid = WinTrustActionGenericVerifyV2;   // 需要 ref，必须是可变的本地副本
        try
        {
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            filePathPtr = Marshal.StringToHGlobalUni(filePath);

            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var wvtData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_LIFETIME_SIGNING_FLAG | WTD_SAFER_FLAG,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero
            };
            wvtDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(wvtData, wvtDataPtr, false);

            uint ret = WinVerifyTrust(IntPtr.Zero, ref actionGuid, wvtDataPtr);

            return ret switch
            {
                0u => "签名有效",
                0x800B0100u => "文件未签名",
                0x80096010u => "签名无效（文件校验值不匹配）",
                0x800B0109u => "未受信任的根证书",
                0x800B010Au => "证书链需要更新",
                0x800B0004u => "证书不受信任",
                0x800B0001u => "签名验证失败",
                _ => $"未验证 (0x{ret:X8})"
            };
        }
        catch (Exception ex)
        {
            return "校验异常: " + ex.Message;
        }
        finally
        {
            if (wvtDataPtr != IntPtr.Zero)
            {
                var close = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwStateAction = WTD_STATEACTION_CLOSE,
                    pFile = fileInfoPtr
                };
                Marshal.StructureToPtr(close, wvtDataPtr, false);
                WinVerifyTrust(IntPtr.Zero, ref actionGuid, wvtDataPtr);
                Marshal.FreeHGlobal(wvtDataPtr);
            }
            if (filePathPtr != IntPtr.Zero) Marshal.FreeHGlobal(filePathPtr);
            if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);
}
