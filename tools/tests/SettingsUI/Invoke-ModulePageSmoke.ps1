param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$runtimeRoot = Join-Path $repoRoot "x64\$Configuration\WinUI3Apps"
$reportRoot = Join-Path $repoRoot 'TestResults\PluginLifecycle'
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
$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
$sdkVersion = (& dotnet --version).Trim()
$sdkRoot = Join-Path $dotnetRoot "sdk\$sdkVersion"
$hostPack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $hostPack) { throw '.NET 10 x64 apphost pack is required.' }
$backups = @{}
foreach ($name in @('settings.json', 'settings-placement.json', 'log_settings.json', 'AiHub\settings.json', 'AiHub\service-settings.json', 'AiHub\secrets.dat', 'AiHub\security.md', 'AiHub\chains\security-audit\AGENTS.md', 'AiHub\chains\system-optimization\AGENTS.md', 'AiHub\chains\self-test\AGENTS.md')) {
    $path = Join-Path $env:LOCALAPPDATA "Kit\$name"
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
$previousOutput = $env:KIT_MODULE_PAGE_TEST_OUTPUT
$testProcess = $null
try {
    $compilerArgs = [Collections.Generic.List[string]]::new()
    foreach ($arg in @('/nostdlib+', '/target:exe', '/langversion:preview')) { $compilerArgs.Add($arg) }
    $compilerArgs.Add('/out:"' + (Join-Path $runtimeRoot "$testName.dll") + '"')
    $compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'ModulePageSmoke.cs') + '"')
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
    foreach ($extension in @('.deps.json', '.runtimeconfig.json', '.pri')) {
        Copy-Item -LiteralPath (Join-Path $runtimeRoot ('Kit.Settings' + $extension)) -Destination (Join-Path $runtimeRoot ($testName + $extension))
    }
    $env:KIT_MODULE_PAGE_TEST_OUTPUT = $reportRoot
    # Release Settings requires the Runner launch contract; use private, unused pipes
    # because the harness supplies its own in-memory replies after startup.
    $pipeSuffix = [guid]::NewGuid().ToString('N')
    $launchArguments = @("KitPageSmokeRunner-$pipeSuffix", "KitPageSmokeSettings-$pipeSuffix", "$PID", 'system', 'false', 'false', 'false')
    $testProcess = Start-Process -FilePath (Join-Path $runtimeRoot "$testName.exe") -ArgumentList $launchArguments -WorkingDirectory $runtimeRoot -WindowStyle Hidden -PassThru
    if (-not $testProcess.WaitForExit(45000)) { throw 'Page smoke test timed out.' }
    Get-Content -LiteralPath (Join-Path $reportRoot 'module-pages-smoke.log')
    if ($testProcess.ExitCode -ne 0) { throw "Page smoke test failed: $($testProcess.ExitCode)" }
    if ((Get-Content -LiteralPath (Join-Path $reportRoot 'module-pages-smoke.log') -Tail 1) -ne 'PASS') {
        throw 'Page smoke test exited before completing its assertions.'
    }
} finally {
    if ($testProcess -and -not $testProcess.HasExited) { $testProcess.Kill(); $testProcess.WaitForExit() }
    $env:KIT_MODULE_PAGE_TEST_OUTPUT = $previousOutput
    foreach ($path in $backups.Keys) {
        if ($null -ne $backups[$path]) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in $temporaryFiles) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}
