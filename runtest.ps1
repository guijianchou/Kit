Get-Process -Name 'Kit*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
# 通过 runner 启动（真实入口，AI Hub 模块接口会加载）
$p = Start-Process 'C:\Users\Zen\Repos\Codings\Kit\x64\Debug\Kit.exe' -PassThru
'runner pid=' + $p.Id
Start-Sleep -Seconds 25
'--- 进程 ---'
Get-Process -Name 'Kit*' -ErrorAction SilentlyContinue | ForEach-Object { '  ' + $_.ProcessName + ' pid=' + $_.Id + ' hwnd=' + $_.MainWindowHandle }
Start-Sleep -Seconds 10
'--- 再查 ---'
Get-Process -Name 'Kit*' -ErrorAction SilentlyContinue | ForEach-Object { '  ' + $_.ProcessName + ' pid=' + $_.Id + ' hwnd=' + $_.MainWindowHandle }