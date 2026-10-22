<#
    DevUp 后端脚本 —— 复用层
    ------------------------------------------------------------
    本脚本只做三件事：
      1) 调用现成的开源/系统能力（PSWindowsUpdate、MSCatalog、winget、Get-PnpDevice、Checkpoint-Computer）
      2) 把结果整理成稳定 JSON
      3) 通过 -OutFile 落盘（规避 PowerShell 控制台编码问题）

    所有动作统一返回：{ ok, action, data, error }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Action,
    [string]$OutFile = '',
    [string]$ModulePath = '',
    [string]$CatalogModulePath = '',
    [switch]$MicrosoftUpdate,
    [string]$UpdateIds = '',
    [string]$PackageId = '',
    [string]$Version = '',
    [string]$SearchText = '',
    [string]$Description = 'DevUp',
    [string]$DownloadDirectory = '',
    [string]$BackupDirectory = '',
    [string]$PublishedNames = '',
    [switch]$Silent,
    [int]$First = 0
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Write-Result {
    param($data)
    $payload = [ordered]@{ ok = $true; action = $Action; data = $data; error = $null }
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    if ($OutFile) {
        [IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding($false)))
    } else {
        Write-Output $json
    }
}

function Write-Failure {
    param([string]$message)
    $payload = [ordered]@{ ok = $false; action = $Action; data = $null; error = $message }
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    if ($OutFile) {
        [IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding($false)))
    } else {
        Write-Output $json
    }
    exit 1
}

function Import-WuModule {
    if (-not $ModulePath -or -not (Test-Path $ModulePath)) {
        Write-Failure 'PSWindowsUpdate 模块缺失：请检查运行时目录中的 PSWindowsUpdate'
    }
    Import-Module $ModulePath -Force -ErrorAction Stop
}

# 提权子进程的参数来自命令行/临时文件，管理员进程必须把它们当作不可信输入处理。
function Get-SafeGuid {
    param([string]$Value)
    $guid = [guid]::Empty
    if ([guid]::TryParse($Value, [ref]$guid)) { return $guid.ToString() }
    return ''
}

function Get-SafeInfName {
    param([string]$Value)
    $name = $Value.Trim()
    if ($name -match '^(?i)oem\d{1,6}\.inf$') { return $name }
    return ''
}

# 从 PSWindowsUpdate 返回的更新对象里取值。
# 这些对象是包着 IUpdate 的 PSObject：标识字段在 Identity 下（UpdateID / RevisionNumber），
# 知识库编号在 KBArticleIDs（字符串集合）上，驱动专有字段只有底层是 IWindowsDriverUpdate 时才有。
# 逐个候选路径尝试，任何一步取不到都退到下一个，避免不同 WU 通道字段不一致时整行变空。
function Get-FieldValue {
    param($Object, [string[]]$Names)
    if ($null -eq $Object) { return '' }
    foreach ($name in $Names) {
        try {
            $value = $Object.$name
            if ($null -ne $value -and "$value" -ne '') { return $value }
        } catch { }
    }
    return ''
}

function Get-NestedValue {
    param($Object, [string[]]$Paths)
    if ($null -eq $Object) { return '' }
    foreach ($path in $Paths) {
        try {
            $current = $Object
            foreach ($segment in ($path -split '\.')) {
                if ($null -eq $current) { break }
                $current = $current.$segment
            }
            if ($null -ne $current -and "$current" -ne '') { return $current }
        } catch { }
    }
    return ''
}

function Get-ProblemCode {
    param($problem, $cmc)
    try { if ($cmc -ne $null) { return [int]$cmc } } catch { }
    switch ([string]$problem) {
        'CM_PROB_NO_CONFIG' { return 1 }
        'CM_PROB_DISABLED' { return 22 }
        'CM_PROB_FAILED_INSTALL' { return 28 }
        'CM_PROB_NEEDS_RESTART' { return 14 }
        'CM_PROB_NOT_STARTED' { return 10 }
        'CM_PROB_FAILED_DRIVER_ENTRY' { return 19 }
        'CM_PROB_HARDWARE_ERROR' { return 43 }
        'CM_PROB_FAILED_DRIVER_LOAD' { return 45 }
        'CM_PROB_DEVICE_NOT_CONNECTED' { return 16 }
        default { return 0 }
    }
}

