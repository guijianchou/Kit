[CmdletBinding()]
param(
    [switch]$IncludeRealWorkerChecks,
    [switch]$RestoreSettingsOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$smokeRoot = Join-Path $repoRoot 'x64\Debug\tests\NativeModules'
$productOutput = [IO.Path]::GetFullPath((Join-Path $smokeRoot '..\..'))
$harnessPath = Join-Path $smokeRoot 'LightSwitchSmoke.exe'
$moduleFileName = 'Kit.LightSwitchModuleInterface.dll'
$sourceModulePath = Join-Path $productOutput $moduleFileName
$copiedModulePath = Join-Path $smokeRoot $moduleFileName
$fakeWorkerPath = Join-Path $smokeRoot 'LightSwitchService\Kit.LightSwitchService.exe'
$realWorkerPath = Join-Path $productOutput 'LightSwitchService\Kit.LightSwitchService.exe'
$moduleSettingsDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kit\LightSwitch'
$settingsPath = Join-Path $moduleSettingsDirectory 'settings.json'
$backupPath = Join-Path $smokeRoot 'LightSwitch.settings.original.bin'
$manifestPath = Join-Path $smokeRoot 'settings-restore.json'
$guardNames = @('Kit', 'Kit.Settings', 'Kit.QuickAccess', 'Kit.LightSwitchService')

function Assert-PlainSettingsPath {
    $kitDataDirectory = Split-Path -Parent $moduleSettingsDirectory
    foreach ($target in @($kitDataDirectory, $moduleSettingsDirectory, $settingsPath)) {
        if (Test-Path -LiteralPath $target) {
            $item = Get-Item -LiteralPath $target -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Refusing to replace Kit settings through a reparse point.'
            }
        }
    }
}

function Assert-AppsStopped {
    $running = @(Get-Process -Name $guardNames -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $names = ($running | ForEach-Object { "$($_.ProcessName) [$($_.Id)]" }) -join ', '
        throw "Smoke test refused: close these running processes first: $names"
    }
}

function Get-BytesHash([byte[]]$Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}

function Restore-OriginalSettings {
    Assert-PlainSettingsPath
    $metadata = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($metadata.TargetPath -ne $settingsPath) {
        throw 'Restore manifest target does not match the exact Kit LightSwitch settings path.'
    }
    if ($metadata.HadOriginal -eq $true) {
        $originalBytes = [IO.File]::ReadAllBytes($backupPath)
        if ((Get-BytesHash $originalBytes) -ne $metadata.OriginalSha256) {
            throw 'Original settings backup hash mismatch; backup files were retained.'
        }
        [IO.Directory]::CreateDirectory($moduleSettingsDirectory) | Out-Null
        [IO.File]::WriteAllBytes($settingsPath, $originalBytes)
        if ((Get-BytesHash ([IO.File]::ReadAllBytes($settingsPath))) -ne $metadata.OriginalSha256) {
            throw 'Restored settings hash mismatch; backup files were retained.'
        }
        [IO.File]::SetLastWriteTimeUtc($settingsPath, ([DateTime]$metadata.LastWriteTimeUtc).ToUniversalTime())
    }
    else {
        if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
            Remove-Item -LiteralPath $settingsPath
        }
        if (Test-Path -LiteralPath $settingsPath) {
            throw 'Could not restore the original absence of the settings file.'
        }
    }
    if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
        Remove-Item -LiteralPath $backupPath
    }
    Remove-Item -LiteralPath $manifestPath
}

function Invoke-NativeSmoke([string]$Mode, [string]$TargetPath) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $harnessPath
    $startInfo.WorkingDirectory = $smokeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['KIT_REVIEW_SMOKE_GUARDED'] = '1'
    $startInfo.ArgumentList.Add($Mode)
    $startInfo.ArgumentList.Add($TargetPath)
    $startInfo.ArgumentList.Add($productOutput)
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(45000)) {
            # Only the Process object created just above is terminated. The native
            # cleanup job owns its children; no process-name termination is used.
            $process.Kill()
            $process.WaitForExit(10000) | Out-Null
            throw 'Native smoke test timed out; its exact harness process was stopped.'
        }
        $output = $outputTask.GetAwaiter().GetResult()
        $errorOutput = $errorTask.GetAwaiter().GetResult()
        if ($output) { Write-Output $output.TrimEnd() }
        if ($errorOutput) { Write-Output $errorOutput.TrimEnd() }
        if ($process.ExitCode -ne 0) {
            throw "Native smoke test failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit(10000) | Out-Null
        }
        $process.Dispose()
    }
}

