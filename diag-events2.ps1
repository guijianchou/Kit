$to = [DateTime]::UtcNow
$from = $to.AddDays(-1)
$f = $from.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$t = $to.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$xpath = "*[System[TimeCreated[@SystemTime >= '$f' and @SystemTime <= '$t'] and (Level=1 or Level=2 or Level=3)]]"
$all = @()
foreach ($log in @('System','Application','Setup')) {
  try { $all += Get-WinEvent -FilterXPath $xpath -LogName $log -MaxEvents 2000 -ErrorAction Stop } catch {}
}
Write-Host ('24h 事件总数: ' + $all.Count)
Write-Host ''
Write-Host '--- 按 Provider/EventId 分组 ---'
$all | Group-Object ProviderName, Id | Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object { '{0,4}  {1}' -f $_.Count, $_.Name }
Write-Host ''
Write-Host '--- 按 Level ---'
$all | Group-Object LevelDisplayName | ForEach-Object { '{0,4}  {1}' -f $_.Count, $_.Name }