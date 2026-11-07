param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('en-us', 'zh-CN')]
    [string]$Language = 'en-us',
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps" }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$reportRoot = Join-Path $repoRoot "TestResults\AiHubUx\$Configuration\$Language"
$testName = 'AiHubUxSmoke'
$temporaryFiles = @('.dll', '.exe', '.deps.json', '.runtimeconfig.json', '.pri') | ForEach-Object { Join-Path $RuntimeRoot ($testName + $_) }
if (@(Get-Process Kit,Kit.Settings,Kit.QuickAccess,Kit.AIHubWorker,Kit.WingetService,AiHubUxSmoke,winget -ErrorAction SilentlyContinue).Count) {
    throw 'Close Kit and its workers before running the isolated AI Hub UI smoke test.'
}
if ((Get-Item -LiteralPath $RuntimeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a redirected runtime directory.' }
foreach ($path in $temporaryFiles) {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $RuntimeRoot) { throw "Temporary file outside runtime: $path" }
    if (Test-Path -LiteralPath $path) { throw "Temporary test file already exists: $path" }
}
if (-not (Test-Path -LiteralPath (Join-Path $RuntimeRoot 'Kit.AIHubLib.dll'))) { throw 'Build Settings and AIHubLib before running this smoke test.' }
$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
$sdkVersion = (& dotnet --version).Trim()
$sdkRoot = Join-Path $dotnetRoot "sdk\$sdkVersion"
$hostPack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $hostPack) { throw '.NET 10 x64 apphost pack is required.' }
$backups = @{}
foreach ($name in @('settings.json', 'settings-placement.json', 'language.json', 'log_settings.json',
    'AiHub\settings.json', 'AiHub\service-settings.json', 'AiHub\secrets.dat', 'AiHub\security.md',
    'AiHub\chains\security-audit\AGENTS.md', 'AiHub\chains\system-optimization\AGENTS.md', 'AiHub\chains\self-test\AGENTS.md',
    'AiHub\State\audit_history.json', 'AiHub\State\optimization_history.json')) {
    $path = Join-Path $env:LOCALAPPDATA "Kit\$name"
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$testTemp = Join-Path $reportRoot 'tmp'
[IO.Directory]::CreateDirectory($testTemp) | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousOutput = $env:KIT_AI_HUB_UX_TEST_OUTPUT
$testProcess = $null
try {
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    $generalPath = Join-Path $env:LOCALAPPDATA 'Kit\settings.json'
    $general = if ($backups[$generalPath]) { [Text.Encoding]::UTF8.GetString($backups[$generalPath]) | ConvertFrom-Json -AsHashtable } else { @{} }
    if ($general -isnot [Collections.IDictionary]) { throw 'General settings must be a JSON object.' }
    if ($general['enabled'] -isnot [Collections.IDictionary]) { $general['enabled'] = @{} }
    foreach ($module in @('Awake', 'LightSwitch', 'Localserver', 'UDPtest', 'NetMap', 'AIHub', 'Winget')) { $general['enabled'][$module] = $false }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $generalPath)) | Out-Null
    [IO.File]::WriteAllText($generalPath, ($general | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA 'Kit\language.json'), (@{ language = $Language } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $aiServicePath = Join-Path $env:LOCALAPPDATA 'Kit\AiHub\service-settings.json'
    $aiService = if ($backups[$aiServicePath]) { [Text.Encoding]::UTF8.GetString($backups[$aiServicePath]) | ConvertFrom-Json -AsHashtable } else { @{} }
    if ($aiService -isnot [Collections.IDictionary]) { throw 'AI service settings must be a JSON object.' }
    $aiService['isEnabled'] = $false
    [IO.Directory]::CreateDirectory((Split-Path -Parent $aiServicePath)) | Out-Null
    [IO.File]::WriteAllText($aiServicePath, ($aiService | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    # Constructors may load local history. Use empty fixture histories so renders
    # cannot include the user's findings or file names, then restore exact bytes.
    foreach ($name in @('audit_history.json', 'optimization_history.json')) {
        $path = Join-Path $env:LOCALAPPDATA "Kit\AiHub\State\$name"
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        [IO.File]::WriteAllText($path, '[]', [Text.UTF8Encoding]::new($false))
    }
    $compilerArgs = [Collections.Generic.List[string]]::new()
    foreach ($arg in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($arg) }
    $compilerArgs.Add('/out:"' + (Join-Path $RuntimeRoot "$testName.dll") + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'AiHubUxSmoke.cs') + '"')
    foreach ($file in Get-ChildItem -LiteralPath $RuntimeRoot -Filter '*.dll' -File) {
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $compilerArgs.Add('/reference:"' + $file.FullName + '"')
    }
    $responseFile = Join-Path $reportRoot 'aihub-ux-csc.rsp'
    [IO.File]::WriteAllLines($responseFile, $compilerArgs)
    & dotnet (Join-Path $sdkRoot 'Roslyn\bincore\csc.dll') ('@' + $responseFile)
    if ($LASTEXITCODE -ne 0) { throw 'AI Hub UI smoke compilation failed.' }
    [Reflection.Assembly]::LoadFrom((Join-Path $sdkRoot 'Microsoft.NET.HostModel.dll')) | Out-Null
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost(
        (Join-Path $hostPack.FullName 'runtimes\win-x64\native\apphost.exe'),
        (Join-Path $RuntimeRoot "$testName.exe"), "$testName.dll", $true,
        (Join-Path $RuntimeRoot 'Kit.Settings.exe'), $false, $false, $null)
    & icacls.exe (Join-Path $RuntimeRoot "$testName.exe") /setintegritylevel M /L | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set smoke app to medium integrity.' }
    foreach ($extension in @('.deps.json', '.runtimeconfig.json', '.pri')) {
        Copy-Item -LiteralPath (Join-Path $RuntimeRoot ('Kit.Settings' + $extension)) -Destination (Join-Path $RuntimeRoot ($testName + $extension))
    }
    $env:KIT_AI_HUB_UX_TEST_OUTPUT = $reportRoot
    $pipeSuffix = [guid]::NewGuid().ToString('N')
    $launchArguments = @("KitAiHubUxRunner-$pipeSuffix", "KitAiHubUxSettings-$pipeSuffix", "$PID", 'system', 'false', 'false', 'false')
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $RuntimeRoot "$testName.exe"))
    $startInfo.WorkingDirectory = $RuntimeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $launchArguments) { $startInfo.ArgumentList.Add($argument) }
    $testProcess = [Diagnostics.Process]::Start($startInfo)
    $harnessLog = Join-Path $reportRoot 'aihub-ux-smoke.log'
    if (-not $testProcess.WaitForExit(60000)) {
        if (Test-Path -LiteralPath $harnessLog) { Get-Content -LiteralPath $harnessLog -Tail 10 }
        throw 'AI Hub UI smoke timed out after 60 seconds.'
    }
    Get-Content -LiteralPath $harnessLog
    if ($testProcess.ExitCode -ne 0) { throw "AI Hub UI smoke failed: $($testProcess.ExitCode)" }
    if ((Get-Content -LiteralPath $harnessLog -Tail 1) -ne 'PASS') { throw 'AI Hub UI smoke exited before assertions completed.' }
    if (@(Get-Process Kit.AIHubWorker,Kit.WingetService,winget -ErrorAction SilentlyContinue).Count) { throw 'A worker appeared during the isolated smoke test.' }
    foreach ($name in @('audit_history.json', 'optimization_history.json')) {
        $path = Join-Path $env:LOCALAPPDATA "Kit\AiHub\State\$name"
        if ([IO.File]::ReadAllText($path) -ne '[]') { throw "Synthetic UI smoke changed execution history: $name" }
    }
} finally {
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit(5000) | Out-Null }
    $env:KIT_AI_HUB_UX_TEST_OUTPUT = $previousOutput
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    foreach ($path in $backups.Keys) {
        if ($null -ne $backups[$path]) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $temporaryFiles) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    foreach ($path in $backups.Keys) {
        if ($null -eq $backups[$path]) {
            if (Test-Path -LiteralPath $path) { throw "Unexpected fixture file remained: $path" }
        } elseif ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ne [Convert]::ToBase64String($backups[$path])) {
            throw "Original configuration was not restored: $path"
        }
    }
}