$mutex = [Threading.Mutex]::new($false, 'Local\KitReviewSmoke-LightSwitch-8a762c92-4cb0-4c65-bd2c-47d8c5d3be3f')
$ownsMutex = $false
try {
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another LightSwitch smoke test is already running.' }
    Assert-AppsStopped
    Assert-PlainSettingsPath

    if ($RestoreSettingsOnly) {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw 'No pending settings backup was found.'
        }
        Restore-OriginalSettings
        Write-Output 'Restored the pending original settings bytes.'
        return
    }

    if ((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $backupPath)) {
        throw 'A previous settings backup remains. Review it and run -RestoreSettingsOnly before another smoke test.'
    }
    foreach ($required in @($harnessPath, $fakeWorkerPath, $sourceModulePath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required build output is missing: $required"
        }
    }
    if ($IncludeRealWorkerChecks -and -not (Test-Path -LiteralPath $realWorkerPath -PathType Leaf)) {
        throw 'The real LightSwitchService build output is missing.'
    }
    Copy-Item -LiteralPath $sourceModulePath -Destination $copiedModulePath -Force
    if ((Get-FileHash -LiteralPath $sourceModulePath).Hash -ne (Get-FileHash -LiteralPath $copiedModulePath).Hash) {
        throw 'Copied module DLL hash mismatch.'
    }
    Assert-AppsStopped

    $hadOriginal = Test-Path -LiteralPath $settingsPath -PathType Leaf
    $originalSha256 = $null
    $lastWriteTimeUtc = $null
    if ($hadOriginal) {
        $originalBytes = [IO.File]::ReadAllBytes($settingsPath)
        $originalSha256 = Get-BytesHash $originalBytes
        $lastWriteTimeUtc = [IO.File]::GetLastWriteTimeUtc($settingsPath).ToString('O')
        [IO.File]::WriteAllBytes($backupPath, $originalBytes)
    }
    $metadata = [ordered]@{
        TargetPath = $settingsPath
        HadOriginal = $hadOriginal
        OriginalSha256 = $originalSha256
        LastWriteTimeUtc = $lastWriteTimeUtc
    }
    [IO.File]::WriteAllText($manifestPath, ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    try {
        $configuration = [ordered]@{
            name = 'LightSwitch'
            version = '1.0'
            properties = [ordered]@{
                changeSystem = @{ value = $false }
                changeApps = @{ value = $false }
                scheduleMode = @{ value = 'Off' }
                lightTime = @{ value = 480 }
                darkTime = @{ value = 1200 }
                sunrise_offset = @{ value = 0 }
                sunset_offset = @{ value = 0 }
                latitude = @{ value = '0.0' }
                longitude = @{ value = '0.0' }
                'toggle-theme-hotkey' = @{ value = @{ win = $true; ctrl = $true; shift = $true; alt = $false; code = 68 } }
            }
        }
        [IO.Directory]::CreateDirectory($moduleSettingsDirectory) | Out-Null
        [IO.File]::WriteAllText($settingsPath, ($configuration | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        Invoke-NativeSmoke '--lifecycle' $copiedModulePath
        if ($IncludeRealWorkerChecks) {
            Assert-AppsStopped
            Invoke-NativeSmoke '--real-worker' $realWorkerPath
        }
    }
    finally {
        # Restore after the harness exits (including assertion failures/timeouts).
        # Configuration contents are never printed. Backups survive restore errors.
        Restore-OriginalSettings
        Write-Output 'Original LightSwitch settings bytes restored and verified.'
    }
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
