param(
    [string]$Version = '',
    [string]$OutDir = 'build/release-assets'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'src/DevUp/Cleaning/RulePack.metadata.json') -Raw | ConvertFrom-Json
if ($Version) {
    $parsedVersion = $null
    if (-not [version]::TryParse($Version,[ref]$parsedVersion)) { throw '规则版本须为年.月.日.修订号。' }
    $metadata.Version = $Version
}
$metadata.Rules = [IO.File]::ReadAllText((Join-Path $projectRoot 'src/DevUp/Cleaning/Winapp2.safe.ini'))
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutDir))
[void][IO.Directory]::CreateDirectory($outputDirectory)
$target = Join-Path $outputDirectory 'DevUp.rules.json.gz'
$data = [Text.Encoding]::UTF8.GetBytes(($metadata | ConvertTo-Json -Depth 4 -Compress))
$output = [IO.File]::Create($target)
try {
    $gzip = [IO.Compression.GZipStream]::new($output,[IO.Compression.CompressionLevel]::Optimal,$true)
    try { $gzip.Write($data,0,$data.Length) } finally { $gzip.Dispose() }
} finally { $output.Dispose() }
$file = Get-Item -LiteralPath $target
if ($file.Length -gt 512KB) { throw '规则包超过客户端大小限制。' }
Write-Output ('规则版本：' + $metadata.Version)
Write-Output ('规则附件：' + $target)
Write-Output ('大小：' + $file.Length + ' bytes')
Get-FileHash -LiteralPath $target -Algorithm SHA256
