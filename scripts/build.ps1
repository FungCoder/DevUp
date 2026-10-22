# 一键构建 DevUp（单文件、自包含、win-x64）
# 用法： powershell -ExecutionPolicy Bypass -File scripts\build.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 优先使用用户目录下的 SDK（无需管理员），回退到 PATH
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

Write-Host '==> 关闭正在运行的 DevUp' -ForegroundColor Cyan
Get-Process DevUp -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host '==> 校正脚本编码（UTF-8 with BOM）' -ForegroundColor Cyan
if (Test-Path 'scripts\fix-encoding.ps1') {
    & powershell -NoProfile -ExecutionPolicy Bypass -File 'scripts\fix-encoding.ps1' | Out-Null
}

Write-Host '==> 校验后端脚本语法' -ForegroundColor Cyan
$script = Resolve-Path 'src\DevUp\Scripts\devup.ps1'
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
if ($errors -and $errors.Count) {
    $errors | ForEach-Object { Write-Host "  $($_.Message) (行 $($_.Extent.StartLineNumber))" -ForegroundColor Red }
    throw '后端脚本存在语法错误'
}

Write-Host '==> 发布' -ForegroundColor Cyan
& $dotnet publish 'src\DevUp\DevUp.csproj' -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o 'build\publish'
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

$exe = Resolve-Path 'build\publish\DevUp.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ''
Write-Host "输出：$exe" -ForegroundColor Green
Write-Host "大小：$size MB" -ForegroundColor Green
