$to = [DateTime]::UtcNow
$from = $to.AddDays(-1)
$f = $from.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$t = $to.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$xpath = "*[System[TimeCreated[@SystemTime >= '$f' and @SystemTime <= '$t'] and (Level=1 or Level=2 or Level=3)]]"
Write-Host ('XPath = ' + $xpath)
Write-Host ''
$q = [System.Diagnostics.Eventing.Reader.EventLogQuery]::new('System', [System.Diagnostics.Eventing.Reader.PathType]::LogName, $xpath)
$q.ReverseDirection = $true
$r = [System.Diagnostics.Eventing.Reader.EventLogReader]::new($q)
$n = 0
while ($true) { $e = $r.ReadEvent(); if ($null -eq $e) { break }; $n++; $e.Dispose(); if ($n -ge 2000) { break } }
$r.Dispose()
Write-Host ('EventLogReader 读到: ' + $n + ' 条')