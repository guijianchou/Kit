Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p = @(Get-Process -Name 'Kit.Settings' -ErrorAction SilentlyContinue)[0]
if (-not $p) { 'not running'; return }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
'=== 当前页全部 Text 元素（前 60） ==='
$tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$txts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)
$n = 0
foreach ($t in $txts) { if ($t.Current.Name -and -not $t.Current.IsOffscreen) { Write-Host ('  ' + $t.Current.Name.Substring(0,[Math]::Min(100,$t.Current.Name.Length))); $n++; if ($n -ge 60) { break } } }