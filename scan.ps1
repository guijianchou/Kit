Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p = @(Get-Process -Name 'Kit.Settings' -ErrorAction SilentlyContinue)[0]
if (-not $p) { 'not running'; return }
'pid=' + $p.Id + ' hwnd=' + $p.MainWindowHandle
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { 'window not found'; return }
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
'buttons: ' + $btns.Count
foreach ($b in $btns) { if ($b.Current.Name) { Write-Host ('  [' + $b.Current.Name + '] enabled=' + $b.Current.IsEnabled) } }