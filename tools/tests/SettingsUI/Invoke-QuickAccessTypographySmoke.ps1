#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('system', 'Noto Sans SC')]
    [string]$FontFamily = 'system',
    [ValidateRange(12, 20)]
    [int]$FontSize = 14,
    [switch]$DiagnoseResources,
    [string]$RuntimeRoot,
    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps" }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$runName = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [guid]::NewGuid().ToString('N')
$familyName = if ($FontFamily -eq 'system') { 'system' } else { 'noto-sans-sc' }
$reportRoot = Join-Path $repoRoot "TestResults\QuickAccessTypography\$Configuration\$familyName-$FontSize\$runName"
$testName = 'QuickAccessTypographySmoke'
$temporaryFiles = @('.dll', '.exe', '.deps.json', '.runtimeconfig.json', '.pri') | ForEach-Object { Join-Path $RuntimeRoot ($testName + $_) }
Write-Host "QuickAccess typography report: $reportRoot"
if (@(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'Kit' -or $_.ProcessName -like 'Kit.*' -or $_.ProcessName -like '*Smoke' -or $_.ProcessName -eq 'winget' }).Count) {
    throw 'Close Kit, workers, and other smoke tests before running the isolated QuickAccess smoke.'
}
if ((Get-Item -LiteralPath $RuntimeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected runtime directory.' }
foreach ($path in $temporaryFiles) {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $RuntimeRoot) { throw "Temporary file outside runtime: $path" }
    if (Test-Path -LiteralPath $path) { throw "Temporary test file already exists: $path" }
}
foreach ($name in @('Kit.QuickAccess.exe', 'Kit.QuickAccess.dll', 'Kit.QuickAccess.pri', 'Kit.QuickAccess.deps.json', 'Kit.QuickAccess.runtimeconfig.json', 'Assets\Fonts\NotoSansSC-Variable.ttf')) {
    if (-not (Test-Path -LiteralPath (Join-Path $RuntimeRoot $name) -PathType Leaf)) { throw "Build QuickAccess before running this smoke: missing $name" }
}
$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
$net10Sdk = & dotnet --list-sdks | ForEach-Object {
    if ($_ -match '^(10\.0\.\d+) \[(.+)\]$') { [pscustomobject]@{ Version = [version]$Matches[1]; Root = Join-Path $Matches[2] $Matches[1] } }
} | Sort-Object Version -Descending | Select-Object -First 1
if (-not $net10Sdk) { throw '.NET 10 SDK is required.' }
$sdkRoot = $net10Sdk.Root
$hostPack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $hostPack) { throw '.NET 10 x64 apphost pack is required.' }
$configRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kit'
if ((Test-Path -LiteralPath $configRoot) -and ((Get-Item -LiteralPath $configRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing redirected Kit configuration root.' }
$generalPath = Join-Path $configRoot 'settings.json'
$backups = @{}
foreach ($name in @('settings.json', 'log_settings.json', 'Awake\settings.json')) {
    $path = Join-Path $configRoot $name
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing redirected fixture: $path" }
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
$environmentBackup = @{}
foreach ($name in @('TEMP', 'TMP', 'KIT_QUICKACCESS_TYPOGRAPHY_OUTPUT', 'KIT_QUICKACCESS_TYPOGRAPHY_FAMILY', 'KIT_QUICKACCESS_TYPOGRAPHY_SIZE', 'KIT_QUICKACCESS_TYPOGRAPHY_DIAGNOSE')) {
    $environmentBackup[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$testTemp = Join-Path $reportRoot 'tmp'
[IO.Directory]::CreateDirectory($testTemp) | Out-Null
$testProcess = $null
try {
    $general = if ($backups[$generalPath]) { [Text.Encoding]::UTF8.GetString($backups[$generalPath]) | ConvertFrom-Json -AsHashtable } else { @{} }
    if ($general -isnot [Collections.IDictionary]) { throw 'General settings must be a JSON object.' }
    if ($general['enabled'] -isnot [Collections.IDictionary]) { $general['enabled'] = @{} }
    foreach ($module in @('Awake', 'LightSwitch', 'Localserver', 'UDPtest', 'NetMap', 'AIHub', 'Winget')) { $general['enabled'][$module] = $false }
    $general['ui_font_family'] = $FontFamily
    $general['ui_font_size'] = $FontSize
    [IO.Directory]::CreateDirectory($configRoot) | Out-Null
    [IO.File]::WriteAllText($generalPath, ($general | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    $compilerArgs = [Collections.Generic.List[string]]::new()
    foreach ($arg in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($arg) }
    $compilerArgs.Add('/out:"' + (Join-Path $RuntimeRoot "$testName.dll") + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'QuickAccessTypographySmoke.cs') + '"')
    foreach ($file in Get-ChildItem -LiteralPath $RuntimeRoot -Filter '*.dll' -File) {
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        # Both applications compile the shared typography type. Bind this harness
        # explicitly to QuickAccess without polluting the global type namespace.
        $reference = if ($file.Name -eq 'Kit.QuickAccess.dll') { '/reference:QuickAccess="' } else { '/reference:"' }
        $compilerArgs.Add($reference + $file.FullName + '"')
    }
    $responseFile = Join-Path $reportRoot 'quickaccess-typography-csc.rsp'
    [IO.File]::WriteAllLines($responseFile, $compilerArgs)
    & dotnet (Join-Path $sdkRoot 'Roslyn\bincore\csc.dll') ('@' + $responseFile)
    if ($LASTEXITCODE -ne 0) { throw 'QuickAccess typography smoke compilation failed.' }
    [Reflection.Assembly]::LoadFrom((Join-Path $sdkRoot 'Microsoft.NET.HostModel.dll')) | Out-Null
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost(
        (Join-Path $hostPack.FullName 'runtimes\win-x64\native\apphost.exe'),
        (Join-Path $RuntimeRoot "$testName.exe"), "$testName.dll", $true,
        (Join-Path $RuntimeRoot 'Kit.QuickAccess.exe'), $false, $false, $null)
    & icacls.exe (Join-Path $RuntimeRoot "$testName.exe") /setintegritylevel M /L | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set temporary smoke executable to medium integrity.' }
    foreach ($extension in @('.deps.json', '.runtimeconfig.json', '.pri')) {
        Copy-Item -LiteralPath (Join-Path $RuntimeRoot ('Kit.QuickAccess' + $extension)) -Destination (Join-Path $RuntimeRoot ($testName + $extension))
    }
    $env:KIT_QUICKACCESS_TYPOGRAPHY_OUTPUT = $reportRoot
    $env:KIT_QUICKACCESS_TYPOGRAPHY_FAMILY = $FontFamily
    $env:KIT_QUICKACCESS_TYPOGRAPHY_SIZE = [string]$FontSize
    $env:KIT_QUICKACCESS_TYPOGRAPHY_DIAGNOSE = if ($DiagnoseResources) { '1' } else { '0' }
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $RuntimeRoot "$testName.exe"))
    $startInfo.WorkingDirectory = $RuntimeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    # No Settings positional arguments or production QuickAccess event/pipe names.
    $testProcess = [Diagnostics.Process]::Start($startInfo)
    $harnessLog = Join-Path $reportRoot 'quickaccess-typography-smoke.log'
    $runTimer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $testProcess.HasExited -and $runTimer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $remaining = [int][Math]::Max(1, ($TimeoutSeconds * 1000) - $runTimer.Elapsed.TotalMilliseconds)
        if ($testProcess.WaitForExit([Math]::Min(30000, $remaining))) { break }
    }
    if (-not $testProcess.HasExited) { throw "QuickAccess typography smoke timed out after $TimeoutSeconds seconds." }
    Get-Content -LiteralPath $harnessLog
    if ($testProcess.ExitCode -ne 0) { throw "QuickAccess typography smoke failed: $($testProcess.ExitCode)" }
    $expectedCompletion = if ($DiagnoseResources) { 'DIAGNOSTIC PASS' } else { 'PASS' }
    if ((Get-Content -LiteralPath $harnessLog -Tail 1) -ne $expectedCompletion) { throw 'QuickAccess typography smoke exited before completing assertions.' }
    if (@(Get-Process Kit,Kit.Settings,Kit.QuickAccess,Kit.AIHubWorker,Kit.WingetService,winget -ErrorAction SilentlyContinue).Count) { throw 'An unexpected Kit application or worker appeared.' }
} finally {
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit(5000) | Out-Null }
    foreach ($name in $environmentBackup.Keys) { [Environment]::SetEnvironmentVariable($name, $environmentBackup[$name], 'Process') }
    foreach ($path in $backups.Keys) {
        if ($null -ne $backups[$path]) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $temporaryFiles) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    foreach ($path in $backups.Keys) {
        if ($null -eq $backups[$path]) {
            if (Test-Path -LiteralPath $path) { throw "Unexpected persisted fixture: $path" }
        } elseif ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ne [Convert]::ToBase64String($backups[$path])) { throw "Configuration was not restored: $path" }
    }
    foreach ($path in $temporaryFiles) { if (Test-Path -LiteralPath $path) { throw "Temporary runtime file remains: $path" } }
    Write-Host 'Original QuickAccess configuration restored byte-for-byte; temporary runtime files removed.'
}
