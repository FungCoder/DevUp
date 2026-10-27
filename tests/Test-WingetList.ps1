$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\src\DevUp\Scripts\devup.ps1'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $scriptPath), [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw "后端脚本语法错误：$parseErrors" }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'ConvertFrom-WingetList'
}, $true)
if (-not $definition) { throw '缺少列表解析函数' }
$parse = $definition.Body.GetScriptBlock()
$fixture = @'
名称                ID                   版本          可用
-------------------------------------------------------------
QQ                  Tencent.QQ.NT        9.9.33.52230
QQ音乐              Tencent.QQMusic      22.76
Docker Desktop      Docker.DockerDesktop 4.96.1        4.97.0
Product v1.0        Vendor.Product       1.0           winget
工具                Vendor.Tool          Unknown       2.0
Node.js (LTS)       OpenJS.NodeJS.LTS     v24.19.0
Long Name           Vendor.LongName      1.0           2.0 winget
共有 2 个可升级项。
'@
$rows = @(& $parse $fixture)
if ($rows.Count -ne 7) { throw "解析条目数量错误：$($rows.Count)" }
if ($rows[0].id -ne 'Tencent.QQ.NT' -or $rows[0].availableVersion) { throw 'QQ 无更新条目错误' }
if ($rows[1].id -ne 'Tencent.QQMusic' -or $rows[1].availableVersion) { throw 'QQ 音乐无更新条目错误' }
if ($rows[2].availableVersion -ne '4.97.0') { throw '有更新条目错误' }
if ($rows[3].id -ne 'Vendor.Product' -or $rows[3].availableVersion) { throw '名称含版本号或来源列解析错误' }
if ($rows[4].installedVersion -ne 'Unknown') { throw '未知已装版本解析错误' }
if ($rows[5].installedVersion -ne 'v24.19.0') { throw 'v 前缀版本解析错误' }
if ($rows[6].availableVersion -ne '2.0') { throw '带来源列的更新解析错误' }
Write-Host 'winget 列表解析检查通过：7 种条目。'
