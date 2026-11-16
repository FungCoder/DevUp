param([switch]$NoRestart)
$ErrorActionPreference = 'Stop'
$sourceStream = $null
$oldMoved = $false
$newCreated = $false
$parentGone = $false
$target = $null
$backup = $null
$resultPath = $null

function Write-UpdateResult([bool]$Success, [string]$Message) {
    if ($null -eq $resultPath) { return }
    $result = @{ Success=$Success; Message=$Message } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($resultPath, $result, [Text.UTF8Encoding]::new($true))
}
function Assert-PlainPath([string]$Path) {
    for ($part = $Path; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if (([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw '更新路径含有目录链接，已停止。'
        }
    }
}
try {
    $job = [IO.Path]::GetFullPath($PSScriptRoot)
    $updates = Split-Path -Parent $job
    $storage = Split-Path -Parent $updates
    $appRoot = Split-Path -Parent $storage
    if ((Split-Path -Leaf $updates) -ne 'updates' -or (Split-Path -Leaf $storage) -ne 'DevUpData') {
        throw '更新辅助脚本所在目录不正确。'
    }
    Assert-PlainPath $job
    $resultPath = Join-Path $updates 'update-result.json'
    $manifestPath = Join-Path $job 'update.json'
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 8192) { throw '更新参数过大。' }
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ([IO.Path]::GetFileName([string]$manifest.TargetName) -ne [string]$manifest.TargetName -or
        [string]$manifest.TargetName -notmatch '^[^<>:"/\\|?*]+\.exe$' -or
        [string]$manifest.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [int]$manifest.ParentId -le 0 -or
        [long]$manifest.ParentStartedUtcTicks -le 0) { throw '更新参数格式无效。' }
    $version = [version]([string]$manifest.Version)
    $target = Join-Path $appRoot ([string]$manifest.TargetName)
    $backup = Join-Path $updates 'DevUp.previous.exe'
    $source = Join-Path $job 'DevUp.new.exe'
    Assert-PlainPath $source
    Assert-PlainPath $target
    if ([IO.File]::Exists($backup)) { Assert-PlainPath $backup }
    $sourceStream = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = ([BitConverter]::ToString($sha.ComputeHash($sourceStream))).Replace('-','') }
    finally { $sha.Dispose() }
    if ($actual -ne [string]$manifest.Sha256) { throw '新版程序 SHA256 校验失败。' }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($source)
    $oldVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($target)
    if ($fileVersion.ProductName -ne 'DevUp 驱动与软件更新助手' -or
        [version]$fileVersion.FileVersion -ne [version]::new($version.Major,$version.Minor,[Math]::Max(0,$version.Build),[Math]::Max(0,$version.Revision))) {
        throw '新版程序的产品信息或版本不匹配。'
    }
    if ($oldVersion.ProductName -ne $fileVersion.ProductName -or [version]$oldVersion.FileVersion -gt $version) {
        throw '原文件不是 DevUp 或目标版本较旧，已停止更新。'
    }
    [IO.File]::WriteAllText((Join-Path $job 'ready'), 'ready')
    try { $parent = [Diagnostics.Process]::GetProcessById([int]$manifest.ParentId) }
    catch [ArgumentException] { $parent = $null }
    if ($null -ne $parent) {
        try {
            if ($parent.StartTime.ToUniversalTime().Ticks -ne [long]$manifest.ParentStartedUtcTicks) {
                throw '原进程标识已改变，已停止更新。'
            }
            if (-not $parent.WaitForExit(45000)) { throw '等待 DevUp 退出超时，原程序未替换。' }
        } finally { $parent.Dispose() }
    }
    $parentGone = $true
    if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    [IO.File]::Move($target, $backup)
    $oldMoved = $true
    $destination = [IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $newCreated = $true
    try { $sourceStream.Position=0; $sourceStream.CopyTo($destination); $destination.Flush() }
    finally { $destination.Dispose() }
    if (-not $NoRestart) {
        $start = [Diagnostics.ProcessStartInfo]::new($target)
        $start.UseShellExecute = $true
        $start.WorkingDirectory = $appRoot
        $start.Arguments = '--updated'
        $restarted = [Diagnostics.Process]::Start($start)
        if ($null -eq $restarted) { throw '新版启动失败。' }
        $restarted.Dispose()
    }
    Write-UpdateResult $true 'DevUp 已更新，旧版保留在 DevUpData/updates/DevUp.previous.exe。'
} catch {
    $message = $_.Exception.Message
    $restored = -not $oldMoved
    if ($oldMoved) {
        try {
            if ($newCreated -and [IO.File]::Exists($target)) { [IO.File]::Delete($target) }
            if ([IO.File]::Exists($target)) { throw '恢复目标位置已被占用。' }
            [IO.File]::Move($backup,$target)
            $restored = $true
        } catch { $message += '；自动还原未完成，请使用 DevUpData/updates/DevUp.previous.exe 恢复。' }
    }
    Write-UpdateResult $false $message
    if ($parentGone -and $restored -and -not $NoRestart) {
        try {
            $restoreStart = [Diagnostics.ProcessStartInfo]::new($target)
            $restoreStart.UseShellExecute = $true
            $restoreStart.WorkingDirectory = $appRoot
            $restoredProcess = [Diagnostics.Process]::Start($restoreStart)
            if ($null -ne $restoredProcess) { $restoredProcess.Dispose() }
        } catch { }
    }
    exit 1
} finally {
    if ($null -ne $sourceStream) { $sourceStream.Dispose() }
    # 只删除本次任务的三个固定文件；不递归删除任何目录。
    if ($null -ne $resultPath) {
        foreach ($name in @('DevUp.new.exe','update.json','apply-update.ps1','ready')) {
            try { [IO.File]::Delete((Join-Path $job $name)) } catch { }
        }
        try { [IO.Directory]::Delete($job) } catch { }
    }
}
