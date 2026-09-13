# Kit Tray Icon Diagnostic Tool
Write-Host "=== Kit Tray Icon Diagnostic ===" -ForegroundColor Cyan

# Check if Kit is running
$kitProcess = Get-Process -Name "Kit" -ErrorAction SilentlyContinue
if ($kitProcess) {
    Write-Host "[OK] Kit.exe is running (PID: $($kitProcess.Id))" -ForegroundColor Green

    # Check window handles
    Add-Type @"
        using System;
        using System.Runtime.InteropServices;
        using System.Text;
        public class User32 {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

            [DllImport("user32.dll")]
            public static extern bool IsWindow(IntPtr hWnd);
        }
"@

    $trayWnd = [User32]::FindWindow("PToyTrayIconWindow", $null)
    if ($trayWnd -ne [IntPtr]::Zero) {
        Write-Host "[OK] Tray icon window exists (HWND: $trayWnd)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] Tray icon window not found" -ForegroundColor Red
    }
} else {
    Write-Host "[FAIL] Kit.exe is not running" -ForegroundColor Red
}

# Check settings file
$settingsPath = "$env:LOCALAPPDATA\Microsoft\PowerToys\settings.json"
if (Test-Path $settingsPath) {
    Write-Host "[OK] Settings file exists: $settingsPath" -ForegroundColor Green
    $settings = Get-Content $settingsPath | ConvertFrom-Json
    $showTrayIcon = $settings.general.show_system_tray_icon
    Write-Host "    show_system_tray_icon = $showTrayIcon" -ForegroundColor Yellow
} else {
    Write-Host "[INFO] Settings file not found (first run)" -ForegroundColor Yellow
}

# Check log file for startup timing
$logPath = "$env:LOCALAPPDATA\Microsoft\PowerToys\Kit\Logs\Kit-Runner.log"
if (Test-Path $logPath) {
    Write-Host "`n=== Latest Startup Timing ===" -ForegroundColor Cyan
    $timingLines = Select-String -Path $logPath -Pattern "STARTUP TIMING|took \d+ms" | Select-Object -Last 15
    if ($timingLines) {
        $timingLines | ForEach-Object { Write-Host $_.Line -ForegroundColor Gray }
    } else {
        Write-Host "No timing information found in log" -ForegroundColor Yellow
    }
} else {
    Write-Host "[INFO] Log file not found yet" -ForegroundColor Yellow
}

Write-Host "`n=== Recommendations ===" -ForegroundColor Cyan
Write-Host "1. Check Windows taskbar: Right-click taskbar > Taskbar settings > Select which icons appear" -ForegroundColor White
Write-Host "2. If icon is hidden, look in overflow area (^ arrow) near system tray" -ForegroundColor White
Write-Host "3. Run Kit.exe and check log file: $logPath" -ForegroundColor White
