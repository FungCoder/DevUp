param(
    [string]$Exe = 'E:\Develop Projects\DevUp\build\single-file\DevUp.exe',
    [int]$WaitSeconds = 14,
    [string]$Out = 'E:\Develop Projects\DevUp\build\ui.png',
    [string]$Page = '',
    [string[]]$Extra = @(),
    [switch]$Screen,
    [long]$Hwnd = 0
)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public class WinCapture {
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    public static void MakeDpiAware() { try { SetProcessDPIAware(); } catch { } }

    public static string CaptureScreen(string path) {
        var w = GetSystemMetrics(0);
        var h = GetSystemMetrics(1);
        using (var bmp = new Bitmap(w, h))
        using (var g = Graphics.FromImage(bmp)) {
            g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
            bmp.Save(path, ImageFormat.Png);
        }
        return w + "x" + h;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    public static string Capture(IntPtr hwnd, string path) {
        RECT r;
        GetWindowRect(hwnd, out r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return "BAD_RECT";
        using (var bmp = new Bitmap(w, h))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, 2);
            g.ReleaseHdc(hdc);
            bmp.Save(path, ImageFormat.Png);
        }
        return w + "x" + h;
    }
    public static void Focus(IntPtr hwnd) { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); }
}
"@

Get-Process DevUp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

[WinCapture]::MakeDpiAware()

if ($Hwnd -ne 0) {
    $result = if ($Screen) { [WinCapture]::CaptureScreen($Out) } else { [WinCapture]::Capture([IntPtr]$Hwnd, $Out) }
    Write-Host "截图: $Out  尺寸: $result"
    exit 0
}

$argList = @()
if ($Page) { $argList = @('--page', $Page) }
if ($Extra) { $argList += $Extra }

$p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.HasExited) { Write-Host "进程已退出，退出码 $($p.ExitCode)"; exit 1 }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "未找到主窗口"; exit 1 }

[WinCapture]::Focus($hwnd)
Start-Sleep -Seconds $WaitSeconds
[WinCapture]::Focus($hwnd)
Start-Sleep -Milliseconds 800

$result = if ($Screen) { [WinCapture]::CaptureScreen($Out) } else { [WinCapture]::Capture($hwnd, $Out) }
Write-Host "截图: $Out  尺寸: $result"
