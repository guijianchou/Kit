Get-Process -Name 'Kit*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$log = Join-Path $env:LOCALAPPDATA 'Kit\Settings\Logs\2.2.3.0\Log_2026-09-20.log'
if (Test-Path $log) { $before = (Get-Item $log).Length; 'log before = ' + $before } else { $before = 0; 'no log yet' }
$p = Start-Process 'C:\Users\Zen\Repos\Codings\Kit\x64\Debug\WinUI3Apps\Kit.Settings.exe' -PassThru
'pid=' + $p.Id
Start-Sleep -Seconds 16
$p.Refresh(); if ($p.HasExited) { 'EXITED ' + $p.ExitCode; return }
Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { 'no window'; return }
$nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'AI Hub')
$nav = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
if ($nav) { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); 'AI Hub selected'; Start-Sleep -Seconds 7 }
# Fast scan 是 HyperlinkButton/Button 皆可能，按名字查找后尝试 Invoke
$nc2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Fast scan')
$t = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc2)
if (-not $t) { 'Fast scan not found'; exit 1 }
'found Fast scan: type=' + $t.Current.ControlType.ProgrammaticName + ' enabled=' + $t.Current.IsEnabled
$ip = $null
if ($t.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) { $ip.Invoke(); 'invoked' } else { 'no invoke pattern' }
Start-Sleep -Seconds 25
'--- 新增日志 ---'
$len = (Get-Item $log).Length
$fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite'); $fs.Seek($before, 'Begin') | Out-Null; $sr = New-Object IO.StreamReader($fs); $sr.ReadToEnd(); $sr.Close(); $fs.Close()