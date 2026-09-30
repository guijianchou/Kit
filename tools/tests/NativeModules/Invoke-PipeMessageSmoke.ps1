$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
Push-Location $repoRoot
try {
    . ./tools/build/build-common.ps1
    if (-not (Ensure-VsDevEnvironment)) { throw 'Visual Studio environment is unavailable.' }
    New-Item -ItemType Directory -Path TestResults/PluginLifecycle -Force | Out-Null
    & cl.exe /nologo /EHsc /std:c++20 /DUNICODE /D_UNICODE tools/tests/NativeModules/PipeMessageSmoke.cpp src/common/interop/two_way_pipe_message_ipc.cpp /FoTestResults/PluginLifecycle/ /FeTestResults/PluginLifecycle/PipeMessageSmoke.exe /link Advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'IPC smoke test compilation failed.' }
    & ./TestResults/PluginLifecycle/PipeMessageSmoke.exe
    if ($LASTEXITCODE -ne 0) { throw 'Ordered IPC delivery failed.' }
    & ./TestResults/PluginLifecycle/PipeMessageSmoke.exe --delayed-server
    if ($LASTEXITCODE -ne 0) { throw 'Delivery during pipe startup failed.' }
} finally { Pop-Location }
