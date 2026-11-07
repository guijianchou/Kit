#requires -Version 7.0
param(
    [string]$TestExecutable = '',
    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 120,
    [ValidateRange(1, 100000)]
    [int]$MinimumExpectedTests = 252,
    [string]$ReportRoot = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if ([string]::IsNullOrWhiteSpace($TestExecutable)) {
    $TestExecutable = Join-Path $repoRoot 'Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.exe'
}
$TestExecutable = [IO.Path]::GetFullPath($TestExecutable)
if ([string]::IsNullOrWhiteSpace($ReportRoot)) {
    $ReportRoot = Join-Path $repoRoot ('TestResults\SettingsTests\' + [guid]::NewGuid().ToString('N'))
}
$ReportRoot = [IO.Path]::GetFullPath($ReportRoot)

function Assert-RegularFile([string]$Path, [bool]$AllowMissing = $true) {
    $full = [IO.Path]::GetFullPath($Path)
    Assert-NoReparsePoints $full
    if (-not (Test-Path -LiteralPath $full)) {
        if ($AllowMissing) { return }
        throw 'Required Settings test file is missing.'
    }
    $item = Get-Item -LiteralPath $full -Force
    if ($item.PSIsContainer -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'A Settings test input path is not an ordinary file.'
    }
}

function Assert-NoReparsePoints([string]$Path) {
    $component = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($component)) {
        $attributes = [IO.FileInfo]::new($component).Attributes
        if ([int]$attributes -ne -1 -and ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Settings test paths must not contain reparse points.'
        }
        $component = [IO.Path]::GetDirectoryName($component)
    }
}

if (-not (Test-Path -LiteralPath $TestExecutable -PathType Leaf)) {
    throw 'Build the Settings unit-test executable before running this wrapper.'
}
Assert-RegularFile $TestExecutable $false

# The tests construct production view models. Do not race Kit or any module worker while
# temporarily replacing the shared per-user files.
$guardNames = @(
    'Kit', 'Kit.Settings', 'Kit.QuickAccess', 'Kit.AIHubWorker', 'Kit.WingetService',
    'Kit.LocalserverWorker', 'Kit.NetMapWorker', 'Kit.LightSwitchService', 'Kit.Awake',
    'Settings.UI.UnitTests'
)
function Assert-KitStopped([int]$AllowedTestId = -1) {
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Id -ne $AllowedTestId -and ($guardNames -contains $_.ProcessName -or $_.ProcessName -like 'Kit.*' -or
            $_.ProcessName -like '*PageSmoke' -or $_.ProcessName -in @('AiHubUxSmoke', 'NavigationPerformanceSmoke'))
    })
    if ($running.Count -gt 0) {
        throw 'Close Kit, its workers and other Settings harnesses before running Settings unit tests.'
    }
}
Assert-KitStopped

$localKit = Join-Path $env:LOCALAPPDATA 'Kit'
$aiHubRoot = Join-Path $localKit 'AiHub'
$journalPath = Join-Path $aiHubRoot '.settings-transaction.dat'
Assert-NoReparsePoints $aiHubRoot
if (Test-Path -LiteralPath $journalPath) {
    throw 'An unfinished AI Hub settings transaction exists; allow Kit to recover it before testing.'
}
$fileNames = @(
    (Join-Path $localKit 'settings.json'),
    (Join-Path $aiHubRoot 'settings.json'),
    (Join-Path $aiHubRoot 'service-settings.json'),
    (Join-Path $aiHubRoot 'secrets.dat'),
    (Join-Path $aiHubRoot 'security.md'),
    (Join-Path $aiHubRoot 'chains\security-audit\AGENTS.md'),
    (Join-Path $aiHubRoot 'chains\system-optimization\AGENTS.md'),
    (Join-Path $aiHubRoot 'chains\self-test\AGENTS.md'),
    $journalPath
)

$backups = @{}
foreach ($path in $fileNames) {
    Assert-RegularFile $path
    $full = [IO.Path]::GetFullPath($path)
    $backups[$full] = $null
    if (Test-Path -LiteralPath $full) {
        # Keep credentials and policies as byte arrays only; never convert them to text or log them.
        $backups[$full] = [IO.File]::ReadAllBytes($full)
    }
}

Assert-NoReparsePoints $ReportRoot
if (Test-Path -LiteralPath $ReportRoot) { throw 'Use a new report directory so an old TRX cannot be mistaken for this run.' }
$reportDirectory = [IO.Directory]::CreateDirectory($ReportRoot).FullName
$stdoutPath = Join-Path $reportDirectory 'settings-tests.stdout.log'
$stderrPath = Join-Path $reportDirectory 'settings-tests.stderr.log'
$testProcess = $null
$failure = $null
$restoreFailures = [Collections.Generic.List[string]]::new()
$modifiedSharedFiles = $false

