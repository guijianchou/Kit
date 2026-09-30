<#
.SYNOPSIS
Summarizes Kit logs so startup cost, errors and crashes can be analyzed in one pass.

.DESCRIPTION
Read-only by default. Scans %LOCALAPPDATA%\Kit (and the LocalLow fallback used when that
location is not writable) and reports:
  - the most recent runner and Settings startup timelines (STARTUP_TIMING markers),
  - error / warning counts per log file and the most frequent error messages,
  - crash.log sessions, unhandled exceptions and the most frequent first-chance exceptions,
  - logger fallbacks (LOGGER_FALLBACK), i.e. processes that could not write their normal log.

With -Zip the scanned files are also copied into a zip archive for sharing.

.PARAMETER Days
Only consider log files modified within this many days. Default: 7.

.PARAMETER Zip
Also write a zip archive with the scanned logs and the summary.

.PARAMETER OutputPath
Where the zip is written. Default: %TEMP%\Kit-diagnostics-<timestamp>.zip.

.EXAMPLE
.\tools\diagnostics\Get-KitDiagnostics.ps1

.EXAMPLE
.\tools\diagnostics\Get-KitDiagnostics.ps1 -Days 2 -Zip
#>
[CmdletBinding()]
param(
    [int]$Days = 7,
    [switch]$Zip,
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$roots = @(
    (Join-Path $env:LOCALAPPDATA 'Kit'),
    (Join-Path $env:USERPROFILE 'AppData\LocalLow\Kit')
) | Where-Object { Test-Path -LiteralPath $_ }

$since = (Get-Date).AddDays(-$Days)
$report = [System.Collections.Generic.List[string]]::new()
function Out-Report([string]$line = '') { $report.Add($line); Write-Output $line }

$logFiles = @(foreach ($root in $roots) {
    Get-ChildItem -LiteralPath $root -Recurse -File -Include '*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $since -and $_.Name -notlike 'crash*.log' }
}) | Sort-Object LastWriteTime

Out-Report "Kit diagnostics  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  (last $Days days)"
Out-Report ("Roots: " + ($roots -join '; '))
Out-Report "Log files scanned: $($logFiles.Count)"
Out-Report

# --- Startup timelines -------------------------------------------------------------------
# Native lines: [2026-09-24 20:57:29.854347] [p-75376] [t-68012] [info] STARTUP_TIMING: Tray Icon at 46ms
# Managed lines: [23:54:59.9049202] [Info] [t-1] App.xaml.cs::LogStartupTiming::64
#                    STARTUP_TIMING: App constructed at 212ms
$nativeTiming = [regex]'^\[(?<ts>[^\]]+)\] \[p-(?<pid>\d+)\] \[t-\d+\] \[\w+\] STARTUP_TIMING: (?<msg>.+)$'
$runnerFiles = $logFiles | Where-Object { $_.FullName -match '\\RunnerLogs\\' }
$runnerSessions = @{}
foreach ($file in $runnerFiles) {
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        $m = $nativeTiming.Match($line)
        if ($m.Success) {
            $key = "$($file.Name)|$($m.Groups['pid'].Value)"
            if (-not $runnerSessions.ContainsKey($key)) { $runnerSessions[$key] = [System.Collections.Generic.List[string]]::new() }
            $runnerSessions[$key].Add("$($m.Groups['ts'].Value)  $($m.Groups['msg'].Value)")
        }
    }
}
Out-Report '== Runner startup (latest session) =='
if ($runnerSessions.Count -gt 0) {
    $latest = $runnerSessions.GetEnumerator() | Sort-Object { $_.Value[0] } | Select-Object -Last 1
    $latest.Value | ForEach-Object { Out-Report "  $_" }
    $totals = $runnerSessions.Values | ForEach-Object {
        $end = $_ | Where-Object { $_ -match 'Runner initialization since WinMain: (\d+)ms' } | Select-Object -Last 1
        if ($end -match 'WinMain: (\d+)ms') { [int]$Matches[1] }
    }
    if ($totals) {
        $stats = $totals | Measure-Object -Minimum -Maximum -Average
        Out-Report ("  Runner init over {0} sessions: min {1}ms / avg {2:N0}ms / max {3}ms" -f $stats.Count, $stats.Minimum, $stats.Average, $stats.Maximum)
    }
} else {
    Out-Report '  (no STARTUP_TIMING markers found)'
}
Out-Report

$settingsFiles = $logFiles | Where-Object { $_.FullName -match '\\Settings\\Logs\\' }
Out-Report '== Settings startup (latest session) =='
$latestSettings = $null
foreach ($file in ($settingsFiles | Sort-Object LastWriteTime -Descending)) {
    $lines = [IO.File]::ReadAllLines($file.FullName)
    $marks = for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'STARTUP_TIMING: (?<msg>.+)$') { $Matches['msg'] }
    }
    if ($marks) {
        # Each launch starts with "App constructed"; keep the last launch in the newest file.
        $start = [Array]::LastIndexOf([string[]]$marks, ($marks | Where-Object { $_ -like 'App constructed*' } | Select-Object -Last 1))
        $latestSettings = $marks[[Math]::Max(0, $start)..($marks.Count - 1)]
        break
    }
}
if ($latestSettings) { $latestSettings | ForEach-Object { Out-Report "  $_" } } else { Out-Report '  (no STARTUP_TIMING markers found)' }
Out-Report

