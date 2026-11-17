$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
Push-Location $repoRoot
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
        throw 'IPC smoke requires the Visual Studio x64 target tools.'
    }
    [xml]$repoProps = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $wilVersion = $repoProps.SelectSingleNode('/Project/PropertyGroup/WilVersion').InnerText
    $wilInc = Join-Path $repoRoot "packages/Microsoft.Windows.ImplementationLibrary.$wilVersion/include"
    if (-not (Test-Path -LiteralPath (Join-Path $wilInc 'wil/resource.h') -PathType Leaf)) {
        throw "Restore the repository's WIL package before running IPC smoke: $wilInc"
    }
    if (-not $env:WindowsSdkDir -or -not $env:WindowsSDKVersion) {
        throw 'The Visual Studio environment did not select a Windows SDK.'
    }
    $sdkVersion = $env:WindowsSDKVersion.TrimEnd('\', '/')
    $cppWinRtInc = Join-Path $env:WindowsSdkDir "Include/$sdkVersion/cppwinrt"
    if (-not (Test-Path -LiteralPath (Join-Path $cppWinRtInc 'winrt/base.h') -PathType Leaf)) {
        throw "The selected Windows SDK has no C++/WinRT headers: $cppWinRtInc"
    }
    $clArgs = @('/nologo', '/EHsc', '/std:c++20', '/DUNICODE', '/D_UNICODE')
    $clArgs += @("/I$wilInc", "/I$cppWinRtInc")
    $clArgs += @(
        'tools/tests/NativeModules/PipeMessageSmoke.cpp',
        'src/common/interop/two_way_pipe_message_ipc.cpp',
        'src/common/interop/pipe_caller_auth.cpp',
        '/FoTestResults/PluginLifecycle/',
        '/FeTestResults/PluginLifecycle/PipeMessageSmoke.exe',
        '/link',
        '/MACHINE:X64',
        'Advapi32.lib',
        'RuntimeObject.lib',
        'Ole32.lib',
        'OleAut32.lib'
    )
    New-Item -ItemType Directory -Path TestResults/PluginLifecycle -Force | Out-Null
    & cl.exe @clArgs
    if ($LASTEXITCODE -ne 0) { throw 'IPC smoke test compilation failed.' }
    & ./TestResults/PluginLifecycle/PipeMessageSmoke.exe
    if ($LASTEXITCODE -ne 0) { throw 'Ordered IPC delivery failed.' }
    & ./TestResults/PluginLifecycle/PipeMessageSmoke.exe --delayed-server
    if ($LASTEXITCODE -ne 0) { throw 'Delivery during pipe startup failed.' }
} finally { Pop-Location }
