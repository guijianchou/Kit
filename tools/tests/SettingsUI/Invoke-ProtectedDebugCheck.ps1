#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScriptPath,
    [string[]]$ScriptArguments = @(),
    [Parameter(Mandatory)][string]$OutputLog,
    [switch]$NormalizeAppearance,
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 900
)

# Outer protection for one existing UI wrapper. Its own fixture remains useful;
# this scope also covers constructor migrations and verifies the original bytes.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AiHubDerivedStateFixture.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$testRoot = Join-Path $repoRoot 'tools\tests'
$reportBoundary = Join-Path $repoRoot 'TestResults'
$ScriptPath = [IO.Path]::GetFullPath($ScriptPath)
$OutputLog = [IO.Path]::GetFullPath($OutputLog)
$dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Kit'))
$localserverRoot = Join-Path $dataRoot 'Localserver'

function Assert-ProtectedPath([string]$Path, [string]$Root, [switch]$File) {
    $full = Assert-AiHubFixturePath $Path $Root
    if ($File -and (Test-Path -LiteralPath $full) -and (Get-Item -LiteralPath $full -Force).PSIsContainer) {
        throw 'Expected a regular fixture file.'
    }
    return $full
}

$null = Assert-ProtectedPath $ScriptPath $testRoot -File
$null = Assert-ProtectedPath $OutputLog $reportBoundary -File
$allowedScripts = @(
    'SettingsUI\Invoke-AiHubUxSmoke.ps1', 'SettingsUI\Invoke-ModulePageSmoke.ps1',
    'SettingsUI\Invoke-TypographySmoke.ps1', 'SettingsUI\Invoke-DashboardPageSmoke.ps1',
    'SettingsUI\Invoke-NavigationPerformanceSmoke.ps1', 'SettingsUI\Invoke-WingetPageSmoke.ps1',
    'SettingsUI\Invoke-QuickAccessTypographySmoke.ps1',
    'SettingsUI\Invoke-SettingsUnitTests.ps1', 'NetMap\Invoke-NetMapPageSmoke.ps1'
)
if ([IO.Path]::GetRelativePath($testRoot, $ScriptPath) -notin $allowedScripts -or -not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
    throw 'Select one of the supported Settings or NetMap UI wrappers.'
}
if (Test-Path -LiteralPath $OutputLog) { throw 'Use a new OutputLog for each protected run.' }
if (@($ScriptArguments | Where-Object { $_ -match '^-PluginFilter(?:$|:)' }).Count) {
    throw 'This wrapper supports scalar/switch arguments. Run the complete Plugins suite instead of forwarding a PluginFilter array through pwsh -File.'
}

function Assert-KitAndHarnessesStopped {
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -eq 'Kit' -or $_.ProcessName -like 'Kit.*' -or $_.ProcessName -eq 'winget' -or
        $_.ProcessName -match '^(?:Settings\.UI\.UnitTests|.*PageSmoke|AiHubUxSmoke|TypographySmoke|QuickAccessTypographySmoke|NavigationPerformanceSmoke|NetMapVisualSmoke)$'
    })
    if ($running.Count) { throw 'Close Kit, its workers and other UI harnesses before this protected check.' }
}

function Get-LocalserverFixtureFiles {
    foreach ($specification in @(
        @('', '^(?:services\.json|hub\.settings\.json|secrets\.dat)(?:\.\d+\.[a-fA-F0-9]{32}\.tmp)?$'),
        @('services.d', '^.+\.json(?:\.\d+\.[a-fA-F0-9]{32}\.tmp)?$'),
        @('backups', '^services\..+\.json$'),
        @('State', '^.+$')
    )) {
        $directory = if ($specification[0]) { Join-Path $localserverRoot $specification[0] } else { $localserverRoot }
        $null = Assert-ProtectedPath $directory $dataRoot
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        if (-not (Get-Item -LiteralPath $directory -Force).PSIsContainer) { throw 'Expected a Localserver fixture directory.' }
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Name -match $specification[1]) {
                $null = Assert-ProtectedPath $item.FullName $dataRoot -File
                $item.FullName
            }
        }
    }
}

function Get-AiHubHistoryTemporaryFiles {
    $directory = Join-Path $dataRoot 'AiHub\State'
    $null = Assert-ProtectedPath $directory $dataRoot
    if (-not (Test-Path -LiteralPath $directory)) { return }
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if ($item.Name -match '^(?:audit_history|optimization_history)\.json\.[a-fA-F0-9]{32}\.tmp$') {
            $null = Assert-ProtectedPath $item.FullName $dataRoot -File
            $item.FullName
        }
    }
}

