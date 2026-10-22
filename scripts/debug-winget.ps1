$raw = & winget upgrade --accept-source-agreements --disable-interactivity 2>&1 | Out-String
$lines = $raw -split "`r?`n"
$i = 0
foreach ($line in $lines) {
    $i++
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $cols = @($line -split '\s{2,}' | Where-Object { $_.Trim() -ne '' })
    "[$i] cols=$($cols.Count) :: " + (($cols | ForEach-Object { "[$_]" }) -join ' ')
}
