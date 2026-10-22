<#
    确保 PowerShell 脚本以 UTF-8 with BOM 保存（仅限纯 UTF-8 文件）。
    Windows PowerShell 5.1 在没有 BOM 时按系统 ANSI 代码页(GBK)解析脚本，
    中文内容会被破坏并引发难以定位的语法错误。
    注意：UTF-16 的模块清单（如 PSWindowsUpdate.psd1）自带 BOM，必须跳过。
#>
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$targets = @(
    (Join-Path $Root 'src\DevUp\Scripts\devup.ps1'),
    (Join-Path $Root 'lib\MSCatalog\MSCatalog.psd1'),
    (Join-Path $Root 'lib\MSCatalog\MSCatalog.psm1'),
    (Join-Path $Root 'lib\PSWindowsUpdate\PSWindowsUpdate.psd1'),
    (Join-Path $Root 'lib\PSWindowsUpdate\PSWindowsUpdate.psm1')
)

# 项目自带的开发脚本（可能含中文注释）
$targets += Get-ChildItem (Join-Path $Root 'scripts') -Filter '*.ps1' -File -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty FullName

$utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)

foreach ($file in $targets) {
    if (-not (Test-Path $file)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($file)

    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        Write-Host "跳过 UTF-16LE : $file"; continue
    }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        Write-Host "跳过 UTF-16BE : $file"; continue
    }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        Write-Host "已有 BOM       : $file"; continue
    }

    try {
        $text = $utf8Strict.GetString($bytes)
    } catch {
        Write-Host "跳过(非 UTF-8)  : $file"; continue
    }

    [System.IO.File]::WriteAllText($file, $text, $utf8Bom)
    Write-Host "已加 UTF-8 BOM  : $file"
}
