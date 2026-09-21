Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes
$p = @(Get-Process -Name 'Kit.Settings' -ErrorAction SilentlyContinue)[0]
if (-not $p) { 'not running'; return }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Fast scan')
$t = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
if (-not $t) { 'Fast scan text not found'; return }
'start: type=' + $t.Current.ControlType.ProgrammaticName
$w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$node = $t
for ($i = 0; $i -lt 8; $i++) {
  $parent = $w.GetParent($node)
  if (-not $parent) { break }
  $ip = $null; $hasInvoke = $parent.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)
  Write-Host ('  up' + $i + ': type=' + $parent.Current.ControlType.ProgrammaticName + ' name="' + $parent.Current.Name + '" invoke=' + $hasInvoke + ' enabled=' + $parent.Current.IsEnabled + ' class=' + $parent.Current.ClassName)
  if ($hasInvoke) { $ip.Invoke(); Write-Host '  >>> INVOKED'; break }
  $node = $parent
}