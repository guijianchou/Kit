#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$RuntimeRoot,
    [ValidateRange(100, 5000)]
    [int]$MaximumEnableMilliseconds = 500,
    [ValidateRange(30, 180)]
    [int]$TimeoutSeconds = 60,
    [string]$VisualStudioPath,
    [switch]$SkipBuild,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration" }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\')
$modulePath = Join-Path $RuntimeRoot 'Kit.WingetModuleInterface.dll'
$servicePath = Join-Path $RuntimeRoot 'Kit.WingetService.exe'

$outputRoot = Join-Path $repoRoot 'TestResults\WingetNative'
$runRoot = Join-Path $outputRoot ([guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $outputRoot 'WingetNativeSmoke.exe'
$reportPath = Join-Path $runRoot 'native-lifecycle.log'
$ownershipPath = Join-Path $runRoot 'owned-processes.tsv'

function Assert-NoReparseAncestor([string]$path) {
    for ($directory = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($path)); $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "The test refuses a reparse-point path: $($directory.FullName)"
        }
    }
}

function Assert-Below([string]$path, [string]$root) {
    $fullPath = [IO.Path]::GetFullPath($path)
    $fullRoot = [IO.Path]::GetFullPath($root).TrimEnd('\')
    if (-not $fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "A test filesystem target escaped its boundary: $fullPath"
    }
    Assert-NoReparseAncestor ([IO.Path]::GetDirectoryName($fullPath))
    if ([IO.File]::Exists($fullPath) -or [IO.Directory]::Exists($fullPath)) {
        if ([IO.File]::GetAttributes($fullPath) -band [IO.FileAttributes]::ReparsePoint) {
            throw "The test refuses a reparse-point target: $fullPath"
        }
    }
}

function Get-SafeStateTree([string]$directory) {
    Assert-Below $directory $dataRoot
    if (-not [IO.Directory]::Exists($directory)) { return }
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
        Assert-Below $path $stateRoot
        $attributes = [IO.File]::GetAttributes($path)
        $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
        [pscustomobject]@{ Path = $path; IsDirectory = $isDirectory }
        if ($isDirectory) { Get-SafeStateTree $path }
    }
}

function Clear-IsolatedState {
    if (-not [IO.Directory]::Exists($stateRoot)) { return }
    $entries = @(Get-SafeStateTree $stateRoot)
    foreach ($entry in $entries | Where-Object { -not $_.IsDirectory }) {
        Assert-Below $entry.Path $stateRoot
        [IO.File]::Delete($entry.Path)
    }
    foreach ($entry in $entries | Where-Object IsDirectory | Sort-Object { $_.Path.Length } -Descending) {
        Assert-Below $entry.Path $stateRoot
        [IO.Directory]::Delete($entry.Path, $false)
    }
}

function Write-Result([string]$message) {
    [IO.File]::AppendAllText($reportPath, $message + [Environment]::NewLine)
    Write-Output $message
}

Assert-NoReparseAncestor $RuntimeRoot
Assert-NoReparseAncestor $runRoot
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
if (-not $SkipBuild) {
    Push-Location -LiteralPath $repoRoot
    try {
        if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue) -or $env:VSCMD_ARG_TGT_ARCH -notin @('x64', 'amd64')) {
            $installation = $VisualStudioPath
            if (-not $installation) {
                $vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer\vswhere.exe'
                $installation = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
                if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'Visual Studio C++ tools are required; use -VisualStudioPath if the installation is not registered.' }
            }
            try {
                Import-Module (Join-Path $installation 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll') -DisableNameChecking
                Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments '-arch=amd64 -host_arch=amd64'
            } catch {
                $vsDevCmd = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
                $devStart = [Diagnostics.ProcessStartInfo]::new('cmd.exe')
                $devStart.Arguments = '/d /s /c ""' + $vsDevCmd + '" -arch=amd64 -host_arch=amd64 >nul && set"'
                $devStart.UseShellExecute = $false
                $devStart.CreateNoWindow = $true
                $devStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
                $devStart.RedirectStandardOutput = $true
                $devProcess = [Diagnostics.Process]::Start($devStart)
                try {
                    $developerEnvironment = $devProcess.StandardOutput.ReadToEnd() -split '\r?\n'
                    $devProcess.WaitForExit()
                    if ($devProcess.ExitCode -ne 0) { throw 'Could not initialize the Visual Studio compiler environment.' }
                    foreach ($line in $developerEnvironment) {
                        if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
                    }
                } finally { $devProcess.Dispose() }
            }
            if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'The Visual Studio environment did not expose cl.exe.' }
        }
        $compilerArguments = @(
            '/nologo', '/std:c++20', '/EHsc', '/W4', '/MT', '/utf-8', '/DUNICODE', '/D_UNICODE',
            "/I$(Join-Path $repoRoot 'src')", "/I$(Join-Path $repoRoot 'src\modules')",
            (Join-Path $PSScriptRoot 'WingetNativeSmoke.cpp'),
            "/Fo$(Join-Path $outputRoot 'WingetNativeSmoke.obj')", "/Fe$harnessPath",
            '/link', '/SUBSYSTEM:CONSOLE', 'Ole32.lib', 'Advapi32.lib'
        )
        & cl.exe @compilerArguments
        if ($LASTEXITCODE -ne 0) { throw 'The real-DLL lifecycle harness did not compile.' }
    } finally { Pop-Location }
}
if (-not (Test-Path -LiteralPath $harnessPath -PathType Leaf)) { throw 'The native lifecycle harness is missing.' }
if ($BuildOnly) {
    Write-Output "PASS native lifecycle harness compilation: $harnessPath"
    return
}
foreach ($path in @($modulePath, $servicePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Build the actual Winget module and service first: $path" }
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $RuntimeRoot) { throw 'The selected runtime boundary is invalid.' }
}
if (@(Get-Process -Name Kit,Kit.Settings,Kit.QuickAccess,Kit.WingetService -ErrorAction SilentlyContinue).Count) {
    throw 'Close existing Kit processes and the Winget service before this isolated lifecycle test.'
}

$dataRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kit\Winget'))
$settingsPath = Join-Path $dataRoot 'settings.json'
$stateRoot = Join-Path $dataRoot 'State'
Assert-NoReparseAncestor $dataRoot
Assert-Below $settingsPath $dataRoot
Assert-Below $stateRoot $dataRoot
if ([IO.Directory]::Exists($settingsPath) -or [IO.File]::Exists($stateRoot)) { throw 'Winget settings/State have unexpected filesystem types.' }
$settingsExisted = [IO.File]::Exists($settingsPath)
$stateExisted = [IO.Directory]::Exists($stateRoot)
$settingsBytes = $null
if ($settingsExisted) { $settingsBytes = [IO.File]::ReadAllBytes($settingsPath) }
$originalFiles = [Collections.Generic.List[object]]::new()
$originalDirectories = [Collections.Generic.List[string]]::new()
foreach ($entry in @(Get-SafeStateTree $stateRoot)) {
    if ($entry.IsDirectory) { $originalDirectories.Add($entry.Path) }
    else {
        $originalFiles.Add([pscustomobject]@{
            Path = $entry.Path
            Bytes = [IO.File]::ReadAllBytes($entry.Path)
            LastWriteTimeUtc = [IO.File]::GetLastWriteTimeUtc($entry.Path)
        })
    }
}

# Keep a disk backup as well as memory, so an interrupted PowerShell process remains recoverable.
$backupRoot = Join-Path $runRoot 'original-data'
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
if ($settingsExisted) { [IO.File]::WriteAllBytes((Join-Path $backupRoot 'settings.json'), $settingsBytes) }
foreach ($file in $originalFiles) {
    $relative = [IO.Path]::GetRelativePath($dataRoot, $file.Path)
    $backup = Join-Path $backupRoot $relative
    Assert-Below $backup $backupRoot
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
    [IO.File]::WriteAllBytes($backup, $file.Bytes)
}
@{ settingsExisted = $settingsExisted; stateExisted = $stateExisted; originalDirectories = @($originalDirectories); dataRoot = $dataRoot } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backupRoot 'restore-manifest.json') -Encoding utf8

$harness = $null
$ownedChildren = [Collections.Generic.Dictionary[int, Diagnostics.Process]]::new()
$stdout = $null
$stderr = $null
$smokeCompleted = $false
$testDataChanged = $false

function Capture-OwnedChildren {
    if (-not $harness) { return }
    # The held harness handle prevents PID reuse while parent-PID matching is used.
    foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($harness.Id)")) {
        if ($child.Name -ne 'Kit.WingetService.exe') { continue }
        $childId = [int]$child.ProcessId
        if ($ownedChildren.ContainsKey($childId)) { continue }
        if (-not $child.ExecutablePath -or -not [string]::Equals([IO.Path]::GetFullPath($child.ExecutablePath), $servicePath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'An apparent child service has an unexpected executable path; it will not be terminated by this test.'
        }
        try { $process = [Diagnostics.Process]::GetProcessById($childId) } catch [ArgumentException] { continue }
        try {
            $null = $process.Handle
            if ($process.HasExited) { $process.Dispose(); continue }
            if (-not [string]::Equals($process.MainModule.FileName, $servicePath, [StringComparison]::OrdinalIgnoreCase) -or
                $process.StartTime.ToUniversalTime() -lt $harness.StartTime.ToUniversalTime()) {
                throw 'A child process failed its exact-image/creation-time ownership check.'
            }
            $ownedChildren.Add($childId, $process)
        } catch {
            if ($process.HasExited) { $process.Dispose(); continue }
            $process.Dispose()
            throw
        }
    }
}