# 判断一次驱动安装是否真的成功。
# PSWindowsUpdate 在安装后会给出状态标记（Status / Result / IsInstalled），
# 拿不到标记时不能直接说“安装成功”——但也不能说失败：
# 驱动安装常常要重启才生效，此时 Windows Update 不一定回填状态。
# 因此区分三态：confirmed（明确成功）/ failed（明确失败）/ submitted（已提交、结果待确认）。
function Get-InstallOutcome {
    param($Updates)

    $seen = 0
    $installed = $false
    $failed = $false
    $detail = ''

    foreach ($u in @($Updates)) {
        if ($null -eq $u) { continue }
        $seen++

        $result = [string](Get-FieldValue $u @('Result', 'InstallResult', 'Status'))
        if ($result) {
            $lower = $result.ToLowerInvariant()
            if ($lower -match 'fail|error|失败|拒绝|reject') { $failed = $true }
            elseif ($lower -match 'success|succeed|installed|成功|已安装') { $installed = $true }
            elseif ($result -eq 'I') { $installed = $true }
            elseif ($result -eq 'F') { $failed = $true }
            if (-not $detail) { $detail = $result }
        }

        $isInstalled = Get-FieldValue $u @('IsInstalled')
        if ($isInstalled -eq $true) { $installed = $true }
    }

    if ($seen -eq 0) {
        return @{ ok = $false; confirmed = $true; message = 'Windows Update 未返回任何安装结果' }
    }
    if ($failed -and -not $installed) {
        return @{ ok = $false; confirmed = $true; message = $(if ($detail) { $detail } else { '安装未成功' }) }
    }
    if ($installed) {
        return @{ ok = $true; confirmed = $true; message = $(if ($detail) { $detail } else { '已安装' }) }
    }

    # 已提交安装但没有明确结果：常见于需要重启才生效的驱动
    return @{ ok = $true; confirmed = $false; message = '已提交安装（Windows Update 未回填结果，请重启后重新扫描确认）' }
}

