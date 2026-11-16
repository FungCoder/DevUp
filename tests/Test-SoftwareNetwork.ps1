$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\src\DevUp\Scripts\devup.ps1'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $scriptPath), [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw "后端脚本语法错误：$parseErrors" }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-SoftwareNetworkArguments'
}, $true)
if (-not $definition) { throw '缺少软件网络参数函数' }
. ([scriptblock]::Create($definition.Extent.Text))
$SoftwareProxyMode = 'default'
$SoftwareProxyAddress = ''
$arguments = @('show')
$arguments += Get-SoftwareNetworkArguments
if ($arguments.Count -ne 1) { throw '默认模式改变了 winget 参数' }
$SoftwareProxyMode = 'direct'
$arguments = @('show')
$arguments += Get-SoftwareNetworkArguments
if ($arguments.Count -ne 2 -or $arguments[1] -ne '--no-proxy') { throw '直连参数错误' }
$SoftwareProxyMode = 'custom'
$SoftwareProxyAddress = 'http://127.0.0.1:7890'
$arguments = @('show')
$arguments += Get-SoftwareNetworkArguments
if ($arguments.Count -ne 3 -or $arguments[1] -ne '--proxy' -or
    $arguments[2] -ne 'http://127.0.0.1:7890/') { throw '自定义代理参数错误' }
$SoftwareProxyAddress = 'http://user:password@localhost:7890'
$rejected = $false
try { Get-SoftwareNetworkArguments | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw '后端允许了包含凭据的代理地址' }
Write-Host '软件网络后端检查通过：4 项。'
