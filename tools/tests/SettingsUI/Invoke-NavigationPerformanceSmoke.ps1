param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Baseline,
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps" }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$mode = if ($Baseline) { 'baseline' } else { 'current' }
$reportRoot = Join-Path $repoRoot "TestResults\NavigationPerformance\$Configuration\$mode"
$testName = 'NavigationPerformanceSmoke'
$temporaryFiles = @('.dll', '.exe', '.deps.json', '.runtimeconfig.json', '.pri') | ForEach-Object { Join-Path $RuntimeRoot ($testName + $_) }
if (@(Get-Process Kit,Kit.Settings,NavigationPerformanceSmoke -ErrorAction SilentlyContinue).Count) {
    throw 'Close Kit before running the navigation performance test.'
}
if ((Get-Item -LiteralPath $RuntimeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing to use a redirected runtime directory.'
}
foreach ($path in $temporaryFiles) {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $RuntimeRoot) { throw "Temporary file outside runtime: $path" }
    if (Test-Path -LiteralPath $path) { throw "Temporary test file already exists: $path" }
}
$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
$sdkVersion = (& dotnet --version).Trim()
$sdkRoot = Join-Path $dotnetRoot "sdk\$sdkVersion"
$hostPack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $hostPack) { throw '.NET 10 x64 apphost pack is required.' }
$backups = @{}
foreach ($name in @('settings.json', 'settings-placement.json', 'language.json', 'log_settings.json', 'UDPtest\settings.json', 'UDPtest\targets.json', 'AiHub\settings.json', 'AiHub\service-settings.json', 'AiHub\secrets.dat', 'AiHub\security.md', 'AiHub\chains\security-audit\AGENTS.md', 'AiHub\chains\system-optimization\AGENTS.md', 'AiHub\chains\self-test\AGENTS.md')) {
    $path = Join-Path $env:LOCALAPPDATA "Kit\$name"
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$testTemp = Join-Path $reportRoot 'tmp'
[IO.Directory]::CreateDirectory($testTemp) | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousOutput = $env:KIT_NAV_TEST_OUTPUT
$previousBaseline = $env:KIT_NAV_BASELINE
$testProcess = $null
try {
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    # AiHubJsonContext uses camelCase. Restore the original bytes in finally.
    $aiServiceSettingsPath = Join-Path $env:LOCALAPPDATA 'Kit\AiHub\service-settings.json'
    if ($null -eq $backups[$aiServiceSettingsPath]) { throw 'Existing AI Hub service-settings.json is required for the isolated navigation test.' }
    $aiServiceSettings = [IO.File]::ReadAllText($aiServiceSettingsPath) | ConvertFrom-Json -AsHashtable
    if ($aiServiceSettings -isnot [Collections.IDictionary]) { throw 'AI Hub service settings must be a JSON object.' }
    $aiServiceSettings['isEnabled'] = $false
    [IO.File]::WriteAllText($aiServiceSettingsPath, ($aiServiceSettings | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    $compilerArgs = [Collections.Generic.List[string]]::new()
    foreach ($arg in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($arg) }
    $compilerArgs.Add('/out:"' + (Join-Path $RuntimeRoot "$testName.dll") + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'NavigationPerformanceSmoke.cs') + '"')
    foreach ($file in Get-ChildItem -LiteralPath $RuntimeRoot -Filter '*.dll' -File) {
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $compilerArgs.Add('/reference:"' + $file.FullName + '"')
    }
    $responseFile = Join-Path $reportRoot 'navigation-performance-csc.rsp'
    [IO.File]::WriteAllLines($responseFile, $compilerArgs)
    & dotnet (Join-Path $sdkRoot 'Roslyn\bincore\csc.dll') ('@' + $responseFile)
    if ($LASTEXITCODE -ne 0) { throw 'Navigation performance test compilation failed.' }
    [Reflection.Assembly]::LoadFrom((Join-Path $sdkRoot 'Microsoft.NET.HostModel.dll')) | Out-Null
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost(
        (Join-Path $hostPack.FullName 'runtimes\win-x64\native\apphost.exe'),
        (Join-Path $RuntimeRoot "$testName.exe"), "$testName.dll", $true,
        (Join-Path $RuntimeRoot 'Kit.Settings.exe'), $false, $false, $null)
    # Match KitSetExecutableIntegrity for this manually generated apphost only.
    & icacls.exe (Join-Path $RuntimeRoot "$testName.exe") /setintegritylevel M /L | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the temporary smoke executable to medium integrity.' }
    foreach ($extension in @('.deps.json', '.runtimeconfig.json', '.pri')) {
        Copy-Item -LiteralPath (Join-Path $RuntimeRoot ('Kit.Settings' + $extension)) -Destination (Join-Path $RuntimeRoot ($testName + $extension))
    }
    $env:KIT_NAV_TEST_OUTPUT = $reportRoot
    $env:KIT_NAV_BASELINE = if ($Baseline) { '1' } else { '0' }
    $pipeSuffix = [guid]::NewGuid().ToString('N')
    $launchArguments = @("KitNavigationSmokeRunner-$pipeSuffix", "KitNavigationSmokeSettings-$pipeSuffix", "$PID", 'system', 'false', 'false', 'false')
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $RuntimeRoot "$testName.exe"))
    $startInfo.WorkingDirectory = $RuntimeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $launchArguments) { $startInfo.ArgumentList.Add($argument) }
    $testProcess = [Diagnostics.Process]::Start($startInfo)
    $harnessLog = Join-Path $reportRoot 'navigation-performance.log'
    if (-not $testProcess.WaitForExit(60000)) {
        if (Test-Path -LiteralPath $harnessLog) { Get-Content -LiteralPath $harnessLog -Tail 10 }
        throw 'Navigation performance test timed out after 60 seconds.'
    }
    Get-Content -LiteralPath $harnessLog
    if ($testProcess.ExitCode -ne 0) { throw "Navigation performance test failed: $($testProcess.ExitCode)" }
    if ((Get-Content -LiteralPath $harnessLog -Tail 1) -ne 'PASS') {
        throw 'Navigation performance test exited before completing its assertions.'
    }
} finally {
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit(5000) | Out-Null }
    $env:KIT_NAV_TEST_OUTPUT = $previousOutput
    $env:KIT_NAV_BASELINE = $previousBaseline
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    foreach ($path in $backups.Keys) {
        if ($null -ne $backups[$path]) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $temporaryFiles) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}