try {
    switch ($Action) {

        # ---------------- 设备清单 ----------------
        'devices' {
            $rows = Get-PnpDevice -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    instanceId    = [string]$_.InstanceId
                    friendlyName  = [string]$_.FriendlyName
                    className     = [string]$_.Class
                    status        = [string]$_.Status
                    problem       = [string]$_.Problem
                    problemCode   = (Get-ProblemCode $_.Problem $_.ConfigManagerErrorCode)
                    problemDesc   = [string]$_.ProblemDescription
                    isPresent     = [bool]$_.Present
                    manufacturer  = [string]$_.Manufacturer
                    service       = [string]$_.Service
                    hardwareIds   = (@($_.HardwareID) -join '; ')
                    compatibleIds = (@($_.CompatibleID) -join '; ')
                    classGuid     = [string]$_.ClassGuid
                }
            }
            Write-Result @{ devices = @($rows) }
        }

        # ---------------- 已签名驱动（版本/厂商/签名） ----------------
        'signedDrivers' {
            $rows = Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    deviceId = [string]$_.DeviceID
                    name     = [string]$_.DeviceName
                    version  = [string]$_.DriverVersion
                    provider = [string]$_.DriverProvider
                    date     = $(if ($_.DriverDate) { ([datetime]$_.DriverDate).ToString('yyyy-MM-dd') } else { '' })
                    inf      = [string]$_.InfName
                    signed   = [bool]$_.IsSigned
                    signer   = [string]$_.Signer
                }
            }
            Write-Result @{ drivers = @($rows) }
        }

        # ---------------- 系统概览 ----------------
        'systemInfo' {
            $os = Get-CimInstance Win32_OperatingSystem
            $cs = Get-CimInstance Win32_ComputerSystem
            $bios = Get-CimInstance Win32_BIOS
            $cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1

            $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
            $displayVersion = [string]$reg.DisplayVersion      # 例如 24H2 / 25H2
            $ubr = $reg.UBR                                     # 修订号

            $caption = ([string]$os.Caption) -replace '^Microsoft\s+', ''
            $build = [string]$os.BuildNumber
            if ($ubr) { $build = "$build.$ubr" }

            $cpuName = ''
            if ($cpu -and $cpu.Name) { $cpuName = ($cpu.Name -replace '\s+', ' ').Trim() }
            $cores = [int]$cs.NumberOfLogicalProcessors

            Write-Result @{
                system = [pscustomobject]@{
                    osName       = $caption
                    osVersion    = $displayVersion
                    osBuild      = $build
                    arch         = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' }
                                      elseif ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' })
                    installDate  = $(if ($os.InstallDate) { ([datetime]$os.InstallDate).ToString('yyyy-MM-dd') } else { '' })
                    manufacturer = [string]$cs.Manufacturer
                    model        = [string]$cs.Model
                    machineName  = [string]$cs.Name
                    cpu          = $cpuName
                    cores        = $cores
                    ram          = '{0:0.0} GB' -f ($cs.TotalPhysicalMemory / 1GB)
                    biosVersion  = [string]$bios.SMBIOSBIOSVersion
                    biosDate     = $(if ($bios.ReleaseDate) { ([datetime]$bios.ReleaseDate).ToString('yyyy-MM-dd') } else { '' })
                }
            }
        }

        # ---------------- Microsoft Store / MSIX 应用 ----------------
        'msixApps' {
            $rows = Get-AppxPackage -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    name      = [string]$_.Name
                    version   = [string]$_.Version
                    publisher = [string]$_.Publisher
                    fullName  = [string]$_.PackageFullName
                }
            }
            Write-Result @{ apps = @($rows) }
        }

        # ---------------- Windows Update / Microsoft Update 驱动候选 ----------------
        'driverUpdates' {
            Import-WuModule

            $warnings = New-Object System.Collections.ArrayList
            if ($MicrosoftUpdate) {
                try {
                    # -Confirm:$false 避免在隐藏窗口里等待交互确认
                    Add-WUServiceManager -MicrosoftUpdate -Confirm:$false -ErrorAction Stop | Out-Null
                } catch {
                    [void]$warnings.Add("注册 Microsoft Update 源失败：$($_.Exception.Message)")
                }
            }

            $criteria = "IsInstalled=0 and Type='Driver'"
            $updates = @()
            try {
                $updates = @(Get-WindowsUpdate -Criteria $criteria -AcceptAll -ErrorAction Stop)
            } catch {
                $msg = $_.Exception.Message
                if ($MicrosoftUpdate) {
                    # 交给调用方回退到 Windows Update 源
                    Write-Failure "Microsoft Update 搜索失败：$msg"
                }
                Write-Failure "查询 Windows Update 失败：$msg"
            }

            $rows = foreach ($u in $updates) {
                $verDate = ''
                $rawDate = Get-FieldValue $u @('DriverVerDate', 'DriverDate')
                if ($rawDate) {
                    try { $verDate = ([datetime]$rawDate).ToString('yyyy-MM-dd') } catch { $verDate = [string]$rawDate }
                }

                # UpdateID / RevisionNumber 在 Identity 下；直接属性只作为兜底
                $updateId = [string](Get-NestedValue $u @('Identity.UpdateID', 'UpdateID'))
                $revision = 0
                $rawRev = Get-NestedValue $u @('Identity.RevisionNumber', 'RevisionNumber')
                if ($rawRev -ne '') { [void][int]::TryParse([string]$rawRev, [ref]$revision) }

                # KBArticleIDs 是字符串集合；PSWindowsUpdate 自己也提供了 KB 汇总字段
                $kb = ''
                $kbIds = Get-FieldValue $u @('KBArticleIDs', 'KBArticleID')
                if ($kbIds -is [string]) { $kb = $kbIds }
                elseif ($null -ne $kbIds) { $kb = (@($kbIds) | ForEach-Object { [string]$_ }) -join ';' }
                if (-not $kb) { $kb = [string](Get-FieldValue $u @('KB')) }

                [pscustomobject]@{
                    title              = [string](Get-FieldValue $u @('Title'))
                    updateId           = $updateId
                    revision           = $revision
                    kb                 = $kb
                    driverModel        = [string](Get-FieldValue $u @('DriverModel'))
                    driverClass        = [string](Get-FieldValue $u @('DriverClass'))
                    driverManufacturer = [string](Get-FieldValue $u @('DriverManufacturer'))
                    driverVerDate      = $verDate
                    size               = [string](Get-FieldValue $u @('Size', 'MaxDownloadSize'))
                    eula               = [bool](Get-FieldValue $u @('EulaAccepted'))
                }
            }
            Write-Result @{ updates = @($rows); warning = ($warnings -join ' ') }
        }

        # ---------------- 安装驱动（按 UpdateID:Revision） ----------------
        'installDriver' {
            Import-WuModule

            $results = @()
            foreach ($pair in ($UpdateIds -split ',')) {
                if ([string]::IsNullOrWhiteSpace($pair)) { continue }
                $parts = $pair.Trim() -split ':'
                $id = Get-SafeGuid $parts[0]
                if (-not $id) {
                    $results += [pscustomobject]@{
                        updateId  = ''
                        ok        = $false
                        confirmed = $true
                        title     = ''
                        message   = '无效的更新标识'
                    }
                    continue
                }
                $rev = 0
                if ($parts.Count -gt 1) { [void][int]::TryParse($parts[1].Trim(), [ref]$rev) }
                try {
                    $installed = @(Get-WindowsUpdate -UpdateID $id -RevisionNumber $rev -AcceptAll -Install -IgnoreReboot -ErrorAction Stop)
                    $outcome = Get-InstallOutcome $installed
                    $results += [pscustomobject]@{
                        updateId = $id
                        ok       = [bool]$outcome.ok
                        confirmed= [bool]$outcome.confirmed
                        title    = [string](Get-FieldValue $installed @('Title'))
                        message  = [string]$outcome.message
                    }
                } catch {
                    $results += [pscustomobject]@{
                        updateId  = $id
                        ok        = $false
                        confirmed = $true
                        title     = ''
                        message   = $_.Exception.Message
                    }
                }
            }
            Write-Result @{ results = @($results) }
        }

        # ---------------- Microsoft Update Catalog 检索（按硬件 ID） ----------------
        'catalogSearch' {
            if (-not $CatalogModulePath -or -not (Test-Path $CatalogModulePath)) {
                Write-Failure 'MSCatalog 模块缺失，无法检索更新目录'
            }
            if ([string]::IsNullOrWhiteSpace($SearchText)) { Write-Failure '缺少 SearchText' }

            Import-Module $CatalogModulePath -Force -ErrorAction Stop

            # MSCatalog 0.27 提供的命令是 Get-MSCatalogUpdate（-Search），没有 Find-CatalogUpdate
            $found = @(Get-MSCatalogUpdate -Search $SearchText -Strict -ErrorAction Stop)
            if ($First -gt 0 -and $found.Count -gt $First) {
                $found = @($found | Select-Object -First $First)
            }

            $rows = foreach ($u in $found) {
                $verDate = ''
                $rawDate = Get-FieldValue $u @('LastUpdated')
                if ($rawDate) {
                    try { $verDate = ([datetime]$rawDate).ToString('yyyy-MM-dd') } catch { $verDate = [string]$rawDate }
                }

                # MSCatalogUpdate 只有这些字段：Title / Products / Classification / LastUpdated / Version / Size / Guid
                [pscustomobject]@{
                    title              = [string](Get-FieldValue $u @('Title'))
                    updateId           = [string](Get-FieldValue $u @('Guid'))
                    revision           = 0
                    driverModel        = ''
                    driverClass        = [string](Get-FieldValue $u @('Classification'))
                    driverManufacturer = [string](Get-FieldValue $u @('Products'))
                    driverVerDate      = $verDate
                    size               = [string](Get-FieldValue $u @('Size'))
                    version            = [string](Get-FieldValue $u @('Version'))
                }
            }
            Write-Result @{ updates = @($rows) }
        }

        # ---------------- 驱动备份（pnputil /export-driver） ----------------
        'exportDrivers' {
            if ([string]::IsNullOrWhiteSpace($BackupDirectory)) { Write-Failure '缺少 BackupDirectory' }
            $dir = $BackupDirectory
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

            $names = @()
            if (-not [string]::IsNullOrWhiteSpace($PublishedNames)) {
                $names = @($PublishedNames -split ',' | ForEach-Object { Get-SafeInfName $_ } | Where-Object { $_ })
            }

            $results = @()
            if ($names.Count -eq 0) {
                # 未指定驱动包时导出全部第三方驱动
                $out = & pnputil.exe /export-driver '*' "$dir" 2>&1 | Out-String
                $results += [pscustomobject]@{
                    name    = '*'
                    ok      = ($LASTEXITCODE -eq 0)
                    message = $(if ($LASTEXITCODE -eq 0) { '已导出全部驱动包' } else { $out.Trim() })
                }
            } else {
                foreach ($name in $names) {
                    $out = & pnputil.exe /export-driver $name "$dir" 2>&1 | Out-String
                    $results += [pscustomobject]@{
                        name    = $name
                        ok      = ($LASTEXITCODE -eq 0)
                        message = $(if ($LASTEXITCODE -eq 0) { '导出成功' } else { $out.Trim() })
                    }
                }
            }
            Write-Result @{ results = @($results); directory = $dir }
        }

        # ---------------- 驱动还原（pnputil /add-driver） ----------------
        'importDrivers' {
            if ([string]::IsNullOrWhiteSpace($BackupDirectory)) { Write-Failure '缺少 BackupDirectory' }
            $dir = $BackupDirectory
            if (-not (Test-Path $dir)) { Write-Failure "备份目录不存在：$dir" }

            $infs = @(Get-ChildItem -Path $dir -Filter '*.inf' -Recurse -File -ErrorAction SilentlyContinue)
            if ($infs.Count -eq 0) { Write-Failure '备份目录中没有 INF 文件' }

            $results = @()
            $okCount = 0
            foreach ($inf in $infs) {
                # pnputil 只接受 INF 文件本身，不接受目录，因此逐个安装
                $out = & pnputil.exe /add-driver $inf.FullName /install 2>&1 | Out-String
                $ok = ($LASTEXITCODE -eq 0)
                if ($ok) { $okCount++ }
                $results += [pscustomobject]@{
                    name    = $inf.Name
                    ok      = $ok
                    message = $(if ($ok) { '还原成功' } else { $out.Trim() })
                }
            }
            Write-Result @{ results = @($results); installed = $okCount; total = $infs.Count }
        }

        # ---------------- winget 可用更新 ----------------
        'softwareUpdates' {
            $raw = & winget upgrade --accept-source-agreements --disable-interactivity 2>&1 | Out-String
            $rows = @()
            foreach ($line in ($raw -split "`r?`n")) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                $tokens = @($line.Trim() -split '\s+')
                if ($tokens.Count -lt 5) { continue }

                # 表格结构: 名称 | ID | 已安装版本 | 可用版本 | 源
                # 列宽溢出时相邻字段可能只隔一个空格，因此从右往左锚定最稳
                $source = $tokens[$tokens.Count - 1]
                $available = $tokens[$tokens.Count - 2]
                $installed = $tokens[$tokens.Count - 3]
                $id = $tokens[$tokens.Count - 4]
                $name = ($tokens[0..($tokens.Count - 5)] -join ' ')

                if ($id -notmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]*\.[A-Za-z0-9._+\-]+$') { continue }
                if ($available -notmatch '^[0-9]') { continue }
                if ($installed -notmatch '^[0-9]') { continue }

                $rows += [pscustomobject]@{
                    name             = $name
                    id               = $id
                    installedVersion = $installed
                    availableVersion = $available
                    source           = $source
                }
            }
            Write-Result @{ updates = @($rows) }
        }

        # ---------------- 执行软件更新 ----------------
        'installSoftware' {
            if ([string]::IsNullOrWhiteSpace($PackageId)) { Write-Failure '缺少 PackageId' }
            $wargs = @('upgrade', '--id', $PackageId, '--exact',
                       '--accept-source-agreements', '--accept-package-agreements',
                       '--disable-interactivity')
            if ($Silent) { $wargs += '--silent' }
            $out = & winget @wargs 2>&1 | Out-String
            Write-Result @{ exitCode = $LASTEXITCODE; output = $out.Trim() }
        }

        # ---------------- 解析安装包下载地址（winget show） ----------------
        'installerUrl' {
            if ([string]::IsNullOrWhiteSpace($PackageId)) { Write-Failure '缺少 PackageId' }
            $wargs = @('show', '--id', $PackageId, '--exact', '--accept-source-agreements', '--disable-interactivity')
            if (-not [string]::IsNullOrWhiteSpace($Version)) { $wargs += @('--version', $Version) }
            $raw = & winget @wargs 2>&1 | Out-String
            $urls = New-Object System.Collections.ArrayList
            foreach ($line in ($raw -split "`r?`n")) {
                foreach ($m in [regex]::Matches($line, 'https?://[^\s"''<>]+')) {
                    $u = $m.Value
                    if ($u -match '\.(msi|msix|msixbundle|appx|exe|zip|7z|msu)(\?|$)') {
                        if (-not $urls.Contains($u)) { [void]$urls.Add($u) }
                    }
                }
            }
            Write-Result @{ urls = @($urls) }
        }

        # ---------------- 下载安装包（离线保存到用户指定目录） ----------------
        'downloadSoftware' {
            if ([string]::IsNullOrWhiteSpace($PackageId)) { Write-Failure '缺少 PackageId' }
            $dir = $DownloadDirectory
            if ([string]::IsNullOrWhiteSpace($dir)) { $dir = Join-Path $env:USERPROFILE 'Downloads\DevUp' }
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

            $wargs = @('download', '--id', $PackageId, '--exact', '--download-directory', $dir,
                       '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity')
            $out = & winget @wargs 2>&1 | Out-String
            Write-Result @{ exitCode = $LASTEXITCODE; output = $out.Trim(); directory = $dir }
        }

        # ---------------- 系统还原点 ----------------
        'restorePoint' {
            try { Enable-ComputerRestore -Drive "$($env:SystemDrive)\" -ErrorAction SilentlyContinue } catch { }
            Checkpoint-Computer -Description $Description -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop
            Write-Result @{ created = $true; description = $Description }
        }

        default {
            Write-Failure "未知动作: $Action"
        }
    }
}
catch {
    Write-Failure $_.Exception.Message
}
