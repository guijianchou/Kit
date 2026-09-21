Get-Process -Name 'Kit*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$p = Start-Process 'C:\Users\Zen\Repos\Codings\Kit\x64\Debug\WinUI3Apps\Kit.Settings.exe' -PassThru
'pid=' + $p.Id
Start-Sleep -Seconds 15
$p.Refresh(); if ($p.HasExited) { 'EXITED ' + $p.ExitCode; return }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { 'no window'; return }

$nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'AI Hub')
$nav = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
if ($nav) { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); 'AI Hub selected'; Start-Sleep -Seconds 6 }

$bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)
$target = $null
foreach ($b in $btns) { if ($b.Current.Name -eq 'Fast scan') { $target = $b } }
if (-not $target) { 'Fast scan NOT FOUND'; foreach ($b in $btns) { if ($b.Current.Name) { '  btn: ' + $b.Current.Name } }; return }
'invoking Fast scan (enabled=' + $target.Current.IsEnabled + ')'
$target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
for ($i = 0; $i -lt 24; $i++) {
  Start-Sleep -Milliseconds 500
  $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ProgressBar)
  $pb = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pc)
  if ($pb.Count -gt 0) { foreach ($b in $pb) { $vp=$null; $v='?'; if ($b.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern,[ref]$vp)) { $v=$vp.Current.Value }; Write-Host ('  t=' + ($i*0.5) + 's ProgressBar "' + $b.Current.Name + '" value=' + $v) } }
}
'done observing'