function Write-SyntheticGeneralSettings([string]$Path) {
    $value = @{ enabled = @{} }
    foreach ($module in @('Awake', 'LightSwitch', 'Localserver', 'UDPtest', 'NetMap', 'AIHub', 'Winget')) {
        $value['enabled'][$module] = $false
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, ($value | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
}

function Write-SyntheticServiceSettings([string]$Path) {
    $value = @{
        isEnabled = $false
        selectedKernel = 'codex'
        maxConcurrentAnalysis = 2
        scanIntervalHours = 0
        auditModeIndex = 0
        retentionDays = 30
        targets = @(
            @{ name = 'Main'; isActive = $false; baseUrl = ''; mode = 'responses'; model = 'gpt-6-luna'; effort = 'high'; isExpanded = $false },
            @{ name = 'Fallback'; isActive = $false; baseUrl = ''; mode = 'responses'; model = 'gpt-6-luna'; effort = 'high'; isExpanded = $false }
        )
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, ($value | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
}

try {
    # Use a valid, disabled service configuration and no credential file. The global and all
    # task policies are absent during the run, so constructors create isolated defaults.
    Assert-KitStopped
    $modifiedSharedFiles = $true
    Write-SyntheticGeneralSettings (Join-Path $localKit 'settings.json')
    Write-SyntheticServiceSettings (Join-Path $aiHubRoot 'service-settings.json')

    foreach ($path in @(
        (Join-Path $aiHubRoot 'settings.json'), (Join-Path $aiHubRoot 'secrets.dat'),
        (Join-Path $aiHubRoot 'security.md'),
        (Join-Path $aiHubRoot 'chains\security-audit\AGENTS.md'),
        (Join-Path $aiHubRoot 'chains\system-optimization\AGENTS.md'),
        (Join-Path $aiHubRoot 'chains\self-test\AGENTS.md')
    )) {
        Assert-RegularFile $path
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new($TestExecutable)
    $startInfo.WorkingDirectory = Split-Path -Parent $TestExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('--report-trx')
    $startInfo.ArgumentList.Add('--results-directory')
    $startInfo.ArgumentList.Add($reportDirectory)
    $startInfo.ArgumentList.Add('--minimum-expected-tests')
    $startInfo.ArgumentList.Add([string]$MinimumExpectedTests)
    $testProcess = [Diagnostics.Process]::Start($startInfo)

    $stdoutTask = $testProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $testProcess.StandardError.ReadToEndAsync()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $testProcess.HasExited) {
        Assert-KitStopped $testProcess.Id
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Settings unit tests timed out after $TimeoutSeconds seconds."
        }
        $null = $testProcess.WaitForExit(5000)
    }
    $stdoutTask.GetAwaiter().GetResult() | Set-Content -LiteralPath $stdoutPath -Encoding utf8
    $stderrTask.GetAwaiter().GetResult() | Set-Content -LiteralPath $stderrPath -Encoding utf8
    if ($testProcess.ExitCode -ne 0) {
        throw "Settings unit tests failed with exit code $($testProcess.ExitCode)."
    }
    Assert-KitStopped
}
catch {
    $failure = $_
}
finally {
    if ($testProcess -and -not $testProcess.HasExited) {
        try {
            $testProcess.Kill($true)
            if (-not $testProcess.WaitForExit(5000)) { $restoreFailures.Add('The test process did not exit after termination.') }
        } catch { $restoreFailures.Add('Could not terminate the test process before restoration.') }
    }

    # Restore exact bytes or exact absence, including files created by a failed test run.
    if ($modifiedSharedFiles) {
        foreach ($path in $backups.Keys) {
            try {
                Assert-RegularFile $path
                $bytes = $backups[$path]
                if ($null -eq $bytes) {
                    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
                } else {
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
                    Assert-NoReparsePoints $path
                    [IO.File]::WriteAllBytes($path, $bytes)
                }
            } catch { $restoreFailures.Add('Could not restore one shared file; remaining files were still attempted.') }
        }
    }

    foreach ($path in $backups.Keys) {
        $bytes = $backups[$path]
        try {
            Assert-RegularFile $path
            if ($null -eq $bytes) {
                if (Test-Path -LiteralPath $path) { throw 'Unexpected file.' }
            } else {
                $restored = [IO.File]::ReadAllBytes($path)
                try {
                    if ($restored.Length -ne $bytes.Length) { throw 'Wrong length.' }
                    for ($index = 0; $index -lt $bytes.Length; $index++) {
                        if ($restored[$index] -ne $bytes[$index]) { throw 'Wrong byte.' }
                    }
                } finally { [Array]::Clear($restored, 0, $restored.Length) }
            }
        } catch {
            $restoreFailures.Add('Byte-for-byte shared-file restoration verification failed.')
        }
        if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
    }
    if ($testProcess) { $testProcess.Dispose() }
}

if ($restoreFailures.Count -gt 0) { throw ($restoreFailures -join ' ') }
if ($failure) { throw $failure }

$trx = @(Get-ChildItem -LiteralPath $reportDirectory -Filter '*.trx' -File -Recurse)
if ($trx.Count -eq 0) { throw 'Settings unit tests passed without producing a TRX report.' }
$executed = 0
$failed = 0
foreach ($report in $trx) {
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($report.FullName)
    $counters = $document.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    if ($null -eq $counters) { throw 'TRX report does not include test counters.' }
    $executed += [int]$counters.GetAttribute('executed')
    $failed += [int]$counters.GetAttribute('failed')
}
if ($executed -lt $MinimumExpectedTests -or $failed -ne 0) { throw 'TRX counts did not meet the required test result.' }
Write-Output "Settings unit tests passed: executed=$executed; failed=$failed; original shared files restored and verified."
Write-Output "Reports: $reportDirectory"
