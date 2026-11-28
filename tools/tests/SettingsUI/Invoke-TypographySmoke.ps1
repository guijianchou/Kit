param(
    [ValidateSet('Typography', 'Plugins', 'AiUsage')]
    [string]$Suite = 'Typography',
    [ValidateSet('AIHub', 'Awake', 'LightSwitch', 'Localserver', 'NetMap', 'UDPtest', 'Winget')]
    [string[]]$PluginFilter = @(),
    [switch]$BrushOnly,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('system', 'Noto Sans SC')]
    [string]$FontFamily = 'system',
    [ValidateRange(12, 20)]
    [int]$FontSize = 14,
    [AllowEmptyString()]
    [ValidateSet('', 'en-us', 'zh-CN')]
    [string]$Language = 'en-us',
    [string]$RuntimeRoot,
    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AiHubDerivedStateFixture.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps" }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$runName = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [guid]::NewGuid().ToString('N')
$familyName = if ($FontFamily -eq 'system') { 'system' } else { 'noto-sans-sc' }
$languageName = if ([string]::IsNullOrEmpty($Language)) { 'system-default' } else { $Language }
$reportCategory = if ($Suite -eq 'Plugins') { 'Plugins' } elseif ($Suite -eq 'AiUsage') { 'AiUsage' } else { 'Typography' }
$reportRoot = Join-Path $repoRoot "TestResults\$reportCategory\$Configuration\$languageName\$familyName-$FontSize\$runName"
Write-Host "Typography smoke report: $reportRoot"
$testName = 'TypographySmoke'
$temporaryFiles = @('.dll', '.exe', '.deps.json', '.runtimeconfig.json', '.pri') | ForEach-Object { Join-Path $RuntimeRoot ($testName + $_) }
if (@(Get-Process Kit,Kit.Settings,Kit.QuickAccess,Kit.AIHubWorker,Kit.WingetService,TypographySmoke,winget -ErrorAction SilentlyContinue).Count) {
    throw 'Close Kit and its workers before running the isolated typography smoke test.'
}
if ((Get-Item -LiteralPath $RuntimeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to use a redirected runtime directory.' }
foreach ($path in $temporaryFiles) {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $RuntimeRoot) { throw "Temporary file outside runtime: $path" }
    if (Test-Path -LiteralPath $path) { throw "Temporary test file already exists: $path" }
}
if (-not (Test-Path -LiteralPath (Join-Path $RuntimeRoot 'Kit.Settings.exe'))) { throw 'Build Settings before running this smoke test.' }
$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
$net10Sdk = & dotnet --list-sdks | ForEach-Object {
    if ($_ -match '^(10\.0\.\d+) \[(.+)\]$') { [pscustomobject]@{ Version = [version]$Matches[1]; Root = Join-Path $Matches[2] $Matches[1] } }
} | Sort-Object Version -Descending | Select-Object -First 1
if (-not $net10Sdk) { throw 'An installed .NET 10 SDK is required for the UI smoke apphost writer.' }
$sdkRoot = $net10Sdk.Root
$hostPack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $hostPack) { throw '.NET 10 x64 apphost pack is required.' }
$backups = @{}
foreach ($name in @('settings.json', 'settings-ui.json', 'settings-placement.json', 'language.json', 'log_settings.json',
    'Winget\settings.json', 'Winget\State\service-state.json', 'AiHub\settings.json', 'AiHub\service-settings.json',
    'AiHub\secrets.dat', 'AiHub\security.md', 'AiHub\chains\security-audit\AGENTS.md',
    'AiHub\chains\system-optimization\AGENTS.md', 'AiHub\chains\self-test\AGENTS.md',
    'AiHub\State\audit_history.json', 'AiHub\State\optimization_history.json',
    'Awake\settings.json', 'LightSwitch\settings.json', 'NetMap\settings.json',
    'Localserver\settings.json', 'UDPtest\settings.json', 'UDPtest\targets.json')) {
    $path = Join-Path $env:LOCALAPPDATA "Kit\$name"
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
$catalogFixtures = @()
$localserverStateFiles = @{}
if ($Suite -eq 'Plugins') {
    # A disabled Localserver page still loads and migrates catalogs. Isolate every
    # source catalog before constructing the page, including migration backups.
    $localserverRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Kit\Localserver'))
    if ((Test-Path -LiteralPath $localserverRoot) -and ((Get-Item -LiteralPath $localserverRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing redirected Localserver data root.' }
    $catalogFixtures = @(Join-Path $localserverRoot 'services.json')
    foreach ($entry in @(@('services.d', '*.json'), @('backups', 'services.*.json'))) {
        $directory = Join-Path $localserverRoot $entry[0]
        if (Test-Path -LiteralPath $directory) {
            if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected Localserver fixture directory.' }
            $catalogFixtures += @(Get-ChildItem -LiteralPath $directory -Filter $entry[1] -File | ForEach-Object FullName)
        }
    }
    foreach ($path in @($catalogFixtures) + @(Join-Path $localserverRoot 'hub.settings.json') + @(Join-Path $localserverRoot 'secrets.dat')) {
        if (-not [IO.Path]::GetFullPath($path).StartsWith($localserverRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped Localserver data root.' }
        $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
    }
    $stateDirectory = Join-Path $localserverRoot 'State'
    if (Test-Path -LiteralPath $stateDirectory) {
        if ((Get-Item -LiteralPath $stateDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected Localserver state directory.' }
        foreach ($file in Get-ChildItem -LiteralPath $stateDirectory -File) {
            $localserverStateFiles[$file.FullName] = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))
        }
    }
}
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
# Snapshot authored translations independently from the live resource loader, so
# stale x:Uid controls cannot pass merely because the runtime loader is stale too.
$expectedStrings = @{}
$expectedKeys = @('General_FontFamily.Header', 'General_FontFamily.Description', 'LanguageHeader.Header',
    'General.ModuleTitle', 'Shell_Dashboard.Content', 'Shell_Settings.Content',
    'Shell_SearchBox.PlaceholderText', 'SettingsWindow_Title', 'DashboardTitle.Text', 'DashboardIntro.Text',
    'AIHub_PageTitle.Text', 'Radio_Theme_Dark.Content', 'Radio_Theme_Light.Content', 'Radio_Theme_Default.Content',
    'LearnMore_Awake.Text', 'SecondaryLink_Awake.Text')
foreach ($culture in @('en-us', 'zh-CN')) {
    [xml]$resources = Get-Content -LiteralPath (Join-Path $repoRoot "src\settings-ui\Settings.UI\Strings\$culture\Resources.resw") -Raw
    $entries = @{}
    foreach ($key in $expectedKeys) {
        $entry = @($resources.root.data | Where-Object { $_.name -eq $key })
        if ($entry.Count -ne 1) { throw "Expected exactly one $culture translation for $key." }
        $entries[$key] = [string]$entry[0].value
    }
    $expectedStrings[$culture] = $entries
}
[IO.File]::WriteAllText((Join-Path $reportRoot 'expected-strings.json'), ($expectedStrings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$testTemp = Join-Path $reportRoot 'tmp'
[IO.Directory]::CreateDirectory($testTemp) | Out-Null
$environmentBackup = @{}
foreach ($name in @('TEMP', 'TMP', 'KIT_TYPOGRAPHY_SMOKE_OUTPUT', 'KIT_TYPOGRAPHY_SMOKE_FONT_FAMILY', 'KIT_TYPOGRAPHY_SMOKE_FONT_SIZE', 'KIT_TYPOGRAPHY_SMOKE_LANGUAGE', 'KIT_TYPOGRAPHY_SMOKE_SUITE', 'KIT_TYPOGRAPHY_SMOKE_PLUGIN_FILTER', 'KIT_TYPOGRAPHY_SMOKE_BRUSH_ONLY')) {
    $environmentBackup[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$aiHubDerivedSnapshot = New-AiHubDerivedStateSnapshot (Join-Path $env:LOCALAPPDATA 'Kit\AiHub\State')
$testProcess = $null
try {
    Clear-AiHubDerivedStateFixture $aiHubDerivedSnapshot
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    if ($Suite -eq 'Plugins') {
        foreach ($path in $catalogFixtures) {
            [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
            [IO.File]::WriteAllText($path, '{"version":2,"services":[]}', [Text.UTF8Encoding]::new($false))
        }
        [IO.File]::WriteAllText((Join-Path $localserverRoot 'hub.settings.json'), '{}', [Text.UTF8Encoding]::new($false))
        $secretsPath = Join-Path $localserverRoot 'secrets.dat'
        if (Test-Path -LiteralPath $secretsPath) { Remove-Item -LiteralPath $secretsPath }
        # Default inert targets are deterministic; the disabled coordinator never
        # probes them. Preserve the user's exact target list for the finally block.
        $targetsPath = Join-Path $env:LOCALAPPDATA 'Kit\UDPtest\targets.json'
        [IO.Directory]::CreateDirectory((Split-Path -Parent $targetsPath)) | Out-Null
        [IO.File]::WriteAllText($targetsPath, '{}', [Text.UTF8Encoding]::new($false))
    }
    # Typography is persisted before Application.Start: startup must resolve the
    # same font resources a real restart would use. Unknown settings are retained.
    $generalPath = Join-Path $env:LOCALAPPDATA 'Kit\settings.json'
    $general = if ($backups[$generalPath]) { [Text.Encoding]::UTF8.GetString($backups[$generalPath]) | ConvertFrom-Json -AsHashtable } else { @{} }
    if ($general -isnot [Collections.IDictionary]) { throw 'General settings must be a JSON object.' }
    if ($general['enabled'] -isnot [Collections.IDictionary]) { $general['enabled'] = @{} }
    foreach ($module in @('Awake', 'LightSwitch', 'Localserver', 'UDPtest', 'NetMap', 'AIHub', 'Winget')) { $general['enabled'][$module] = $false }
    $general['ui_font_family'] = $FontFamily
    $general['ui_font_size'] = $FontSize
    [IO.Directory]::CreateDirectory((Split-Path -Parent $generalPath)) | Out-Null
    [IO.File]::WriteAllText($generalPath, ($general | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA 'Kit\language.json'), (@{ language = $Language } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $aiServicePath = Join-Path $env:LOCALAPPDATA 'Kit\AiHub\service-settings.json'
    $aiService = if ($backups[$aiServicePath]) { [Text.Encoding]::UTF8.GetString($backups[$aiServicePath]) | ConvertFrom-Json -AsHashtable } else { @{ targets = @(@{ name = 'Main' }, @{ name = 'Fallback' }) } }
    if ($aiService -isnot [Collections.IDictionary]) { throw 'AI Hub service settings must be a JSON object.' }
    $aiService['isEnabled'] = $false
    [IO.Directory]::CreateDirectory((Split-Path -Parent $aiServicePath)) | Out-Null
    [IO.File]::WriteAllText($aiServicePath, ($aiService | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    foreach ($name in @('audit_history.json', 'optimization_history.json')) {
        $path = Join-Path $env:LOCALAPPDATA "Kit\AiHub\State\$name"
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        [IO.File]::WriteAllText($path, '[]', [Text.UTF8Encoding]::new($false))
    }
    $compilerArgs = [Collections.Generic.List[string]]::new()
    foreach ($arg in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($arg) }
    $compilerArgs.Add('/out:"' + (Join-Path $RuntimeRoot "$testName.dll") + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'TypographySmoke.cs') + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'PluginsSmoke.cs') + '"')
    foreach ($file in Get-ChildItem -LiteralPath $RuntimeRoot -Filter '*.dll' -File) {
        # Quick Access links the same typography helper into its own executable.
        # This harness targets Settings and must resolve that type from Settings.
        if ($file.Name -eq 'Kit.QuickAccess.dll') { continue }
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $compilerArgs.Add('/reference:"' + $file.FullName + '"')
    }
    $responseFile = Join-Path $reportRoot 'typography-csc.rsp'
    [IO.File]::WriteAllLines($responseFile, $compilerArgs)
    & dotnet (Join-Path $sdkRoot 'Roslyn\bincore\csc.dll') ('@' + $responseFile)
    if ($LASTEXITCODE -ne 0) { throw 'Typography smoke compilation failed.' }
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
    $env:KIT_TYPOGRAPHY_SMOKE_OUTPUT = $reportRoot
    $env:KIT_TYPOGRAPHY_SMOKE_FONT_FAMILY = $FontFamily
    $env:KIT_TYPOGRAPHY_SMOKE_FONT_SIZE = [string]$FontSize
    $env:KIT_TYPOGRAPHY_SMOKE_LANGUAGE = $Language
    $env:KIT_TYPOGRAPHY_SMOKE_SUITE = $Suite
    $env:KIT_TYPOGRAPHY_SMOKE_PLUGIN_FILTER = $PluginFilter -join ','
    $env:KIT_TYPOGRAPHY_SMOKE_BRUSH_ONLY = if ($BrushOnly) { '1' } else { '0' }
    $pipeSuffix = [guid]::NewGuid().ToString('N')
    $launchArguments = @("\\.\pipe\KitTypographySmokeRunner-$pipeSuffix", "\\.\pipe\KitTypographySmokeSettings-$pipeSuffix", "$PID", 'system', 'false', 'false', 'false')
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $RuntimeRoot "$testName.exe"))
    $startInfo.WorkingDirectory = $RuntimeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $launchArguments) { $startInfo.ArgumentList.Add($argument) }
    $testProcess = [Diagnostics.Process]::Start($startInfo)
    $harnessLog = Join-Path $reportRoot 'typography-smoke.log'
    $runTimer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $testProcess.HasExited -and $runTimer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $remaining = [int][Math]::Max(1, ($TimeoutSeconds * 1000) - $runTimer.Elapsed.TotalMilliseconds)
        if ($testProcess.WaitForExit([Math]::Min(30000, $remaining))) { break }
    }
    if (-not $testProcess.HasExited) {
        if (Test-Path -LiteralPath $harnessLog) { Get-Content -LiteralPath $harnessLog -Tail 10 }
        throw "Typography smoke timed out after $TimeoutSeconds seconds."
    }
    Get-Content -LiteralPath $harnessLog
    if ($testProcess.ExitCode -ne 0) { throw "Typography smoke failed: $($testProcess.ExitCode)" }
    if ((Get-Content -LiteralPath $harnessLog -Tail 1) -ne 'PASS') { throw 'Typography smoke exited before completing its assertions.' }
    if (@(Get-Process Kit.AIHubWorker,Kit.WingetService,winget -ErrorAction SilentlyContinue).Count) { throw 'A module worker appeared during the isolated smoke test.' }
    $statePath = Join-Path $env:LOCALAPPDATA 'Kit\Winget\State\service-state.json'
    $stateAfter = if (Test-Path -LiteralPath $statePath) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($statePath)) } else { $null }
    $stateBefore = if ($null -ne $backups[$statePath]) { [Convert]::ToBase64String($backups[$statePath]) } else { $null }
    if ($stateAfter -ne $stateBefore) { throw 'The typography smoke changed Winget service state.' }
    if ($Suite -eq 'Plugins') {
        if ([IO.File]::ReadAllText($targetsPath) -ne '{}') { throw 'UDPtest layout fixture unexpectedly saved target edits.' }
        foreach ($path in $catalogFixtures) {
            if ([IO.File]::ReadAllText($path) -ne '{"version":2,"services":[]}') { throw "Localserver fixture unexpectedly persisted: $path" }
        }
        $stateDirectory = Join-Path $localserverRoot 'State'
        $stateFilesAfter = @(Get-ChildItem -LiteralPath $stateDirectory -File -ErrorAction SilentlyContinue)
        if ($stateFilesAfter.Count -ne $localserverStateFiles.Count) { throw 'Localserver ownership/state file count changed.' }
        foreach ($file in $stateFilesAfter) {
            if (-not $localserverStateFiles.ContainsKey($file.FullName) -or
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName)) -ne $localserverStateFiles[$file.FullName]) { throw 'Localserver ownership/state changed.' }
        }
    }
} finally {
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit(5000) | Out-Null }
    foreach ($name in $environmentBackup.Keys) { [Environment]::SetEnvironmentVariable($name, $environmentBackup[$name], 'Process') }
    $derivedRestoreFailure = $null
    try { Restore-AiHubDerivedStateFixture $aiHubDerivedSnapshot }
    catch { $derivedRestoreFailure = $_ }
    foreach ($path in $backups.Keys) {
        if ($null -ne $backups[$path]) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $temporaryFiles) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    foreach ($path in $backups.Keys) {
        if ($null -eq $backups[$path]) {
            if (Test-Path -LiteralPath $path) { throw "Unexpected persisted fixture file: $path" }
        } elseif ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ne [Convert]::ToBase64String($backups[$path])) { throw "Original configuration was not restored: $path" }
    }
    if ($derivedRestoreFailure) { throw $derivedRestoreFailure }
    Write-Host 'Original configuration restored byte-for-byte; temporary smoke runtime files removed.'
}