function Write-ProtectedRecovery {
    $recovery = $OutputLog + '.recovery.dat'
    $payload = $null
    try {
        $payload = [Text.Encoding]::UTF8.GetBytes([Management.Automation.PSSerializer]::Serialize(@{
            Files = $backups; Derived = $derivedSnapshot; Directories = $directories;
            LocalserverFiles = $localserverOriginal; HistoryTemporaryFiles = $historyTemporaryOriginal
        }))
        $encrypted = [Security.Cryptography.ProtectedData]::Protect($payload, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes($recovery, $encrypted)
        [IO.File]::AppendAllText($OutputLog, "`nRESTORE FAIL: $($restoreFailures -join ' ')`nEncrypted recovery: $recovery`n")
    } catch { $restoreFailures.Add('Could not write encrypted recovery snapshot.') }
    finally { if ($null -ne $payload) { [Array]::Clear($payload, 0, $payload.Length) } }
}

function Save-ProtectedFile([string]$Path) {
    $full = Assert-ProtectedPath $Path $dataRoot -File
    if (-not $backups.ContainsKey($full)) {
        $backups[$full] = $null
        # Assign directly: a zero-byte array emitted from an if block becomes $null in PowerShell.
        if (Test-Path -LiteralPath $full) { $backups[$full] = [IO.File]::ReadAllBytes($full) }
    }
    for ($directory = [IO.Path]::GetDirectoryName($full); $directory; $directory = [IO.Path]::GetDirectoryName($directory)) {
        if ($directory -eq $dataRoot -or $directory.StartsWith($dataRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            if (-not $directories.ContainsKey($directory)) { $directories[$directory] = Test-Path -LiteralPath $directory }
        } else { break }
    }
}

function Write-ProtectedText([string]$Path, [string]$Text) {
    $null = Assert-ProtectedPath $Path $dataRoot -File
    if (-not $backups.ContainsKey($Path)) { throw 'Refusing to replace a file outside the original snapshot.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

$mutex = [Threading.Mutex]::new($false, 'Local\Kit.ProtectedDebugCheck')
$ownsMutex = $false
$backups = @{}
$directories = @{}
$localserverOriginal = @()
$historyTemporaryOriginal = @()
$derivedSnapshot = $null
$modified = $false
$child = $null
$stdout = $null
$stderr = $null
$childOutputSaved = $false
$failure = $null
$restoreFailures = [Collections.Generic.List[string]]::new()
try {
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another protected UI check is already running.' }
    Assert-KitAndHarnessesStopped
    $null = Assert-ProtectedPath $dataRoot $dataRoot
    if (Test-Path -LiteralPath (Join-Path $dataRoot 'AiHub\.settings-transaction.dat')) {
        throw 'An unfinished AI Hub settings transaction must be recovered by Kit before testing.'
    }
    foreach ($relative in @(
        'settings.json', 'settings-ui.json', 'settings-placement.json', 'language.json', 'log_settings.json',
        'Awake\settings.json', 'LightSwitch\settings.json', 'Localserver\settings.json',
        'NetMap\settings.json', 'NetMap\mtr-latest.json', 'UDPtest\settings.json', 'UDPtest\targets.json',
        'Winget\settings.json', 'Winget\State\service-state.json',
        'AiHub\settings.json', 'AiHub\service-settings.json', 'AiHub\secrets.dat', 'AiHub\security.md',
        'AiHub\.settings-transaction.dat', 'AiHub\chains\security-audit\AGENTS.md',
        'AiHub\chains\system-optimization\AGENTS.md', 'AiHub\chains\self-test\AGENTS.md',
        'AiHub\State\audit_history.json', 'AiHub\State\optimization_history.json',
        'Localserver\services.json', 'Localserver\hub.settings.json', 'Localserver\secrets.dat'
    )) { Save-ProtectedFile (Join-Path $dataRoot $relative) }
    $localserverOriginal = @(Get-LocalserverFixtureFiles)
    foreach ($path in $localserverOriginal) { Save-ProtectedFile $path }
    $historyTemporaryOriginal = @(Get-AiHubHistoryTemporaryFiles)
    foreach ($path in $historyTemporaryOriginal) { Save-ProtectedFile $path }
    foreach ($name in @('services.d', 'backups', 'State')) {
        $directory = Join-Path $localserverRoot $name
        if (-not $directories.ContainsKey($directory)) { $directories[$directory] = Test-Path -LiteralPath $directory }
    }
    $derivedSnapshot = New-AiHubDerivedStateSnapshot (Join-Path $dataRoot 'AiHub\State')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputLog)) | Out-Null
    [IO.File]::WriteAllText($OutputLog, "Protected Debug check: $([IO.Path]::GetFileName($ScriptPath))`n", [Text.UTF8Encoding]::new($false))

    Assert-KitAndHarnessesStopped
    $modified = $true
    Clear-AiHubDerivedStateFixture $derivedSnapshot
    foreach ($path in $historyTemporaryOriginal) { Remove-Item -LiteralPath $path -Force }
    $generalPath = Join-Path $dataRoot 'settings.json'
    $general = if ($null -ne $backups[$generalPath]) { [Text.Encoding]::UTF8.GetString($backups[$generalPath]) | ConvertFrom-Json -AsHashtable } else { @{} }
    if ($general -isnot [Collections.IDictionary]) { throw 'General settings must be a JSON object.' }
    if ($general['enabled'] -isnot [Collections.IDictionary]) { $general['enabled'] = @{} }
    foreach ($module in @('Awake', 'LightSwitch', 'Localserver', 'UDPtest', 'NetMap', 'AIHub', 'Winget')) { $general['enabled'][$module] = $false }
    if ($NormalizeAppearance) { $general['ui_font_family'] = 'system'; $general['ui_font_size'] = 14 }
    Write-ProtectedText $generalPath ($general | ConvertTo-Json -Depth 100)
    Write-ProtectedText (Join-Path $dataRoot 'AiHub\service-settings.json') '{"isEnabled":false,"selectedKernel":"codex","targets":[{"name":"Main","isActive":false},{"name":"Fallback","isActive":false}]}'
    foreach ($relative in @('AiHub\State\audit_history.json', 'AiHub\State\optimization_history.json')) {
        Write-ProtectedText (Join-Path $dataRoot $relative) '[]'
    }
    foreach ($relative in @('AiHub\security.md', 'AiHub\chains\security-audit\AGENTS.md', 'AiHub\chains\system-optimization\AGENTS.md', 'AiHub\chains\self-test\AGENTS.md')) {
        # Let production create its current valid defaults. An invented policy body
        # can fail schema validation or trigger migrations unrelated to the UI check.
        $policyPath = Join-Path $dataRoot $relative
        if (Test-Path -LiteralPath $policyPath) { Remove-Item -LiteralPath $policyPath -Force }
    }
    $secretPath = Join-Path $dataRoot 'AiHub\secrets.dat'
    if (Test-Path -LiteralPath $secretPath) { Remove-Item -LiteralPath $secretPath -Force }
    foreach ($path in $localserverOriginal) { Remove-Item -LiteralPath $path -Force }
    Write-ProtectedText (Join-Path $localserverRoot 'services.json') '{"version":2,"services":[]}'
    Write-ProtectedText (Join-Path $localserverRoot 'hub.settings.json') '{}'
    Write-ProtectedText (Join-Path $dataRoot 'UDPtest\targets.json') '{}'

    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $ScriptArguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $child.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline) { $null = $child.WaitForExit(1000) }
    if (-not $child.HasExited) {
        $child.Kill($true)
        if (-not $child.WaitForExit(10000)) { throw 'The owned child process tree did not exit after termination.' }
        throw 'The protected child wrapper exceeded its time limit.'
    }
    [IO.File]::AppendAllText($OutputLog, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
    $childOutputSaved = $true
    if ($child.ExitCode -ne 0) { throw "The child wrapper failed with exit code $($child.ExitCode); see OutputLog." }
    Assert-KitAndHarnessesStopped
} catch {
    $failure = $_
} finally {
    $safeToRestore = $true
    if ($child -and -not $child.HasExited) {
        try {
            $child.Kill($true)
            if (-not $child.WaitForExit(10000)) { throw 'Child process remains active.' }
        } catch {
            $safeToRestore = $false
            $restoreFailures.Add('Could not stop the owned child process tree; shared files were left as fixtures to avoid racing live writers.')
        }
    }
    if (-not $childOutputSaved -and $stdout -and $stderr -and $stdout.IsCompleted -and $stderr.IsCompleted) {
        try { [IO.File]::AppendAllText($OutputLog, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()) }
        catch { $restoreFailures.Add('Could not preserve child failure diagnostics.') }
    }
    if ($modified -and $safeToRestore) {
        try { Assert-KitAndHarnessesStopped }
        catch {
            $safeToRestore = $false
            $restoreFailures.Add('A Kit worker or UI harness is still active; restoration was deferred and an encrypted recovery snapshot was retained.')
        }
    }
    if ($modified -and $safeToRestore) {
        try {
            foreach ($path in @(Get-LocalserverFixtureFiles)) {
                if ($path -notin $localserverOriginal) { Remove-Item -LiteralPath $path -Force }
            }
        } catch { $restoreFailures.Add('Could not enumerate/remove newly generated Localserver fixture files.') }
        try {
            foreach ($path in @(Get-AiHubHistoryTemporaryFiles)) {
                if ($path -notin $historyTemporaryOriginal) { Remove-Item -LiteralPath $path -Force }
            }
        } catch { $restoreFailures.Add('Could not remove newly generated AI Hub history temporary files.') }
        foreach ($path in $backups.Keys) {
            try {
                $null = Assert-ProtectedPath $path $dataRoot -File
                if ($null -eq $backups[$path]) {
                    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
                } else {
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
                    [IO.File]::WriteAllBytes($path, $backups[$path])
                }
            } catch { $restoreFailures.Add('Could not restore: ' + [IO.Path]::GetRelativePath($dataRoot, $path)) }
        }
        # Original histories are restored first so the helper can remove an originally absent State directory.
        try { Restore-AiHubDerivedStateFixture $derivedSnapshot }
        catch { $restoreFailures.Add('AI Hub derived-state restoration failed: ' + $_.Exception.Message) }
        foreach ($path in $backups.Keys) {
            try {
                $null = Assert-ProtectedPath $path $dataRoot -File
                if ($null -eq $backups[$path]) {
                    if (Test-Path -LiteralPath $path) { throw 'Originally absent file remains.' }
                } elseif (-not [Linq.Enumerable]::SequenceEqual[byte]([byte[]]$backups[$path], [IO.File]::ReadAllBytes($path))) {
                    throw 'Original bytes differ.'
                }
            } catch { $restoreFailures.Add('Verification failed: ' + [IO.Path]::GetRelativePath($dataRoot, $path)) }
        }
        try {
            $actualFiles = @(Get-LocalserverFixtureFiles)
            if ($actualFiles.Count -ne $localserverOriginal.Count -or @($actualFiles | Where-Object { $_ -notin $localserverOriginal }).Count) {
                throw 'Localserver file set differs.'
            }
        } catch { $restoreFailures.Add('Localserver original file set/count was not restored.') }
        try {
            $actualTemporary = @(Get-AiHubHistoryTemporaryFiles)
            if ($actualTemporary.Count -ne $historyTemporaryOriginal.Count -or @($actualTemporary | Where-Object { $_ -notin $historyTemporaryOriginal }).Count) {
                throw 'AI Hub history temporary file set differs.'
            }
        } catch { $restoreFailures.Add('AI Hub history temporary file set/count was not restored.') }
        foreach ($directory in @($directories.Keys | Sort-Object Length -Descending)) {
            if (-not $directories[$directory]) {
                try {
                    $null = Assert-ProtectedPath $directory $dataRoot
                    if ((Test-Path -LiteralPath $directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory }
                } catch { $restoreFailures.Add('Could not remove an originally absent empty fixture directory.') }
            }
        }
        if ($restoreFailures.Count -eq 0) {
            $restored = "RESTORE PASS: $($backups.Count) tracked files and $($localserverOriginal.Count) Localserver catalog/state files; original bytes, absence and derived-file counts verified."
            [IO.File]::AppendAllText($OutputLog, "`n$restored`n")
            Write-Output $restored
        }
    }
    # Preserve recovery bytes without racing live writers if normal restoration was unsafe or failed.
    if ($modified -and $restoreFailures.Count) { Write-ProtectedRecovery }
    if ($child) { $child.Dispose() }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
if ($restoreFailures.Count) { throw ($restoreFailures -join ' ') }
if ($failure) { throw $failure }
Write-Output "Protected check passed. Log: $OutputLog"
