#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RuntimeRoot,
    [string]$ReportRoot,
    [ValidateSet('Both', 'QuickAccess', 'Settings')]
    [string]$SingleClickMode = 'Both'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot 'x64\Debug' }
if (-not $ReportRoot) { $ReportRoot = Join-Path $repoRoot ('TestResults\TrayClick\Debug\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
$ReportRoot = [IO.Path]::GetFullPath($ReportRoot)
$runtimeBoundary = $RuntimeRoot + '\'
if ($RuntimeRoot -notin @((Join-Path $repoRoot 'x64\Debug'), (Join-Path $repoRoot 'ARM64\Debug'))) {
    throw 'This test only accepts this checkout''s x64\Debug or ARM64\Debug runtime.'
}
foreach ($relative in @('Kit.exe', 'WinUI3Apps\Kit.Settings.exe', 'WinUI3Apps\Kit.QuickAccess.exe')) {
    $path = Join-Path $RuntimeRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing Debug executable: $path" }
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Redirected executable: $path" }
}
foreach ($path in @($RuntimeRoot, (Join-Path $RuntimeRoot 'WinUI3Apps'))) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Redirected runtime directory: $path" }
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this smoke test from a non-elevated PowerShell so Settings has the owned Runner as its parent.'
}
function Assert-NoKitProcesses {
    $existing = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -eq 'Kit' -or $_.ProcessName -like 'Kit.*' -or $_.ProcessName -match '^(AiHubUx|WingetPage|ModulePage|DashboardPage)Smoke$'
    })
    if ($existing.Count) { throw "Close existing Kit/UI/worker processes first: $($existing.ProcessName -join ', ')." }
}
Assert-NoKitProcesses

$configRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kit'
$generalPath = Join-Path $configRoot 'settings.json'
$general = if (Test-Path -LiteralPath $generalPath) { Get-Content -LiteralPath $generalPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
if ($general -isnot [Collections.IDictionary]) { throw 'General settings must contain a JSON object.' }
# Runner reconciles the logon task during startup. Refuse a fixture that could
# redirect an existing task to the Debug build; this test does not edit tasks.
if ($general['startup']) { throw 'This controlled test requires Kit startup to be disabled.' }
$scheduler = New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
try {
    $task = $scheduler.GetFolder('\Kit').GetTask('Autorun for ' + $env:USERNAME)
    if ($task) { throw 'An existing Kit logon task would be reconciled by Runner; leave it untouched and run without a registered startup task.' }
} catch {
    if ($_.Exception.GetBaseException().HResult -notin @(-2147024894, -2147024893)) { throw }
}

if (-not ('KitTrayClickProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class KitTrayClickProbe {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    public static void Send(IntPtr hwnd, uint owner, uint msg, int value) {
        GetWindowThreadProcessId(hwnd, out uint actual);
        if (hwnd == IntPtr.Zero || actual != owner) throw new InvalidOperationException("Tray window ownership changed.");
        if (SendMessageTimeout(hwnd, msg, IntPtr.Zero, new IntPtr(value), 2, 3000, out _) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned tray window did not process the message.");
    }
    public static void Click(IntPtr hwnd, uint owner, string kind) {
        Send(hwnd, owner, 0x8000, 0x0202); // WM_APP / WM_LBUTTONUP starts the real window timer.
        if (kind == "double") {
            Send(hwnd, owner, 0x8000, 0x0203); // WM_LBUTTONDBLCLK
            Send(hwnd, owner, 0x8000, 0x0202); // Final button-up must not schedule a second action.
        } else if (kind == "close") {
            Send(hwnd, owner, 0x0010, 0); // WM_CLOSE, before returning control to PowerShell.
        }
    }
    public static long[] VisibleWindows(uint owner) {
        var result = new List<long>();
        EnumWindows((hwnd, _) => {
            GetWindowThreadProcessId(hwnd, out uint actual);
            if (actual == owner && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out Rect rect)
                && rect.Right - rect.Left >= 100 && rect.Bottom - rect.Top >= 100) result.Add(hwnd.ToInt64());
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@
}

[IO.Directory]::CreateDirectory($ReportRoot) | Out-Null
$backups = @{}
$configNames = @('settings.json', 'settings-placement.json', 'language.json', 'log_settings.json', 'UpdateState.json',
    'Awake\settings.json', 'LightSwitch\settings.json', 'Localserver\settings.json', 'NetMap\settings.json',
    'UDPtest\settings.json', 'Winget\settings.json', 'AiHub\settings.json', 'AiHub\service-settings.json')
foreach ($name in $configNames) {
    $path = Join-Path $configRoot $name
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
    if ($null -ne $backups[$path]) {
        $backupPath = Join-Path $ReportRoot ('original-config\' + $name)
        [IO.Directory]::CreateDirectory((Split-Path -Parent $backupPath)) | Out-Null
        [IO.File]::WriteAllBytes($backupPath, $backups[$path])
    }
}
$result = [ordered]@{ Started = (Get-Date).ToString('o'); Configuration = 'Debug'; RuntimeRoot = $RuntimeRoot; Success = $false; Cases = @() }
$cleanupErrors = [Collections.Generic.List[string]]::new()
$failure = $null
$activeCase = $null

function Get-TrayWindow($case) {
    $hwnd = [KitTrayClickProbe]::FindWindow('KitTrayIconWindow', 'KitTrayIconWindow')
    [uint32]$windowPid = 0
    $null = [KitTrayClickProbe]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
    if ($windowPid -eq $case.Runner.Id) { return $hwnd }
    return [IntPtr]::Zero
}
function Read-CaseLog($case) {
    $logDirectory = Join-Path $configRoot 'RunnerLogs'
    if (-not (Test-Path -LiteralPath $logDirectory)) { return @() }
    $pidMarker = "[p-$($case.Runner.Id)]"
    $startPrefix = '[' + $case.Started.ToString('yyyy-MM-dd HH:mm:ss.ffffff', [Globalization.CultureInfo]::InvariantCulture) + ']'
    foreach ($file in Get-ChildItem -LiteralPath $logDirectory -Filter 'runner-log*.log' -File) {
        if ($file.LastWriteTime -lt $case.Started) { continue }
        foreach ($line in Get-Content -LiteralPath $file.FullName -Tail 3000) {
            if ($line.Contains($pidMarker) -and $line.Length -ge $startPrefix.Length -and
                [string]::CompareOrdinal($line.Substring(0, $startPrefix.Length), $startPrefix) -ge 0) { $line }
        }
    }
}
function Update-OwnedChildren($case) {
    foreach ($candidate in Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'Kit.*' }) {
        if ($case.Children.ContainsKey($candidate.Id)) { $candidate.Dispose(); continue }
        try {
            $row = Get-CimInstance Win32_Process -Filter "ProcessId=$($candidate.Id)" -ErrorAction Stop
            if (-not $row -or $row.ParentProcessId -ne $case.Runner.Id) { continue }
            $null = $candidate.Handle # Pin the process instance, not just its reusable PID.
            $path = $candidate.MainModule.FileName
            if (-not $path.StartsWith($runtimeBoundary, [StringComparison]::OrdinalIgnoreCase) -or $candidate.StartTime -lt $case.Started) {
                throw "Owned child failed the Debug path/start-time check: $path"
            }
            $case.Children.Add($candidate.Id, $candidate)
            $case.ChildPaths[$candidate.Id] = $path
            if ($candidate.ProcessName -notin @('Kit.Settings', 'Kit.QuickAccess')) { throw "Disabled module started a worker: $path" }
        } catch [ArgumentException] {
            # A short-lived process is still caught by the Runner launch/action log.
        } finally {
            if (-not $case.Children.ContainsKey($candidate.Id)) { $candidate.Dispose() }
        }
    }
    foreach ($child in $case.Children.Values) {
        if (-not $child.HasExited -and [KitTrayClickProbe]::VisibleWindows($child.Id).Count -gt 0) { $case.VisiblePids.Add($child.Id) | Out-Null }
    }
}
function Stop-OwnedCase($case) {
    if (-not $case -or -not $case.Runner) { return }
    try {
        Update-OwnedChildren $case
        if (-not $case.Runner.HasExited) {
            $hwnd = Get-TrayWindow $case
            if ($hwnd -ne [IntPtr]::Zero) { [KitTrayClickProbe]::Send($hwnd, $case.Runner.Id, 0x0010, 0) }
            if (-not $case.Runner.WaitForExit(15000)) { throw 'Owned Runner did not exit gracefully within 15 seconds.' }
        }
        Update-OwnedChildren $case
        foreach ($child in $case.Children.Values) {
            if (-not $child.WaitForExit(5000)) { throw "Owned child survived Runner shutdown: $($child.Id)" }
        }
        $case.Record.GracefulExit = $true
        $case.Record.RunnerExitCode = $case.Runner.ExitCode
    } catch {
        $cleanupErrors.Add($_.Exception.Message)
    } finally {
        # Handles were pinned at creation/discovery and their executable paths verified.
        # Never use taskkill /IM, Stop-Process by name, or recursive process-tree killing.
        foreach ($owned in @($case.Children.Values) + @($case.Runner)) {
            try {
                if (-not $owned.HasExited) {
                    $path = $owned.MainModule.FileName
                    if (-not $path.StartsWith($runtimeBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside Debug runtime: $path" }
                    $owned.Kill()
                    if (-not $owned.WaitForExit(5000)) { throw "Owned process did not terminate: $($owned.Id)" }
                }
            } catch { $cleanupErrors.Add($_.Exception.Message) }
        }
        $case.Record.Children = @($case.Children.Values | ForEach-Object {
            [ordered]@{ ProcessId = $_.Id; ExecutablePath = $case.ChildPaths[$_.Id]; VisibleWindowObserved = $case.VisiblePids.Contains($_.Id); Exited = $_.HasExited }
        })
        $lines = @(Read-CaseLog $case)
        [IO.File]::WriteAllLines((Join-Path $ReportRoot ($case.Record.Name + '.runner.log')), [string[]]$lines)
        foreach ($owned in @($case.Children.Values) + @($case.Runner)) { $owned.Dispose() }
    }
}

try {
    [IO.Directory]::CreateDirectory($configRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $configRoot 'log_settings.json'), '{"logLevel":"info"}', [Text.UTF8Encoding]::new($false))
    if ($general['enabled'] -isnot [Collections.IDictionary]) { $general['enabled'] = @{} }
    foreach ($module in @($general['enabled'].Keys) + @('Awake', 'LightSwitch', 'Localserver', 'UDPtest', 'NetMap', 'AIHub', 'Winget')) { $general['enabled'][$module] = $false }
    $general['run_elevated'] = $false
    $general['startup'] = $false
    $general['show_tray_icon'] = $true
    $general['show_whats_new_after_updates'] = $false
    $general['show_new_updates_toast_notification'] = $false
    $general['download_updates_automatically'] = $false
    $scenarios = @()
    if ($SingleClickMode -in @('Both', 'QuickAccess')) { $scenarios += @{ Name = 'single-quick-access'; QuickAccess = $true; Kind = 'single'; Expected = 'Kit.QuickAccess' } }
    if ($SingleClickMode -in @('Both', 'Settings')) { $scenarios += @{ Name = 'single-settings'; QuickAccess = $false; Kind = 'single'; Expected = 'Kit.Settings' } }
    $scenarios += @{ Name = 'double-settings-only'; QuickAccess = $true; Kind = 'double'; Expected = 'Kit.Settings' }
    $scenarios += @{ Name = 'pending-click-close'; QuickAccess = $true; Kind = 'close'; Expected = '' }
    foreach ($scenario in $scenarios) {
        Assert-NoKitProcesses
        $general['enable_quick_access'] = $scenario.QuickAccess
        [IO.File]::WriteAllText($generalPath, ($general | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
        $record = [ordered]@{ Name = $scenario.Name; EnableQuickAccess = $scenario.QuickAccess; Success = $false }
        $result.Cases += $record
        $activeCase = @{ Started = Get-Date; Runner = $null; Children = [Collections.Generic.Dictionary[int, Diagnostics.Process]]::new(); ChildPaths = @{}; VisiblePids = [Collections.Generic.HashSet[int]]::new(); Record = $record }
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $RuntimeRoot 'Kit.exe'))
        $start.WorkingDirectory = $RuntimeRoot
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.ArgumentList.Add('--silent')
        $activeCase.Runner = [Diagnostics.Process]::Start($start)
        $null = $activeCase.Runner.Handle
        if ($activeCase.Runner.MainModule.FileName -ne (Join-Path $RuntimeRoot 'Kit.exe')) { throw 'Unexpected Runner executable path.' }
        $record.RunnerPid = $activeCase.Runner.Id
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        do {
            if ($activeCase.Runner.HasExited) { throw "Runner exited before initialization: $($activeCase.Runner.ExitCode)" }
            $hwnd = Get-TrayWindow $activeCase
            $lines = @(Read-CaseLog $activeCase)
            if ($hwnd -ne [IntPtr]::Zero -and @($lines | Select-String 'Runner initialization since WinMain:').Count -eq 1) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($hwnd -eq [IntPtr]::Zero -or @($lines | Select-String 'Runner initialization since WinMain:').Count -ne 1) { throw 'Runner did not initialize its verified tray window within 25 seconds.' }
        Update-OwnedChildren $activeCase
        if ($activeCase.Children.Count) { throw 'Silent startup unexpectedly created a child before the click.' }
        $record.DoubleClickMilliseconds = [KitTrayClickProbe]::GetDoubleClickTime()
        $record.ClickSent = (Get-Date).ToString('o')
        $clickElapsed = [Diagnostics.Stopwatch]::StartNew()
        [KitTrayClickProbe]::Click($hwnd, $activeCase.Runner.Id, $scenario.Kind)
        $record.InjectionMilliseconds = $clickElapsed.ElapsedMilliseconds
        if ($scenario.Kind -in @('close', 'double') -and $record.InjectionMilliseconds -ge $record.DoubleClickMilliseconds) { throw 'Message injection exceeded the double-click interval; the scenario was not exercised reliably.' }
        $observationMilliseconds = [Math]::Max(1500, $record.DoubleClickMilliseconds * 2 + 300)
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        do {
            Update-OwnedChildren $activeCase
            $lines = @(Read-CaseLog $activeCase)
            $quickRequests = @($lines | Select-String -SimpleMatch 'QuickAccessHost::start() called').Count
            $settingsRequests = @($lines | Select-String -SimpleMatch 'open_settings_window: called with target=').Count
            if ($scenario.Kind -ne 'close' -and $activeCase.Runner.HasExited) { throw 'Runner exited during click handling.' }
            $ready = $scenario.Kind -eq 'close' -or $activeCase.VisiblePids.Count -gt 0
            if ($ready -and $clickElapsed.ElapsedMilliseconds -ge $observationMilliseconds) { break }
            Start-Sleep -Milliseconds 75
        } while ([DateTime]::UtcNow -lt $deadline)
        $record.ObservedMilliseconds = $clickElapsed.ElapsedMilliseconds
        $record.QuickAccessRequests = $quickRequests
        $record.SettingsRequests = $settingsRequests
        if ($scenario.Kind -eq 'close') {
            if (-not $activeCase.Runner.WaitForExit(10000)) { throw 'Closing with a pending click did not exit Runner.' }
            Update-OwnedChildren $activeCase
            $lines = @(Read-CaseLog $activeCase)
            $quickRequests = @($lines | Select-String -SimpleMatch 'QuickAccessHost::start() called').Count
            $settingsRequests = @($lines | Select-String -SimpleMatch 'open_settings_window: called with target=').Count
            $record.QuickAccessRequests = $quickRequests
            $record.SettingsRequests = $settingsRequests
            if ($quickRequests -ne 0 -or $settingsRequests -ne 0 -or $activeCase.Children.Count -ne 0) { throw 'A pending click launched a UI after close was requested.' }
        } else {
            $expectedQuick = [int]($scenario.Expected -eq 'Kit.QuickAccess')
            if ($quickRequests -ne $expectedQuick -or $settingsRequests -ne (1 - $expectedQuick)) { throw "Unexpected action count: QuickAccess=$quickRequests Settings=$settingsRequests." }
            if ($activeCase.Children.Count -ne 1) { throw "Expected exactly one owned UI process, observed $($activeCase.Children.Count)." }
            $child = @($activeCase.Children.Values)[0]
            if ($child.ProcessName -ne $scenario.Expected -or $child.HasExited -or -not $activeCase.VisiblePids.Contains($child.Id)) { throw 'The expected UI process did not show a window.' }
            if ($scenario.Kind -eq 'single') {
                $action = @($lines | Where-Object { $_.Contains('QuickAccessHost::start() called') -or $_.Contains('open_settings_window: called with target=') })[0]
                $actionTime = [DateTime]::ParseExact($action.Substring(1, 26), 'yyyy-MM-dd HH:mm:ss.ffffff', [Globalization.CultureInfo]::InvariantCulture)
                $record.ActionDelayMilliseconds = [Math]::Round(($actionTime - [DateTime]::Parse($record.ClickSent)).TotalMilliseconds)
                if ($record.ActionDelayMilliseconds -lt ($record.DoubleClickMilliseconds - 60)) { throw 'Single-click action bypassed the double-click detection delay.' }
            }
        }
        $record.Success = $true
        Stop-OwnedCase $activeCase
        $activeCase = $null
        if ($cleanupErrors.Count) { throw ($cleanupErrors -join '; ') }
    }
    $result.Success = $true
} catch {
    $failure = $_
    $result.Error = $_.Exception.Message
} finally {
    try { Stop-OwnedCase $activeCase } catch { $cleanupErrors.Add($_.Exception.Message) }
    foreach ($path in $backups.Keys) {
        try {
            if ($null -ne $backups[$path]) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
            elseif (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
            if ($null -ne $backups[$path]) {
                if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($backups[$path])) { throw "Configuration restore mismatch: $path" }
            } elseif (Test-Path -LiteralPath $path) { throw "Unexpected configuration remains: $path" }
        } catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    $result.ConfigurationRestored = $cleanupErrors.Count -eq 0
    $result.CleanupErrors = @($cleanupErrors.ToArray())
    if ($cleanupErrors.Count) { $result.Success = $false }
    $result.Completed = (Get-Date).ToString('o')
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ReportRoot 'tray-click-smoke.json') -Encoding utf8
}
$result | ConvertTo-Json -Depth 10
if ($failure) { throw $failure }
if ($cleanupErrors.Count) { throw ($cleanupErrors -join '; ') }
