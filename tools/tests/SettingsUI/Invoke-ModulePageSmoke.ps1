param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('en-US', 'zh-CN')]
    [string]$Language = 'en-US',
    [ValidateRange(-1, 2)]
    [Nullable[int]]$ColdStartTab,
    [switch]$ColdStartDirectRoute,
    [ValidateSet('Enabled', 'Disabled', 'Failed')]
    [string]$ColdStartGeneral,
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
if ($ColdStartDirectRoute -and $null -eq $ColdStartTab) { throw '-ColdStartDirectRoute requires -ColdStartTab.' }
if ($ColdStartGeneral -and $null -ne $ColdStartTab) { throw 'Choose either -ColdStartGeneral or -ColdStartTab.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps" }
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N')
$reportRoot = Join-Path $repoRoot "TestResults\PluginLifecycle\$Language\$runId"
$testName = 'ModulePageSmoke'
$temporaryFiles = @('.dll', '.exe', '.deps.json', '.runtimeconfig.json', '.pri') | ForEach-Object { Join-Path $runtimeRoot ($testName + $_) }
if (@(Get-Process Kit,Kit.Settings,ModulePageSmoke -ErrorAction SilentlyContinue).Count) {
    throw 'Close Kit before running the page smoke test.'
}
foreach ($path in $temporaryFiles) {
    if (Test-Path -LiteralPath $path) { throw "Temporary test file already exists: $path" }
}
if ((Get-Item -LiteralPath $runtimeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing to use a redirected runtime directory.'
}
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
Write-Host "Module page smoke report directory: $reportRoot"
$testTemp = Join-Path $reportRoot "tmp"
[IO.Directory]::CreateDirectory($testTemp) | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$env:TEMP = $testTemp
$env:TMP = $testTemp
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
foreach ($name in @('settings.json', 'settings-placement.json', 'log_settings.json', 'language.json', 'AiHub\settings.json', 'AiHub\service-settings.json', 'AiHub\secrets.dat', 'AiHub\security.md', 'AiHub\chains\security-audit\AGENTS.md', 'AiHub\chains\system-optimization\AGENTS.md', 'AiHub\chains\self-test\AGENTS.md')) {
    $path = Join-Path $env:LOCALAPPDATA "Kit\$name"
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
$previousOutput = $env:KIT_MODULE_PAGE_TEST_OUTPUT
$previousColdStartTab = $env:KIT_MODULE_PAGE_COLD_START_TAB
$previousColdStartDirect = $env:KIT_MODULE_PAGE_COLD_START_DIRECT
$previousColdStartGeneral = $env:KIT_MODULE_PAGE_COLD_START_GENERAL
$testProcess = $null
try {
    [IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA 'Kit\language.json'), ('{"language":"' + $Language + '"}'))
    $compilerArgs = [Collections.Generic.List[string]]::new()
    foreach ($arg in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($arg) }
    $compilerArgs.Add('/out:"' + (Join-Path $runtimeRoot "$testName.dll") + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'ModulePageSmoke.cs') + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'UDPtestRenderingChecks.cs') + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'LocalserverRenderingChecks.cs') + '"')
    foreach ($file in Get-ChildItem -LiteralPath $runtimeRoot -Filter '*.dll' -File) {
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $compilerArgs.Add('/reference:"' + $file.FullName + '"')
    }
    $responseFile = Join-Path $reportRoot 'module-pages-csc.rsp'
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
    $env:KIT_MODULE_PAGE_TEST_OUTPUT = $reportRoot
    $env:KIT_MODULE_PAGE_COLD_START_TAB = if ($null -ne $ColdStartTab) { [string]$ColdStartTab } else { $null }
    $env:KIT_MODULE_PAGE_COLD_START_DIRECT = if ($ColdStartDirectRoute) { '1' } else { $null }
    $env:KIT_MODULE_PAGE_COLD_START_GENERAL = $ColdStartGeneral
    # Release Settings requires the Runner launch contract; use private, unused pipes
    # because the harness supplies its own in-memory replies after startup.
    $pipeSuffix = [guid]::NewGuid().ToString('N')
    $launchArguments = @("\\.\pipe\KitPageSmokeRunner-$pipeSuffix", "\\.\pipe\KitPageSmokeSettings-$pipeSuffix", "$PID", 'system', 'false', 'false', 'false')
    if ($ColdStartDirectRoute) { $launchArguments[6] = 'true'; $launchArguments += 'AIHub' }
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $runtimeRoot "$testName.exe"))
    $startInfo.WorkingDirectory = $runtimeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $launchArguments) { $startInfo.ArgumentList.Add($argument) }
    $testProcess = [Diagnostics.Process]::Start($startInfo)
    if (-not $testProcess.WaitForExit(60000)) {
        Write-Host 'Page smoke test timed out after 60s; harness log tail:'
        $harnessLog = Join-Path $reportRoot 'module-pages-smoke.log'
        if (Test-Path -LiteralPath $harnessLog) { Get-Content -LiteralPath $harnessLog -Tail 5 }
        throw 'Page smoke test timed out.'
    }
    Get-Content -LiteralPath (Join-Path $reportRoot 'module-pages-smoke.log')
    if ($testProcess.ExitCode -ne 0) { throw "Page smoke test failed: $($testProcess.ExitCode)" }
    if ((Get-Content -LiteralPath (Join-Path $reportRoot 'module-pages-smoke.log') -Tail 1) -ne 'PASS') {
        throw 'Page smoke test exited before completing its assertions.'
    }
} finally {
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit(5000) | Out-Null }
    $env:KIT_MODULE_PAGE_TEST_OUTPUT = $previousOutput
    $env:KIT_MODULE_PAGE_COLD_START_TAB = $previousColdStartTab
    $env:KIT_MODULE_PAGE_COLD_START_DIRECT = $previousColdStartDirect
    $env:KIT_MODULE_PAGE_COLD_START_GENERAL = $previousColdStartGeneral
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $restoreResults = [Collections.Generic.List[string]]::new()
    $restoreFailures = [Collections.Generic.List[string]]::new()
    foreach ($path in $backups.Keys) {
        try {
            if ($null -ne $backups[$path]) {
                [IO.File]::WriteAllBytes($path, $backups[$path])
                if (-not [Linq.Enumerable]::SequenceEqual[byte]([byte[]]$backups[$path], [IO.File]::ReadAllBytes($path))) {
                    throw 'Restored bytes differ from the original backup.'
                }
                $restoreResults.Add("PASS byte-for-byte: $path")
            } else {
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
                if (Test-Path -LiteralPath $path) { throw 'A file absent before the test still exists.' }
                $restoreResults.Add("PASS originally absent: $path")
            }
        } catch {
            $restoreFailures.Add($path + ': ' + $_.Exception.Message)
            $restoreResults.Add('FAIL ' + $path + ': ' + $_.Exception.Message)
        }
    }
    foreach ($path in $temporaryFiles) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    [IO.File]::WriteAllLines((Join-Path $reportRoot 'restore-verification.log'), $restoreResults)
    if ($restoreFailures.Count) { throw ('Settings restoration failed: ' + ($restoreFailures -join '; ')) }
    Write-Host "Settings restore verified: $($restoreResults.Count) files, byte-for-byte or originally absent."
}
