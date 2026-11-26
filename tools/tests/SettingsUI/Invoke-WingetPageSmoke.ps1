param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('en-us', 'zh-CN')]
    [string]$Language = 'en-us',
    [string]$RuntimeRoot,
    [ValidateSet('Light', 'Dark')]
    [string]$HistoryPreview
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps" }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$runName = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [guid]::NewGuid().ToString('N')
$reportRoot = Join-Path $repoRoot "TestResults\WingetPage\$Configuration\$Language\$runName"
Write-Host "Winget page smoke report: $reportRoot"
$testName = 'WingetPageSmoke'
$temporaryFiles = @('.dll', '.exe', '.deps.json', '.runtimeconfig.json', '.pri') | ForEach-Object { Join-Path $RuntimeRoot ($testName + $_) }
if (@(Get-Process Kit,Kit.Settings,Kit.QuickAccess,Kit.WingetService,WingetPageSmoke,winget -ErrorAction SilentlyContinue).Count) {
    throw 'Close Kit and WinGet before running the isolated page smoke test.'
}
if ((Get-Item -LiteralPath $RuntimeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing to use a redirected runtime directory.'
}
foreach ($path in $temporaryFiles) {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $RuntimeRoot) { throw "Temporary file outside runtime: $path" }
    if (Test-Path -LiteralPath $path) { throw "Temporary test file already exists: $path" }
}
if (-not (Test-Path -LiteralPath (Join-Path $RuntimeRoot 'Kit.WingetLib.dll'))) { throw 'Build the Winget Settings integration before running this smoke test.' }
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
foreach ($name in @('settings.json', 'settings-placement.json', 'language.json', 'log_settings.json', 'Winget\settings.json', 'Winget\State\service-state.json', 'AiHub\settings.json', 'AiHub\service-settings.json', 'AiHub\secrets.dat', 'AiHub\security.md', 'AiHub\chains\security-audit\AGENTS.md', 'AiHub\chains\system-optimization\AGENTS.md', 'AiHub\chains\self-test\AGENTS.md')) {
    $path = Join-Path $env:LOCALAPPDATA "Kit\$name"
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$testTemp = Join-Path $reportRoot 'tmp'
[IO.Directory]::CreateDirectory($testTemp) | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousOutput = $env:KIT_WINGET_PAGE_TEST_OUTPUT
$previousLanguage = $env:KIT_WINGET_PAGE_TEST_LANGUAGE
$previousHistoryPreview = $env:KIT_WINGET_HISTORY_PREVIEW
$testProcess = $null
try {
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    # Persisted module state remains disabled. Enabled synthetic layout fixtures
    # suspend the page's lifecycle activity source as well as the VM timer, so
    # delayed window events cannot restart service polling. Real lifecycle
    # handlers and final cached-navigation checks remain attached and enabled.
    # No package confirmation is accepted or package operation submitted. The
    # history-cleanup fixture uses a unique in-process pipe and synthetic state.
    $generalPath = Join-Path $env:LOCALAPPDATA 'Kit\settings.json'
    $general = if ($backups[$generalPath]) { [Text.Encoding]::UTF8.GetString($backups[$generalPath]) | ConvertFrom-Json -AsHashtable } else { @{} }
    if ($general -isnot [Collections.IDictionary]) { throw 'General settings must be a JSON object.' }
    if ($general['enabled'] -isnot [Collections.IDictionary]) { $general['enabled'] = @{} }
    foreach ($module in @('Awake', 'LightSwitch', 'Localserver', 'UDPtest', 'NetMap', 'AIHub', 'Winget')) { $general['enabled'][$module] = $false }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $generalPath)) | Out-Null
    [IO.File]::WriteAllText($generalPath, ($general | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA 'Kit\language.json'), (@{ language = $Language } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $aiServicePath = Join-Path $env:LOCALAPPDATA 'Kit\AiHub\service-settings.json'
    $aiService = if ($backups[$aiServicePath]) { [Text.Encoding]::UTF8.GetString($backups[$aiServicePath]) | ConvertFrom-Json -AsHashtable } else { @{ targets = @(@{ name = 'Main' }, @{ name = 'Fallback' }) } }
    if ($aiService -isnot [Collections.IDictionary]) { throw 'AI Hub service settings must be a JSON object.' }
    $aiService['isEnabled'] = $false
    [IO.Directory]::CreateDirectory((Split-Path -Parent $aiServicePath)) | Out-Null
    [IO.File]::WriteAllText($aiServicePath, ($aiService | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    $compilerArgs = [Collections.Generic.List[string]]::new()
    foreach ($arg in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($arg) }
    $compilerArgs.Add('/out:"' + (Join-Path $RuntimeRoot "$testName.dll") + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'WingetPageSmoke.cs') + '"')
    foreach ($file in Get-ChildItem -LiteralPath $RuntimeRoot -Filter '*.dll' -File) {
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $compilerArgs.Add('/reference:"' + $file.FullName + '"')
    }
    $responseFile = Join-Path $reportRoot 'winget-pages-csc.rsp'
    [IO.File]::WriteAllLines($responseFile, $compilerArgs)
    & dotnet (Join-Path $sdkRoot 'Roslyn\bincore\csc.dll') ('@' + $responseFile)
    if ($LASTEXITCODE -ne 0) { throw 'Winget page smoke compilation failed.' }
    [Reflection.Assembly]::LoadFrom((Join-Path $sdkRoot 'Microsoft.NET.HostModel.dll')) | Out-Null
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost(
        (Join-Path $hostPack.FullName 'runtimes\win-x64\native\apphost.exe'),
        (Join-Path $RuntimeRoot "$testName.exe"), "$testName.dll", $true,
        (Join-Path $RuntimeRoot 'Kit.Settings.exe'), $false, $false, $null)
    & icacls.exe (Join-Path $RuntimeRoot "$testName.exe") /setintegritylevel M /L | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the temporary smoke executable to medium integrity.' }
    foreach ($extension in @('.deps.json', '.runtimeconfig.json', '.pri')) {
        Copy-Item -LiteralPath (Join-Path $RuntimeRoot ('Kit.Settings' + $extension)) -Destination (Join-Path $RuntimeRoot ($testName + $extension))
    }
    $env:KIT_WINGET_PAGE_TEST_OUTPUT = $reportRoot
    $env:KIT_WINGET_PAGE_TEST_LANGUAGE = $Language
    if ($HistoryPreview) { $env:KIT_WINGET_HISTORY_PREVIEW = $HistoryPreview }
    $historyPreviewEnabled = $env:KIT_WINGET_HISTORY_PREVIEW -and $env:KIT_WINGET_HISTORY_PREVIEW -notin @('0', 'false')
    $timeoutSeconds = if ($historyPreviewEnabled) { 330 } else { 120 }
    if ($historyPreviewEnabled) {
        Write-Host "History preview ready marker: $(Join-Path $reportRoot 'history-preview.ready.json')"
        Write-Host "Create/remove $(Join-Path $reportRoot 'history-preview.shift') to switch the temporary background between states B/A."
        Write-Host "Create $(Join-Path $reportRoot 'history-preview.continue') after native capture to continue the smoke test."
    }
    $pipeSuffix = [guid]::NewGuid().ToString('N')
    $launchArguments = @("\\.\pipe\KitWingetSmokeRunner-$pipeSuffix", "\\.\pipe\KitWingetSmokeSettings-$pipeSuffix", "$PID", 'system', 'false', 'false', 'false')
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $RuntimeRoot "$testName.exe"))
    $startInfo.WorkingDirectory = $RuntimeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $launchArguments) { $startInfo.ArgumentList.Add($argument) }
    $testProcess = [Diagnostics.Process]::Start($startInfo)
    $harnessLog = Join-Path $reportRoot 'winget-pages-smoke.log'
    $runTimer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $testProcess.HasExited -and $runTimer.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        $remainingMilliseconds = [int][Math]::Max(1, ($timeoutSeconds * 1000) - $runTimer.Elapsed.TotalMilliseconds)
        if ($testProcess.WaitForExit([Math]::Min(30000, $remainingMilliseconds))) { break }
        if ($historyPreviewEnabled -and (Test-Path -LiteralPath (Join-Path $reportRoot 'history-preview.ready.json'))) {
            Write-Host "History preview reached; see $harnessLog for the active pause state."
        }
    }
    if (-not $testProcess.HasExited) {
        if (Test-Path -LiteralPath $harnessLog) { Get-Content -LiteralPath $harnessLog -Tail 10 }
        throw "Winget page smoke timed out after $timeoutSeconds seconds."
    }
    Get-Content -LiteralPath $harnessLog
    if ($testProcess.ExitCode -ne 0) { throw "Winget page smoke failed: $($testProcess.ExitCode)" }
    if ((Get-Content -LiteralPath $harnessLog -Tail 1) -ne 'PASS') { throw 'Winget page smoke exited before completing its assertions.' }
    if (@(Get-Process Kit.WingetService,winget -ErrorAction SilentlyContinue).Count) { throw 'A WinGet process appeared during the isolated smoke test.' }
    $statePath = Join-Path $env:LOCALAPPDATA 'Kit\Winget\State\service-state.json'
    $stateAfter = if (Test-Path -LiteralPath $statePath) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($statePath)) } else { $null }
    $stateBefore = if ($null -ne $backups[$statePath]) { [Convert]::ToBase64String($backups[$statePath]) } else { $null }
    if ($stateAfter -ne $stateBefore) { throw 'The isolated page smoke changed Winget service state.' }
} finally {
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit(5000) | Out-Null }
    $env:KIT_WINGET_PAGE_TEST_OUTPUT = $previousOutput
    $env:KIT_WINGET_PAGE_TEST_LANGUAGE = $previousLanguage
    $env:KIT_WINGET_HISTORY_PREVIEW = $previousHistoryPreview
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    foreach ($path in $backups.Keys) {
        if ($null -ne $backups[$path]) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $temporaryFiles) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $backups.Keys) {
        if ($null -eq $backups[$path]) {
            if (Test-Path -LiteralPath $path) { throw "Unexpected persisted fixture file: $path" }
        } elseif ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ne [Convert]::ToBase64String($backups[$path])) {
            throw "Original configuration was not restored: $path"
        }
    }
}
