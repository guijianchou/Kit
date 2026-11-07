#requires -Version 7.0
[CmdletBinding()]
param([string]$VisualStudioPath)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$reportRoot = Join-Path $repoRoot ('TestResults\Logging\' + [guid]::NewGuid().ToString('N'))
$managedRoot = Join-Path $reportRoot 'Managed'
$nativeRoot = Join-Path $reportRoot 'Native'
[IO.Directory]::CreateDirectory($managedRoot) | Out-Null
[IO.Directory]::CreateDirectory($nativeRoot) | Out-Null

# Both tests compile production logger sources with only their platform/settings providers
# replaced. No real user profile, settings files, or logger cleanup directories are used.
$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
$sdkVersion = (& dotnet --version).Trim()
$sdkRoot = Join-Path $dotnetRoot "sdk\$sdkVersion"
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $referencePack) { throw '.NET 10 reference pack is required.' }
$managedDll = Join-Path $managedRoot 'ManagedLoggerSmoke.dll'
$compilerArgs = [Collections.Generic.List[string]]::new()
foreach ($arg in @('/nologo', '/nostdlib+', '/target:exe', '/langversion:preview', '/define:TRACE', '/nullable:disable')) { $compilerArgs.Add($arg) }
$compilerArgs.Add('/out:"' + $managedDll + '"')
$compilerArgs.Add('"' + (Join-Path $PSScriptRoot 'ManagedLoggerSmoke.cs') + '"')
$compilerArgs.Add('"' + (Join-Path $repoRoot 'src\common\ManagedCommon\Logger.cs') + '"')
foreach ($file in Get-ChildItem (Join-Path $referencePack.FullName 'ref\net10.0') -Filter '*.dll' -File) {
    $compilerArgs.Add('/reference:"' + $file.FullName + '"')
}
$responseFile = Join-Path $managedRoot 'compile.rsp'
[IO.File]::WriteAllLines($responseFile, $compilerArgs)
& dotnet (Join-Path $sdkRoot 'Roslyn\bincore\csc.dll') ('@' + $responseFile)
if ($LASTEXITCODE -ne 0) { throw 'Managed logger harness compilation failed.' }
[IO.File]::WriteAllText((Join-Path $managedRoot 'ManagedLoggerSmoke.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
& dotnet $managedDll $managedRoot
if ($LASTEXITCODE -ne 0) { throw 'Managed logger path regression failed.' }

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue) -or $env:VSCMD_ARG_TGT_ARCH -notin @('x64', 'amd64')) {
    $installation = $VisualStudioPath
    if (-not $installation) {
        $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
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
        $developerEnvironment = $devProcess.StandardOutput.ReadToEnd() -split '\r?\n'
        $devProcess.WaitForExit()
        if ($devProcess.ExitCode -ne 0) { throw 'Could not initialize the Visual Studio compiler environment.' }
        foreach ($line in $developerEnvironment) {
            if ($line -match '^([^=]+)=(.*)$') {
                [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
            }
        }
    }
}
$nativeExe = Join-Path $nativeRoot 'NativeLoggerSmoke.exe'
Push-Location -LiteralPath $nativeRoot
try {
    & cl.exe /nologo /std:c++20 /EHsc /W4 /MT /utf-8 /DUNICODE /D_UNICODE /DSPDLOG_WCHAR_TO_UTF8_SUPPORT /DSPDLOG_WCHAR_FILENAMES "/I$(Join-Path $repoRoot 'src')" "/I$(Join-Path $repoRoot 'deps\spdlog\include')" (Join-Path $PSScriptRoot 'NativeLoggerSmoke.cpp') (Join-Path $repoRoot 'src\common\logger\logger.cpp') "/Fe$nativeExe" /link /SUBSYSTEM:CONSOLE
    if ($LASTEXITCODE -ne 0) { throw 'Native logger harness compilation failed.' }
    $startInfo = [Diagnostics.ProcessStartInfo]::new($nativeExe)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($nativeRoot)
    $startInfo.Environment['USERPROFILE'] = Join-Path $nativeRoot 'Profile'
    $startInfo.Environment['LOCALAPPDATA'] = Join-Path $nativeRoot 'Local'
    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Native logger harness timed out.' }
    Write-Output $process.StandardOutput.ReadToEnd()
    Write-Output $process.StandardError.ReadToEnd()
    if ($process.ExitCode -ne 0) { throw 'Native logger path regression failed.' }
} finally {
    Pop-Location
}
Write-Output "PASS logger path regression; isolated artifacts: $reportRoot"
