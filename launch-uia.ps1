Get-Process -Name 'Kit*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$exe = 'C:\Users\Zen\Repos\Codings\Kit\x64\Debug\WinUI3Apps\Kit.Settings.exe'
$p = Start-Process -FilePath $exe -PassThru
'started pid=' + $p.Id
Start-Sleep -Seconds 14
$p.Refresh()
if ($p.HasExited) { 'EXITED code=' + $p.ExitCode; return }
'running, title=' + $p.MainWindowTitle

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { 'window not found'; return }

# 点击 AI Hub 导航项
$nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'AI Hub')
$nav = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
if ($nav) {
  $sel = $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $sel.Select()
  'selected AI Hub'
  Start-Sleep -Seconds 5
} else { 'AI Hub nav not found' }

Write-Host '=== 按钮列表 ==='
$bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)
foreach ($b in $btns) { if ($b.Current.Name) { Write-Host ('  [' + $b.Current.Name + '] enabled=' + $b.Current.IsEnabled) } }