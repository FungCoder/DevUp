# 直接调用 Windows Update Agent COM，验证驱动候选通道（不经过 PSWindowsUpdate 的提权限制）
$session = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()
$searcher.ServiceID = '7971f918-a847-4430-9279-4a52d1efe18d'
$searcher.ServerSelection = 3
$searcher.SearchScope = 3
$sw = [Diagnostics.Stopwatch]::StartNew()
$result = $searcher.Search("IsInstalled=0 and Type='Driver'")
"耗时 $($sw.ElapsedMilliseconds)ms 候选数 $($result.Updates.Count)"
$result.Updates | Select-Object -First 12 | ForEach-Object {
    $verDate = ''
    try { if ($_.DriverVerDate) { $verDate = ([datetime]$_.DriverVerDate).ToString('yyyy-MM-dd') } } catch { }
    [pscustomobject]@{
        Title   = $_.Title
        Model   = $_.DriverModel
        Class   = $_.DriverClass
        Vendor  = $_.DriverManufacturer
        VerDate = $verDate
        Id      = $_.UpdateID
        Rev     = $_.RevisionNumber
    }
} | Format-Table -AutoSize | Out-String -Width 200
