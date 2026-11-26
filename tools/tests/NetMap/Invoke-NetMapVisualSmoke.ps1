param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('en-US', 'zh-CN')][string]$Language = 'en-US',
    [ValidateRange(12, 20)][int]$FontSize = 14,
    [string]$RuntimeRoot,
    [ValidateRange(10, 45)][int]$TimeoutSeconds = 45,
    [ValidateRange(0, 300)][int]$ManualVisualSeconds = 0
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps" }
$sourceRuntime = [IO.Path]::GetFullPath($RuntimeRoot)
if (-not (Test-Path -LiteralPath (Join-Path $sourceRuntime 'Kit.Settings.dll'))) { throw 'Build Settings before running the visual smoke.' }
if ((Get-Item -LiteralPath $sourceRuntime).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected source runtime.' }
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N')
$reportRoot = Join-Path $repoRoot "TestResults\NetMapVisual\$Language\$runId"
$isolatedRuntime = Join-Path $reportRoot 'runtime'
[IO.Directory]::CreateDirectory($isolatedRuntime) | Out-Null
Write-Host "NetMap visual report: $reportRoot"
# Copy dependencies instead of adding executables to the runtime used by a running Kit.
# Reject child reparse points so copied data cannot escape into another runtime/profile.
if (@(Get-ChildItem -LiteralPath $sourceRuntime -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
    throw 'Refusing a source runtime containing redirected entries.'
}
Get-ChildItem -LiteralPath $sourceRuntime -Force | Copy-Item -Destination $isolatedRuntime -Recurse -Force

# Use the authored application resources with compiled product dictionaries and page XBF.
# Product App startup is not run, and no XAML/code for NetMap is duplicated here.
[xml]$appXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'src\settings-ui\Settings.UI\SettingsXAML\App.xaml') -Raw
$dictionary = $appXaml.DocumentElement.SelectSingleNode('*[local-name()="Application.Resources"]/*[local-name()="ResourceDictionary"]')
foreach ($attribute in $appXaml.DocumentElement.Attributes) {
    if ($attribute.Name -eq 'xmlns' -or $attribute.Prefix -eq 'xmlns') { $dictionary.SetAttribute($attribute.Name, $attribute.Value) }
}
[IO.File]::WriteAllText((Join-Path $reportRoot 'application-resources.xaml'), $dictionary.OuterXml, [Text.UTF8Encoding]::new($false))

$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
$sdk = & dotnet --list-sdks | ForEach-Object {
    if ($_ -match '^(10\.0\.\d+) \[(.+)\]$') { [pscustomobject]@{ Version = [version]$Matches[1]; Root = Join-Path $Matches[2] $Matches[1] } }
} | Sort-Object Version -Descending | Select-Object -First 1
if (-not $sdk) { throw '.NET 10 SDK is required.' }
$hostPack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $hostPack) { throw '.NET 10 x64 apphost pack is required.' }
$testName = 'NetMapVisualSmoke'
$compilerArgs = [Collections.Generic.List[string]]::new()
foreach ($argument in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($argument) }
$compilerArgs.Add('/out:"' + (Join-Path $isolatedRuntime "$testName.dll") + '"')
$compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'NetMapVisualSmoke.cs') + '"')
foreach ($file in Get-ChildItem -LiteralPath $isolatedRuntime -Filter '*.dll' -File) {
    # Settings owns the typography type used by this page; Quick Access links its own copy.
    if ($file.Name -eq 'Kit.QuickAccess.dll') { continue }
    try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $compilerArgs.Add('/reference:"' + $file.FullName + '"')
}
$responseFile = Join-Path $reportRoot 'visual-csc.rsp'
[IO.File]::WriteAllLines($responseFile, $compilerArgs)
& dotnet (Join-Path $sdk.Root 'Roslyn\bincore\csc.dll') ('@' + $responseFile) 2>&1 | Tee-Object -FilePath (Join-Path $reportRoot 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'Visual smoke compilation failed; see compile.log.' }
[Reflection.Assembly]::LoadFrom((Join-Path $sdk.Root 'Microsoft.NET.HostModel.dll')) | Out-Null
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost(
    (Join-Path $hostPack.FullName 'runtimes\win-x64\native\apphost.exe'),
    (Join-Path $isolatedRuntime "$testName.exe"), "$testName.dll", $true,
    (Join-Path $isolatedRuntime 'Kit.Settings.exe'), $false, $false, $null)
& icacls.exe (Join-Path $isolatedRuntime "$testName.exe") /setintegritylevel M /L | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not set the harness executable integrity.' }
foreach ($extension in @('.deps.json', '.runtimeconfig.json', '.pri')) {
    Copy-Item -LiteralPath (Join-Path $isolatedRuntime ('Kit.Settings' + $extension)) -Destination (Join-Path $isolatedRuntime ($testName + $extension))
}

$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $isolatedRuntime "$testName.exe"))
$start.WorkingDirectory = $isolatedRuntime
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.Environment['KIT_NETMAP_VISUAL_OUTPUT'] = $reportRoot
$start.Environment['KIT_NETMAP_VISUAL_LANGUAGE'] = $Language
$start.Environment['KIT_NETMAP_VISUAL_FONT_SIZE'] = [string]$FontSize
$start.Environment['KIT_NETMAP_MANUAL_VISUAL_SECONDS'] = [string]$ManualVisualSeconds
$start.Environment['TEMP'] = Join-Path $reportRoot 'tmp'
$start.Environment['TMP'] = $start.Environment['TEMP']
[IO.Directory]::CreateDirectory($start.Environment['TEMP']) | Out-Null
$testProcess = $null
try {
    $testProcess = [Diagnostics.Process]::Start($start)
    Write-Host "Visual harness PID: $($testProcess.Id); current Kit processes are untouched."
    $processTimeoutSeconds = $TimeoutSeconds + $ManualVisualSeconds
    if (-not $testProcess.WaitForExit($processTimeoutSeconds * 1000)) { throw "Visual smoke timed out after $processTimeoutSeconds seconds (including $ManualVisualSeconds seconds of manual preview)." }
    $log = Join-Path $reportRoot 'visual-smoke.log'
    if (-not (Test-Path -LiteralPath $log)) { throw 'Harness exited before creating its diagnostic log.' }
    Get-Content -LiteralPath $log
    if ($testProcess.ExitCode -ne 0 -or (Get-Content -LiteralPath $log -Tail 1) -ne 'PASS') { throw "Visual smoke failed with exit $($testProcess.ExitCode)." }
} finally {
    # Kill only the process created above; never search by image name or touch Kit.
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit(5000) | Out-Null }
    Write-Host "Screenshots, logs and isolated runtime retained: $reportRoot"
}
