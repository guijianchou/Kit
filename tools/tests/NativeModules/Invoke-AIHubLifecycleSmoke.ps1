[CmdletBinding()]
param(
    [string]$ModuleSourcePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$output = Join-Path $repoRoot 'TestResults\AIHubLifecycle'
if (-not $ModuleSourcePath) {
    $ModuleSourcePath = Join-Path $repoRoot 'src\modules\AIHub\AIHubModuleInterface\dllmain.cpp'
}
$source = [IO.File]::ReadAllText($ModuleSourcePath)
$methods = foreach ($signature in @('virtual void destroy()', 'virtual void enable()', 'virtual void disable()', 'virtual bool is_enabled()')) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing lifecycle method: $signature" }
    $open = $source.IndexOf('{', $start)
    $depth = 1
    $end = $open + 1
    while ($depth -gt 0 -and $end -lt $source.Length) {
        if ($source[$end] -eq '{') { $depth++ }
        elseif ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    if ($depth -ne 0) { throw "Unbalanced lifecycle method: $signature" }
    $source.Substring($start, $end - $start)
}
$methods = $methods -join "`r`n"
# Exercise the unchanged production bodies on the actual Runner lifecycle queue.
# The harness blocks its inert worker callback; no production source is rewritten
# to introduce a thread or manufacture a lifecycle result.
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.File]::WriteAllText((Join-Path $output 'AIHubLifecycleMethods.generated.h'), $methods)

Push-Location -LiteralPath $repoRoot
try {
    . ./tools/build/build-common.ps1
    if (-not (Ensure-VsDevEnvironment)) { throw 'Visual Studio environment is unavailable.' }
    if ($env:VSCMD_ARG_TGT_ARCH -notin @('x64', 'amd64')) {
        if (-not $env:VSINSTALLDIR) { throw 'The Visual Studio installation directory is unavailable.' }
        $x64DevShell = Join-Path $env:VSINSTALLDIR 'Common7/Tools/Launch-VsDevShell.ps1'
        if (-not (Test-Path -LiteralPath $x64DevShell -PathType Leaf)) {
            throw "Cannot select the Visual Studio x64 tools: $x64DevShell"
        }
        & $x64DevShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
        Normalize-ProcessPathEnvironment
    }
    if ($env:VSCMD_ARG_TGT_ARCH -notin @('x64', 'amd64')) {
        throw 'AIHub lifecycle smoke requires the Visual Studio x64 target tools.'
    }
    & cl.exe /nologo /std:c++20 /EHsc /W4 /MT "/I$output" (Join-Path $PSScriptRoot 'AIHubLifecycleSmoke.cpp') "/Fo$output\AIHubLifecycleSmoke.obj" "/Fe$output\AIHubLifecycleSmoke.exe" /link /MACHINE:X64
    if ($LASTEXITCODE -ne 0) { throw 'AIHub lifecycle smoke compilation failed.' }
    & (Join-Path $output 'AIHubLifecycleSmoke.exe')
    if ($LASTEXITCODE -ne 0) { throw "AIHub lifecycle smoke failed: $LASTEXITCODE" }
}
finally { Pop-Location }
