Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p = Get-Process -Name 'Kit.Settings' | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { 'window not found'; return }
Write-Host ('WINDOW: ' + $win.Current.Name)
Write-Host ''
Write-Host '=== 导航栏项目 (ListItem) ==='
$lc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $lc)
foreach ($i in $items) { $n = $i.Current.Name; if ($n) { Write-Host ('  [' + $i.Current.ControlType.ProgrammaticName.Replace('ControlType.','') + '] ' + $n) } }
Write-Host ''
Write-Host '=== 进度条 (ProgressBar) ==='
$pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ProgressBar)
$bars = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pc)
Write-Host ('  找到 ' + $bars.Count + ' 个 ProgressBar')
foreach ($b in $bars) {
  $vp = $null
  if ($b.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$vp)) {
    Write-Host ('    name="' + $b.Current.Name + '" value=' + $vp.Current.Value + ' visible=' + (-not $b.Current.IsOffscreen))
  } else {
    Write-Host ('    name="' + $b.Current.Name + '" visible=' + (-not $b.Current.IsOffscreen))
  }
}