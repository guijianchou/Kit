[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$ResultName = 'settings-activation'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $SourcePath) { $SourcePath = Join-Path $repoRoot 'src\runner\settings_window.cpp' }
$SourcePath = [IO.Path]::GetFullPath($SourcePath)
if ($ResultName -notmatch '^[a-zA-Z0-9-]+$') { throw 'ResultName must be a simple file name.' }
$output = Join-Path $repoRoot "TestResults\WinGetNotificationCrash\$ResultName"
[IO.Directory]::CreateDirectory($output) | Out-Null
$source = [IO.File]::ReadAllText($SourcePath)

# Both production functions use unindented top-level closing braces. Match the
# precise named definition through that boundary; preserve every function byte.
$extracted = foreach ($name in @('bring_settings_to_front', 'open_settings_window')) {
    $pattern = '(?ms)^void ' + $name + '\([^\r\n]*\)\r?\n\{\r?\n.*?^\}'
    $matches = [regex]::Matches($source, $pattern)
    if ($matches.Count -ne 1) { throw "Expected exactly one complete $name definition." }
    $match = $matches[0]
    $line = 1 + ([regex]::Matches($source.Substring(0, $match.Index), "`n")).Count
    '#line ' + $line + ' "' + $SourcePath.Replace('\', '/') + '"'
    $match.Value
}
$include = Join-Path $output 'SettingsActivation.production.inc'
[IO.File]::WriteAllText($include, ($extracted -join "`r`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{
    source = $SourcePath
    sourceSha256 = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash
    extractedSha256 = (Get-FileHash -LiteralPath $include -Algorithm SHA256).Hash
    configuration = 'Debug x64 /MTd /Od /Zi'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'source.json') -Encoding utf8

# The local VS installation can be present while Setup/DevShell discovery is
# unavailable. Resolve its actual compiler and Windows SDK without modifying it.
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise'
$toolset = Get-ChildItem (Join-Path $vsRoot 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$sdk = Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $toolset -or -not $sdk) { throw 'The local x64 MSVC toolset and Windows SDK are required.' }
$compilerDirectory = Join-Path $toolset.FullName 'bin\Hostx64\x64'
$compiler = Join-Path $compilerDirectory 'cl.exe'
$executable = Join-Path $output 'SettingsActivationSmoke.exe'
$arguments = @('/nologo', '/std:c++20', '/EHsc', '/W4', '/MTd', '/Od', '/Zi', '/DUNICODE', '/D_UNICODE',
    "/I$(Join-Path $toolset.FullName 'include')", "/I$(Join-Path $sdk.FullName 'ucrt')", "/I$(Join-Path $sdk.FullName 'shared')", "/I$(Join-Path $sdk.FullName 'um')",
    "/I$(Join-Path $repoRoot 'src')", "/I$output", (Join-Path $PSScriptRoot 'SettingsActivationSmoke.cpp'),
    "/Fe$executable", "/Fo$(Join-Path $output 'SettingsActivationSmoke.obj')", "/Fd$(Join-Path $output 'SettingsActivationSmoke.compiler.pdb')",
    '/link', '/SUBSYSTEM:CONSOLE', "/LIBPATH:$(Join-Path $toolset.FullName 'lib\x64')",
    "/LIBPATH:$(Join-Path $sdkRoot ('Lib\' + $sdk.Name + '\ucrt\x64'))", "/LIBPATH:$(Join-Path $sdkRoot ('Lib\' + $sdk.Name + '\um\x64'))", 'User32.lib')
$previousCompilerPath = $env:Path
try {
    $env:Path = $compilerDirectory + ';' + $previousCompilerPath
    & $compiler @arguments *> (Join-Path $output 'build.log')
} finally {
    $env:Path = $previousCompilerPath
}
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'build.log'); throw 'Settings activation Debug harness compilation failed.' }
$process = Start-Process -FilePath $executable -WorkingDirectory $output -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $output 'stdout.log') -RedirectStandardError (Join-Path $output 'stderr.log')
try {
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
        throw 'Settings activation hung for 15 seconds (possible lock deadlock).'
    }
    Get-Content (Join-Path $output 'stdout.log')
    Get-Content (Join-Path $output 'stderr.log')
    if ($process.ExitCode -ne 0) { throw "Settings activation regression failed: exit $($process.ExitCode)." }
} finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