try {
    # Recheck after compilation and backup; never share mutable test settings with a live user session.
    if (@(Get-Process -Name Kit,Kit.Settings,Kit.QuickAccess,Kit.WingetService -ErrorAction SilentlyContinue).Count) {
        throw 'Kit or its service started before the smoke setup. Close it and retry.'
    }
    $testDataChanged = $true
    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    [IO.File]::WriteAllText($settingsPath, '{"name":"Winget","version":"1.0","properties":{"daily_check_enabled":{"value":false},"notify_updates":{"value":false}}}', [Text.UTF8Encoding]::new($false))
    Clear-IsolatedState
    [IO.Directory]::CreateDirectory($stateRoot) | Out-Null
    Write-Result "Loading actual module: $modulePath"
    Write-Result 'Daily checks and notifications are disabled; prior State is isolated. No download/install requests are sent.'
    $start = [Diagnostics.ProcessStartInfo]::new($harnessPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $RuntimeRoot
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($modulePath, $ownershipPath, [string]$MaximumEnableMilliseconds)) { $start.ArgumentList.Add($argument) }
    $harness = [Diagnostics.Process]::Start($start)
    $null = $harness.Handle
    $stdout = $harness.StandardOutput.ReadToEndAsync()
    $stderr = $harness.StandardError.ReadToEndAsync()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $harness.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Capture-OwnedChildren
        Start-Sleep -Milliseconds 100
    }
    Capture-OwnedChildren
    if (-not $harness.HasExited) { throw 'The native lifecycle harness exceeded its deadline.' }
    $output = $stdout.GetAwaiter().GetResult()
    $errors = $stderr.GetAwaiter().GetResult()
    if ($output) { Write-Result $output.TrimEnd() }
    if ($errors) { Write-Result $errors.TrimEnd() }
    if ($harness.ExitCode -ne 0) { throw "The actual module lifecycle smoke failed with exit code $($harness.ExitCode)." }
    foreach ($child in $ownedChildren.Values) {
        if (-not $child.WaitForExit(1000)) { throw 'An owned service survived the native lifecycle smoke.' }
    }
    if ($output -notmatch 'PASS actual_native_module_lifecycle') { throw 'The native harness did not produce its completion assertion.' }
    $smokeCompleted = $true
} finally {
    # Stop only the held harness and exact-image child handles belonging to this invocation.
    if ($harness) {
        Capture-OwnedChildren
        if (-not $harness.HasExited) { $harness.Kill(); $null = $harness.WaitForExit(5000) }
        Capture-OwnedChildren
    }
    foreach ($child in $ownedChildren.Values) {
        if (-not $child.HasExited -and -not $child.WaitForExit(8000)) { $child.Kill(); $null = $child.WaitForExit(5000) }
        if (-not $child.HasExited) { throw 'An owned child is still running; restoring mutable settings would be unsafe.' }
        $child.Dispose()
    }
    if ($harness) { $harness.Dispose() }
    if ($testDataChanged) {
        Clear-IsolatedState
        foreach ($directory in $originalDirectories | Sort-Object Length) {
            Assert-Below $directory $stateRoot
            [IO.Directory]::CreateDirectory($directory) | Out-Null
        }
        foreach ($file in $originalFiles) {
            Assert-Below $file.Path $stateRoot
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.Path)) | Out-Null
            [IO.File]::WriteAllBytes($file.Path, $file.Bytes)
            [IO.File]::SetLastWriteTimeUtc($file.Path, $file.LastWriteTimeUtc)
        }
        if (-not $stateExisted -and [IO.Directory]::Exists($stateRoot)) { [IO.Directory]::Delete($stateRoot, $false) }
        if ($settingsExisted) { [IO.File]::WriteAllBytes($settingsPath, $settingsBytes) }
        elseif ([IO.File]::Exists($settingsPath)) { [IO.File]::Delete($settingsPath) }

        if ([IO.File]::Exists($settingsPath) -ne $settingsExisted -or [IO.Directory]::Exists($stateRoot) -ne $stateExisted) {
            throw 'Original Winget settings/State existence was not restored.'
        }
        if ($settingsExisted -and [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($settingsPath))) -ne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$settingsBytes))) {
            throw 'Winget settings byte restoration failed.'
        }
        $restoredFiles = @(Get-SafeStateTree $stateRoot | Where-Object { -not $_.IsDirectory })
        if ($restoredFiles.Count -ne $originalFiles.Count) { throw 'The State file set was not restored.' }
        foreach ($file in $originalFiles) {
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($file.Path))) -ne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$file.Bytes))) {
                throw "State byte restoration failed: $($file.Path)"
            }
        }
        Write-Result 'RESTORE verified: settings and all State files match their original bytes/existence.'
    }
    Write-Output "Report: $reportPath"
}
if ($smokeCompleted) { Write-Result 'PASS native lifecycle and data restoration' }
