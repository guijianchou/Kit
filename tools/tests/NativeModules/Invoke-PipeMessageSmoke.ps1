$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
Push-Location $repoRoot
try {
    . ./tools/build/build-common.ps1
    if (-not (Ensure-VsDevEnvironment)) { throw 'Visual Studio environment is unavailable.' }
    $wilPkg = (Get-ChildItem -Path (Join-Path $repoRoot 'packages') -Directory -Filter "Microsoft.Windows.ImplementationLibrary.*" | Sort-Object Name -Descending | Select-Object -First 1)
    $wilInc = if ($wilPkg) { Join-Path $wilPkg.FullName 'include' } else { '' }
    $clArgs = @('/nologo', '/EHsc', '/std:c++20', '/DUNICODE', '/D_UNICODE')
    if ($wilInc -and (Test-Path $wilInc)) {
        $clArgs += "-I$wilInc"
    }
    $clArgs += @(
        'tools/tests/NativeModules/PipeMessageSmoke.cpp',
        'src/common/interop/two_way_pipe_message_ipc.cpp',
        'src/common/interop/pipe_caller_auth.cpp',
        '/FoTestResults/PluginLifecycle/',
        '/FeTestResults/PluginLifecycle/PipeMessageSmoke.exe',
        '/link',
        'Advapi32.lib',
        'Version.lib'
    )
    New-Item -ItemType Directory -Path TestResults/PluginLifecycle -Force | Out-Null
    & cl.exe @clArgs
    if ($LASTEXITCODE -ne 0) { throw 'IPC smoke test compilation failed.' }
    & ./TestResults/PluginLifecycle/PipeMessageSmoke.exe
    if ($LASTEXITCODE -ne 0) { throw 'Ordered IPC delivery failed.' }
    & ./TestResults/PluginLifecycle/PipeMessageSmoke.exe --delayed-server
    if ($LASTEXITCODE -ne 0) { throw 'Delivery during pipe startup failed.' }
} finally { Pop-Location }