# --- Errors and warnings -------------------------------------------------------------------
Out-Report '== Errors / warnings per log file =='
$errorMessages = @{}
$fallbacks = [System.Collections.Generic.List[string]]::new()
foreach ($file in $logFiles) {
    $lines = [IO.File]::ReadAllLines($file.FullName)
    $errors = 0; $warnings = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $isError = $line -match '\] \[(error|err|critical)\] ' -or $line -match '^\[[^\]]+\] \[Error\]'
        $isWarning = $line -match '\] \[warning\] ' -or $line -match '^\[[^\]]+\] \[Warning\]'
        if ($line -match 'LOGGER_FALLBACK') { $fallbacks.Add("$($file.Name): $line") }
        if (-not ($isError -or $isWarning)) { continue }
        if ($isError) { $errors++ } else { $warnings++ }
        if ($isError) {
            # Managed entries put the message on the next (indented) line.
            $text = if ($line -match '^\[[^\]]+\] \[Error\]' -and $i + 1 -lt $lines.Count) { $lines[$i + 1].Trim() } else { ($line -replace '^(\[[^\]]*\] )+', '') }
            $normalized = ($text -replace '\d+', '#')
            if ($normalized.Length -gt 160) { $normalized = $normalized.Substring(0, 160) }
            $errorMessages[$normalized] = 1 + [int]$errorMessages[$normalized]
        }
    }
    if ($errors -or $warnings) {
        $relative = $file.FullName
        foreach ($root in $roots) { $relative = $relative.Replace($root + '\', '') }
        Out-Report ("  {0,5} errors {1,5} warnings  {2}" -f $errors, $warnings, $relative)
    }
}
Out-Report
Out-Report '== Most frequent error messages =='
$errorMessages.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 15 | ForEach-Object {
    Out-Report ("  x{0,-5} {1}" -f $_.Value, $_.Key)
}
if ($errorMessages.Count -eq 0) { Out-Report '  (none)' }
Out-Report
Out-Report '== Logger fallbacks =='
if ($fallbacks.Count) { $fallbacks | Select-Object -Last 10 | ForEach-Object { Out-Report "  $_" } } else { Out-Report '  (none)' }
Out-Report

# --- crash.log -----------------------------------------------------------------------------
$crashFiles = foreach ($root in $roots) { Get-ChildItem -LiteralPath $root -Filter 'crash*.log' -File -ErrorAction SilentlyContinue }
Out-Report '== crash.log =='
if ($crashFiles) {
    $crashText = ($crashFiles | Sort-Object Name -Descending | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    $sessions = ([regex]::Matches($crashText, '^\[Session ', 'Multiline')).Count
    $unhandled = [regex]::Matches($crashText, '^\[Unhandled (?<ts>[^\]]+)\] (?<msg>.+)$', 'Multiline')
    Out-Report "  Sessions: $sessions   Unhandled: $($unhandled.Count)"
    $unhandled | Select-Object -Last 10 | ForEach-Object { Out-Report "  [Unhandled $($_.Groups['ts'].Value)] $($_.Groups['msg'].Value)" }

    $firstChance = @{}
    foreach ($m in [regex]::Matches($crashText, '^\[FirstChance [^\]]+\] (?<sig>[^\r\n]+)', 'Multiline')) {
        $sig = $m.Groups['sig'].Value
        $firstChance[$sig] = 1 + [int]$firstChance[$sig]
    }
    foreach ($m in [regex]::Matches($crashText, '^\[FirstChanceSummary [^\]]+\] x(?<n>\d+) (?<type>[^|]+)\|(?<msg>[^|]*)\|', 'Multiline')) {
        $sig = "$($m.Groups['type'].Value): $($m.Groups['msg'].Value)"
        $firstChance[$sig] = [int]$m.Groups['n'].Value - 1 + [int]$firstChance[$sig]
    }
    Out-Report '  Most frequent first-chance exceptions:'
    $firstChance.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 10 | ForEach-Object {
        $sig = if ($_.Key.Length -gt 150) { $_.Key.Substring(0, 150) } else { $_.Key }
        Out-Report ("    x{0,-5} {1}" -f $_.Value, $sig)
    }
} else {
    Out-Report '  (no crash.log)'
}

if ($Zip) {
    if (-not $OutputPath) {
        $OutputPath = Join-Path $env:TEMP ("Kit-diagnostics-{0}.zip" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    $staging = Join-Path $env:TEMP ("Kit-diagnostics-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        foreach ($file in @($logFiles) + @($crashFiles)) {
            if (-not $file) { continue }
            $relative = $file.FullName
            foreach ($root in $roots) { $relative = $relative.Replace($root + '\', ((Split-Path $root -Parent | Split-Path -Leaf) + '\')) }
            $target = Join-Path $staging $relative
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
        [IO.File]::WriteAllLines((Join-Path $staging 'summary.txt'), $report)
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $OutputPath -Force
        Write-Output ''
        Write-Output "Diagnostics archive: $OutputPath"
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
