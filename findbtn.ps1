Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p = @(Get-Process -Name 'Kit.Settings' -ErrorAction SilentlyContinue)[0]
if (-not $p) { 'not running'; return }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

# 找所有名字含 Fast scan 的元素，报告其控件类型
$nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Fast scan')
$hits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nc)
'Fast scan 元素数: ' + $hits.Count
foreach ($h in $hits) {
  $ct = $h.Current.ControlType.ProgrammaticName
  $pats = @()
  foreach ($pn in @('InvokePattern','SelectionItemPattern','TogglePattern','ExpandCollapsePattern')) {
    $o = $null; if ($h.TryGetCurrentPattern([System.Windows.Automation.AutomationPattern]::LookupById([System.Windows.Automation.AutomationPattern]::LookupById(0))) ) {}
  }
  $canInvoke = $h.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$null)
  Write-Host ('  type=' + $ct + ' enabled=' + $h.Current.IsEnabled + ' invoke=' + $canInvoke + ' offscreen=' + $h.Current.IsOffscreen + ' class=' + $h.Current.ClassName)
}

# Enable AI Hub 的状态
$ec = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Enable AI Hub')
$et = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $ec)
if ($et) { $tp=$null; $state='n/a'; if ($et.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern,[ref]$tp)) { $state = $tp.Current.ToggleState }; 'Enable AI Hub: type=' + $et.Current.ControlType.ProgrammaticName + ' toggle=' + $state + ' enabled=' + $et.Current.IsEnabled }