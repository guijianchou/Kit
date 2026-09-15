[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$smokeRoot = Join-Path $repoRoot 'x64\Debug\tests\NativeModules'
$sourcePath = Join-Path $PSScriptRoot 'LightSwitchSmoke.cpp'
$fakeDirectory = Join-Path $smokeRoot 'LightSwitchService'

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue) -or $env:VSCMD_ARG_TGT_ARCH -notin @('x64', 'amd64')) {
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Run this script from the Visual Studio x64 Developer PowerShell.'
    }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $installation) {
        throw 'Visual Studio C++ tools were not found.'
    }
    $devShell = Join-Path $installation 'Common7\Tools\Launch-VsDevShell.ps1'
    & $devShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
}

[IO.Directory]::CreateDirectory($fakeDirectory) | Out-Null
$commonArguments = @(
    '/nologo', '/std:c++20', '/EHsc', '/W4', '/MTd', '/Zi',
    '/DUNICODE', '/D_UNICODE',
    "/I$(Join-Path $repoRoot 'src')",
    "/I$(Join-Path $repoRoot 'src\modules')"
)

Push-Location -LiteralPath $smokeRoot
try {
    $harnessArguments = $commonArguments + @(
        $sourcePath,
        "/Fe$(Join-Path $smokeRoot 'LightSwitchSmoke.exe')",
        "/Fo$(Join-Path $smokeRoot 'LightSwitchSmoke.obj')",
        "/Fd$(Join-Path $smokeRoot 'LightSwitchSmoke.compiler.pdb')",
        '/link', '/SUBSYSTEM:CONSOLE', 'RuntimeObject.lib', 'Advapi32.lib', 'Shell32.lib'
    )
    & cl.exe @harnessArguments
    if ($LASTEXITCODE -ne 0) { throw 'The native smoke harness build failed.' }

    $fakeArguments = $commonArguments + @(
        '/DKIT_REVIEW_FAKE_WORKER', $sourcePath,
        "/Fe$(Join-Path $fakeDirectory 'Kit.LightSwitchService.exe')",
        "/Fo$(Join-Path $smokeRoot 'FakeLightSwitchWorker.obj')",
        "/Fd$(Join-Path $smokeRoot 'FakeLightSwitchWorker.compiler.pdb')",
        '/link', '/SUBSYSTEM:WINDOWS', 'Shell32.lib'
    )
    & cl.exe @fakeArguments
    if ($LASTEXITCODE -ne 0) { throw 'The no-window fake worker build failed.' }

    $abiSource = Join-Path $PSScriptRoot 'ModuleAbiSmoke.cpp'
    & cl.exe /nologo /std:c++20 /EHsc /W4 /MTd /Zi /DUNICODE /D_UNICODE /LD /DKIT_UPSTREAM_ABI_FIXTURE "/I$(Join-Path $repoRoot 'source\PowerToys\src')" $abiSource /FoUpstreamFixture.obj /FdUpstreamFixture.pdb /FeUpstreamFixture.dll /link Advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'The upstream ABI fixture build failed.' }
    & cl.exe @commonArguments $abiSource /FoModuleAbiSmoke.obj /FdModuleAbiSmoke.pdb /FeModuleAbiSmoke.exe /link Advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'The Kit ABI host build failed.' }
}
finally {
    Pop-Location
}

Write-Output 'Built the temporary harness and no-window fake worker. No tests were run.'
