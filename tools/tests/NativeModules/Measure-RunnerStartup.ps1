[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [ValidateRange(1, 10)]
    [int]$Runs = 3,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $repoRoot 'x64\Debug\Kit.exe' }
if (-not $ReportPath) { $ReportPath = Join-Path $repoRoot 'TestResults\FrameworkReview\runner-startup.json' }
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$runtimeDirectory = Split-Path -Parent $ExecutablePath
$runtimeBoundary = $runtimeDirectory + [IO.Path]::DirectorySeparatorChar
if ([IO.Path]::GetFileName($ExecutablePath) -ne 'Kit.exe' -or -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw 'Select an existing Kit.exe build.'
}
if (Get-Process -Name Kit,Kit.Settings,Kit.QuickAccess,Kit.Awake,Kit.LightSwitchService -ErrorAction SilentlyContinue) {
    throw 'Close existing Kit processes before measuring startup.'
}
$logDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kit\RunnerLogs'
$results = [Collections.Generic.List[object]]::new()
for ($run = 1; $run -le $Runs; $run++) {
    $start = [Diagnostics.ProcessStartInfo]::new($ExecutablePath)
    $start.WorkingDirectory = $runtimeDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add('--silent')
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $children = [Collections.Generic.List[Diagnostics.Process]]::new()
    try {
        $record = $null
        while ($elapsed.Elapsed.TotalSeconds -lt 25 -and -not $process.HasExited) {
            $log = Get-ChildItem -LiteralPath $logDirectory -Filter 'runner-log*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($log) {
                $lines = @(Get-Content -LiteralPath $log.FullName -Tail 160 | Where-Object { $_ -match "\[p-$($process.Id)\]" })
                $match = $lines | Select-String -Pattern 'Runner initialization since WinMain: (\d+)ms' | Select-Object -Last 1
                if ($match) {
                    $record = [ordered]@{
                        Run = $run
                        ProcessId = $process.Id
                        RunnerInitMilliseconds = [int]$match.Matches[0].Groups[1].Value
                        LaunchToLogMilliseconds = $elapsed.ElapsedMilliseconds
                        LoadedModules = @($lines | Select-String -Pattern 'STARTUP_TIMING: Module Loaded:').Count
                    }
                    break
                }
            }
            Start-Sleep -Milliseconds 50
        }
        if (-not $record) { throw 'Runner initialization did not complete within 25 seconds.' }
        foreach ($child in Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)") {
            if (-not $child.ExecutablePath -or -not $child.ExecutablePath.StartsWith($runtimeBoundary, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'A test child is outside the selected runtime directory.'
            }
            $ownedChild = [Diagnostics.Process]::GetProcessById([int]$child.ProcessId)
            $null = $ownedChild.Handle
            $children.Add($ownedChild)
        }
        if ($record.LoadedModules -ne 3) { throw 'The runner did not load all three module DLLs.' }
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
        $record.ChildrenExitedAfterRunnerTermination = $true
        foreach ($child in $children) {
            if (-not $child.WaitForExit(10000)) { $record.ChildrenExitedAfterRunnerTermination = $false }
        }
        $results.Add($record)
        if (-not $record.ChildrenExitedAfterRunnerTermination) { throw 'A worker survived the terminated test runner.' }
    }
    finally {
        # Cleanup is limited to process handles created by this run.
        foreach ($owned in @($process) + @($children.ToArray())) {
            if (-not $owned.HasExited) { $owned.Kill(); $owned.WaitForExit(5000) | Out-Null }
            $owned.Dispose()
        }
    }
}
[IO.Directory]::CreateDirectory((Split-Path -Parent $ReportPath)) | Out-Null
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath -Encoding utf8
$results | ForEach-Object { [pscustomobject]$_ }
