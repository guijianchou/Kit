Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p = Get-Process -Name 'Kit.Settings' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { 'not running'; return }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

Write-Host '=== 全部可交互元素（含进度条/文本） ==='
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$prog = 0; $txt = 0
foreach ($e in $all) {
  $ct = $e.Current.ControlType.ProgrammaticName.Replace('ControlType.','')
  if ($ct -eq 'ProgressBar') { $prog++; $vp=$null; $v='?'; if ($e.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern,[ref]$vp)) { $v = $vp.Current.Value }; Write-Host ('  ProgressBar name="' + $e.Current.Name + '" value=' + $v + ' offscreen=' + $e.Current.IsOffscreen) }
  elseif ($ct -eq 'Text' -and $e.Current.Name -and ($e.Current.Name -match 'Scan|scan|扫描|AI|Health|score|finding|audit')) { $txt++; Write-Host ('  Text: ' + $e.Current.Name.Substring(0, [Math]::Min(110, $e.Current.Name.Length))) }
}
Write-Host ('ProgressBar count = ' + $prog)