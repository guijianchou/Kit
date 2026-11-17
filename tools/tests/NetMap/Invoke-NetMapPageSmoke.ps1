param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('en-US', 'zh-CN')]
    [string]$Language = 'en-US',
    [switch]$LifecycleOnly,
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps" }
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N')
$reportRoot = Join-Path $repoRoot "TestResults\NetMap\$Language\$runId"
$harnessLog = Join-Path $reportRoot 'page-smoke.log'
$testName = 'NetMapPageSmoke'
$temporaryFiles = @('.dll', '.exe', '.deps.json', '.runtimeconfig.json', '.pri') | ForEach-Object { Join-Path $runtimeRoot ($testName + $_) }
if (@(Get-Process Kit,Kit.Settings,NetMapPageSmoke -ErrorAction SilentlyContinue).Count) {
    throw 'Close Kit before running the page smoke test.'
}
foreach ($path in $temporaryFiles) {
    if (Test-Path -LiteralPath $path) { throw "Temporary test file already exists: $path" }
}
if ((Get-Item -LiteralPath $runtimeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing to use a redirected runtime directory.'
}
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
Write-Host "NetMap smoke report directory: $reportRoot"
$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
# HostModel is loaded inside PowerShell (.NET 10), so a globally selected .NET 11
# SDK cannot supply its apphost writer even though its compiler runs separately.
$net10Sdk = & dotnet --list-sdks | ForEach-Object {
    if ($_ -match '^(10\.0\.\d+) \[(.+)\]$') {
        [pscustomobject]@{ Version = [version]$Matches[1]; Root = Join-Path $Matches[2] $Matches[1] }
    }
} | Sort-Object Version -Descending | Select-Object -First 1
if (-not $net10Sdk) { throw 'An installed .NET 10 SDK is required for the UI smoke apphost writer.' }
$sdkRoot = $net10Sdk.Root
Write-Host "UI smoke SDK: $($net10Sdk.Version); PowerShell runtime: $([Environment]::Version)"
$hostPack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $hostPack) { throw '.NET 10 x64 apphost pack is required.' }
$backups = @{}
foreach ($name in @('settings.json', 'settings-placement.json', 'log_settings.json', 'language.json', 'NetMap\mtr-latest.json')) {
    $path = Join-Path $env:LOCALAPPDATA "Kit\$name"
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
$previousOutput = $env:KIT_NETMAP_TEST_OUTPUT
$previousLifecycleOnly = $env:KIT_NETMAP_TEST_LIFECYCLE_ONLY
$testProcess = $null
try {
    [IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA 'Kit\language.json'), ('{"language":"' + $Language + '"}'))
    $compilerArgs = [Collections.Generic.List[string]]::new()
    foreach ($arg in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($arg) }
    $compilerArgs.Add('/out:"' + (Join-Path $runtimeRoot "$testName.dll") + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'NetMapPageSmoke.cs') + '"')
    foreach ($file in Get-ChildItem -LiteralPath $runtimeRoot -Filter '*.dll' -File) {
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $compilerArgs.Add('/reference:"' + $file.FullName + '"')
    }
    $responseFile = Join-Path $reportRoot 'netmap-page-csc.rsp'
    [IO.File]::WriteAllLines($responseFile, $compilerArgs)
    & dotnet (Join-Path $sdkRoot 'Roslyn\bincore\csc.dll') ('@' + $responseFile)
    if ($LASTEXITCODE -ne 0) { throw 'Page smoke test compilation failed.' }
    [Reflection.Assembly]::LoadFrom((Join-Path $sdkRoot 'Microsoft.NET.HostModel.dll')) | Out-Null
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost(
        (Join-Path $hostPack.FullName 'runtimes\win-x64\native\apphost.exe'),
        (Join-Path $runtimeRoot "$testName.exe"), "$testName.dll", $true,
        (Join-Path $runtimeRoot 'Kit.Settings.exe'), $false, $false, $null)
    # Match KitSetExecutableIntegrity for this manually generated apphost only.
    & icacls.exe (Join-Path $runtimeRoot "$testName.exe") /setintegritylevel M /L | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the temporary smoke executable to medium integrity.' }
    foreach ($extension in @('.deps.json', '.runtimeconfig.json', '.pri')) {
        Copy-Item -LiteralPath (Join-Path $runtimeRoot ('Kit.Settings' + $extension)) -Destination (Join-Path $runtimeRoot ($testName + $extension))
    }
    $env:KIT_NETMAP_TEST_OUTPUT = $reportRoot
    $env:KIT_NETMAP_TEST_LIFECYCLE_ONLY = if ($LifecycleOnly) { '1' } else { $null }
    # Release Settings requires the Runner launch contract; use private, unused pipes
    # because the harness supplies its own in-memory replies after startup.
    $pipeSuffix = [guid]::NewGuid().ToString('N')
    $launchArguments = @("\\.\pipe\KitPageSmokeRunner-$pipeSuffix", "\\.\pipe\KitPageSmokeSettings-$pipeSuffix", "$PID", 'system', 'false', 'false', 'false')
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $runtimeRoot "$testName.exe"))
    $startInfo.WorkingDirectory = $runtimeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $launchArguments) { $startInfo.ArgumentList.Add($argument) }
    Write-Host 'Starting the NetMap smoke process directly.'
    $testProcess = [Diagnostics.Process]::Start($startInfo)
    Write-Host "NetMap smoke process: $($testProcess.Id)"
    if (-not $testProcess.WaitForExit(45000)) {
        Write-Host 'Page smoke test timed out after 45s; harness log tail:'
        if (Test-Path -LiteralPath $harnessLog) { Get-Content -LiteralPath $harnessLog -Tail 5 }
        throw 'Page smoke test timed out.'
    }
    if (-not (Test-Path -LiteralPath $harnessLog)) { throw "Page smoke test produced no report in this run: $reportRoot" }
    Get-Content -LiteralPath $harnessLog
    if ($testProcess.ExitCode -ne 0) { throw "Page smoke test failed: $($testProcess.ExitCode)" }
    if ((Get-Content -LiteralPath $harnessLog -Tail 1) -ne 'PASS') {
        throw 'Page smoke test exited before completing its assertions.'
    }
} finally {
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit(5000) | Out-Null }
    $env:KIT_NETMAP_TEST_OUTPUT = $previousOutput
    $env:KIT_NETMAP_TEST_LIFECYCLE_ONLY = $previousLifecycleOnly
    foreach ($path in $backups.Keys) {
        if ($null -ne $backups[$path]) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $temporaryFiles) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $backups.Keys) {
        if ($null -eq $backups[$path]) {
            if (Test-Path -LiteralPath $path) { throw "Originally absent settings file remains: $path" }
        } elseif ([Convert]::ToBase64String([byte[]]$backups[$path]) -cne [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) {
            throw "Settings restore verification failed: $path"
        }
    }
    Write-Host "Settings restore verified: $($backups.Count) files, byte-for-byte or originally absent."
}
