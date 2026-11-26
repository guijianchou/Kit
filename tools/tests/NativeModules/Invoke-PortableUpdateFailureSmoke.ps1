[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$ResultName = 'runner-update-failures'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $SourcePath) { $SourcePath = Join-Path $repoRoot 'src\runner\UpdateUtils.cpp' }
$SourcePath = [IO.Path]::GetFullPath($SourcePath)
if ($ResultName -notmatch '^[a-zA-Z0-9-]+$') { throw 'ResultName must be a simple file name.' }
$output = Join-Path $repoRoot "TestResults\PortableUpdater\$ResultName"
[IO.Directory]::CreateDirectory($output) | Out-Null
$source = [IO.File]::ReadAllText($SourcePath)

# Extract the exact current function definitions, including source-line mapping.
# The helper is indented inside an anonymous namespace; RunPortableUpdate is not.
$functions = @(
    @{ Name = 'record_helper_failure'; Indent = '    ' },
    @{ Name = 'RunPortableUpdate'; Indent = '' }
)
$extracted = foreach ($function in $functions) {
    $indent = [regex]::Escape($function.Indent)
    $pattern = '(?ms)^' + $indent + 'void ' + $function.Name + '\([^\r\n]*\)\r?\n' + $indent + '\{\r?\n.*?^' + $indent + '\}'
    $matches = [regex]::Matches($source, $pattern)
    if ($matches.Count -ne 1) { throw "Expected exactly one complete $($function.Name) definition." }
    $match = $matches[0]
    $line = 1 + ([regex]::Matches($source.Substring(0, $match.Index), "`n")).Count
    '#line ' + $line + ' "' + $SourcePath.Replace('\', '/') + '"'
    $match.Value
}
$include = Join-Path $output 'PortableUpdateFailure.production.inc'
[IO.File]::WriteAllText($include, ($extracted -join "`r`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{
    source = $SourcePath
    sourceSha256 = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash
    extractedSha256 = (Get-FileHash -LiteralPath $include -Algorithm SHA256).Hash
    stateHeaderSha256 = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'src\common\updating\updateState.h') -Algorithm SHA256).Hash
    configuration = 'Debug x64 /MTd /Od /Zi'
    isolation = 'Original Runner functions; real UpdateState type; fixture-only disk adapter; mocked process, clock, COM and badge. Does not test production JSON, locking, or Settings watcher.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'source.json') -Encoding utf8

# Prefer the installed compiler directly, matching other standalone native
# regression wrappers; no solution build, NuGet restore, or application launch.
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise'
if (-not (Test-Path -LiteralPath $vsRoot)) {
    $vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ tools were not found.' }
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw 'Visual Studio C++ tools were not found.' }
}
$toolset = Get-ChildItem (Join-Path $vsRoot 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$sdk = Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $toolset -or -not $sdk) { throw 'The local x64 MSVC toolset and Windows SDK are required.' }
$compilerDirectory = Join-Path $toolset.FullName 'bin\Hostx64\x64'
$compiler = Join-Path $compilerDirectory 'cl.exe'
$executable = Join-Path $output 'PortableUpdateFailureSmoke.exe'
$arguments = @('/nologo', '/std:c++20', '/EHsc', '/W4', '/MTd', '/Od', '/Zi', '/DUNICODE', '/D_UNICODE',
    "/I$(Join-Path $toolset.FullName 'include')", "/I$(Join-Path $sdk.FullName 'ucrt')", "/I$(Join-Path $sdk.FullName 'shared')", "/I$(Join-Path $sdk.FullName 'um')",
    "/I$(Join-Path $repoRoot 'src')", "/I$output", (Join-Path $PSScriptRoot 'PortableUpdateFailureSmoke.cpp'),
    "/Fe$executable", "/Fo$(Join-Path $output 'PortableUpdateFailureSmoke.obj')", "/Fd$(Join-Path $output 'PortableUpdateFailureSmoke.compiler.pdb')",
    '/link', '/SUBSYSTEM:CONSOLE', "/LIBPATH:$(Join-Path $toolset.FullName 'lib\x64')",
    "/LIBPATH:$(Join-Path $sdkRoot ('Lib\' + $sdk.Name + '\ucrt\x64'))", "/LIBPATH:$(Join-Path $sdkRoot ('Lib\' + $sdk.Name + '\um\x64'))")
$previousCompilerPath = $env:Path
try {
    $env:Path = $compilerDirectory + ';' + $previousCompilerPath
    & $compiler @arguments *> (Join-Path $output 'build.log')
} finally {
    $env:Path = $previousCompilerPath
}
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'build.log'); throw 'Portable updater Debug harness compilation failed.' }
$process = Start-Process -FilePath $executable -WorkingDirectory $output -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $output 'stdout.log') -RedirectStandardError (Join-Path $output 'stderr.log')
try {
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
        throw 'Portable updater regression exceeded its 15-second limit.'
    }
    Get-Content (Join-Path $output 'stdout.log')
    Get-Content (Join-Path $output 'stderr.log')
    if ($process.ExitCode -ne 0) { throw "Portable updater regression failed: exit $($process.ExitCode)." }
} finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
