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
$threadEntry = 'std::thread([this]() {'
if (($methods.Split($threadEntry, [StringSplitOptions]::None)).Count -ne 2) {
    throw 'Expected exactly one startup thread entry to gate.'
}
# Pause before the first production lambda statement, including its old lock/access
# to this. This makes the previous detached-thread lifetime race deterministic.
$methods = $methods.Replace($threadEntry, "$threadEntry`r`n            WaitAtStartupGate();")
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.File]::WriteAllText((Join-Path $output 'AIHubLifecycleMethods.generated.h'), $methods)

Push-Location -LiteralPath $repoRoot
try {
    . ./tools/build/build-common.ps1
    if (-not (Ensure-VsDevEnvironment)) { throw 'Visual Studio environment is unavailable.' }
    & cl.exe /nologo /std:c++20 /EHsc /W4 /MT "/I$output" (Join-Path $PSScriptRoot 'AIHubLifecycleSmoke.cpp') "/Fo$output\AIHubLifecycleSmoke.obj" "/Fe$output\AIHubLifecycleSmoke.exe"
    if ($LASTEXITCODE -ne 0) { throw 'AIHub lifecycle smoke compilation failed.' }
    & (Join-Path $output 'AIHubLifecycleSmoke.exe')
    if ($LASTEXITCODE -ne 0) { throw "AIHub lifecycle smoke failed: $LASTEXITCODE" }
}
finally { Pop-Location }
