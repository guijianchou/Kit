#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$RuntimeRoot,
    [switch]$TraceService,
    [ValidateRange(15, 300)]
    [int]$CheckTimeoutSeconds = 120,
    [ValidateRange(10, 120)]
    [int]$DiscoverTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration" }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$serviceExe = Join-Path $RuntimeRoot 'Kit.WingetService.exe'
if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) { throw 'Build WingetService before running this smoke test.' }
if (@(Get-Process Kit,Kit.Settings,Kit.WingetService -ErrorAction SilentlyContinue).Count) { throw 'Close Kit and the Winget service before this test.' }
$reportRoot = Join-Path $repoRoot ('TestResults\WingetService\' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$report = Join-Path $reportRoot 'service-smoke.log'
$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kit\Winget'
$settingsPath = Join-Path $dataRoot 'settings.json'
$statePath = Join-Path $dataRoot 'State\service-state.json'
$logSettingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kit\log_settings.json'
$backups = @{}
foreach ($path in @($settingsPath, $statePath) + $(if ($TraceService) { @($logSettingsPath) } else { @() })) {
    $backups[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
$serviceProcess = $null
$parentProcess = $null
$exitEvent = $null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WingetSmokePipeIdentity
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
}
'@

function Write-Result([string]$text) {
    [IO.File]::AppendAllText($report, $text + [Environment]::NewLine)
    Write-Output $text
}

function Send-Command([ValidateSet('get-status', 'check', 'search', 'list-installed', 'details', 'prepare', 'cancel')][string]$command, [hashtable]$payload = @{}) {
    $pipeName = 'Kit_Winget_Service_' + [Diagnostics.Process]::GetCurrentProcess().SessionId
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $timeout = [Threading.CancellationTokenSource]::new(3000)
    $reader = $null
    $writer = $null
    try {
        [IO.File]::AppendAllText($report, "IPC $command connecting`r`n")
        $pipe.Connect(3000)
        [IO.File]::AppendAllText($report, "IPC $command connected`r`n")
        [uint32]$serverProcessId = 0
        if (-not [WingetSmokePipeIdentity]::GetNamedPipeServerProcessId($pipe.SafePipeHandle.DangerousGetHandle(), [ref]$serverProcessId) -or $serverProcessId -ne $serviceProcess.Id) {
            throw "The Winget pipe is not owned by the controlled service (PID $serverProcessId)."
        }
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $true, 4096, $true)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $request = @{ Version = 1; Command = $command; PayloadJson = ($payload | ConvertTo-Json -Depth 20 -Compress) } | ConvertTo-Json -Compress
        $writer.WriteLineAsync($request).WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()
        [IO.File]::AppendAllText($report, "IPC $command sent`r`n")
        $line = $reader.ReadLineAsync($timeout.Token).AsTask().WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()
        [IO.File]::AppendAllText($report, "IPC $command received`r`n")
        if ([string]::IsNullOrWhiteSpace($line)) { throw 'The service closed its pipe without a response.' }
        $response = $line | ConvertFrom-Json
        if (-not $response.Success) { throw ($response.Error | ConvertTo-Json -Compress) }
        return $response.DataJson | ConvertFrom-Json
    } finally {
        # Closing the pipe first aborts pending I/O; disposing a writer first can
        # otherwise wait indefinitely for a peer while flushing after a timeout.
        $pipe.Dispose()
        if ($writer) { try { $writer.Dispose() } catch {} }
        if ($reader) { try { $reader.Dispose() } catch {} }
        $timeout.Dispose()
    }
}

function Assert-NoNativeOperation($status) {
    if (@($status.queue | Where-Object { $null -ne $_ }).Count -or @($status.history | Where-Object { $null -ne $_ }).Count -or $status.recoveryRequired) {
        throw 'A read-only smoke operation created a native queue/history/recovery marker.'
    }
    if ($status.lastResult.install -or $status.lastResult.download) { throw 'A read-only smoke operation returned a native install/download result.' }
}

function Invoke-ReadOnlyQuery([ValidateSet('search', 'list-installed', 'details', 'prepare')][string]$command, [hashtable]$payload, [string]$reportName, [int]$timeoutSeconds = 0, [bool]$expectSuccess = $true) {
    if ($timeoutSeconds -eq 0) { $timeoutSeconds = $CheckTimeoutSeconds }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $ack = Send-Command $command $payload
    $ackMilliseconds = $timer.ElapsedMilliseconds
    if (-not $ack.accepted -or -not $ack.operationId) { throw "$command was not accepted: $($ack.error)" }
    if ($ackMilliseconds -ge 3000) { throw "$command blocked the IPC acknowledgement." }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        Start-Sleep -Milliseconds 200
        $result = Send-Command 'get-status'
    } while ($result.isBusy -and [DateTimeOffset]::UtcNow -lt $deadline)
    $timer.Stop()
    if ($result.isBusy) {
        [IO.File]::WriteAllText((Join-Path $reportRoot ($reportName + '-timeout.json')), ($result | ConvertTo-Json -Depth 30))
        $null = Send-Command 'cancel' @{ operationId = $ack.operationId }
        throw "$command exceeded ${timeoutSeconds}s; cancellation of the read-only query was requested. This is a timeout failure, not proof of cancellation safety."
    }
    [IO.File]::WriteAllText((Join-Path $reportRoot ($reportName + '.json')), ($result | ConvertTo-Json -Depth 30))
    if ($result.lastResult.operationId -ne $ack.operationId -or $result.lastResult.kind -ne $command) { throw "$command completion did not match its acknowledgement." }
    if ([bool]$result.lastResult.succeeded -ne $expectSuccess) { throw "$command succeeded=$($result.lastResult.succeeded), expected=$expectSuccess`: $($result.lastResult.message)" }
    Assert-NoNativeOperation $result
    Write-Result "PASS $command ACK=$($ackMilliseconds)ms; complete=$($timer.ElapsedMilliseconds)ms; succeeded=$($result.lastResult.succeeded); report=$reportName.json." | Out-Host
    return $result
}

function Get-PackageIdentity($package) {
    return @{ id = $package.id; source = $package.source; installedVersion = $package.installedVersion; availableVersion = $package.availableVersion }
}

function Test-SamePackageIdentity($left, $right) {
    return $left.id -ieq $right.id -and $left.source -ieq $right.source -and
        $left.installedVersion -ceq $right.installedVersion -and $left.availableVersion -ceq $right.availableVersion
}

function Assert-LightweightSearch($status, [string]$query, [bool]$allowEmpty = $false) {
    $rows = @($status.searchResults | Where-Object { $null -ne $_ })
    if ($status.searchQuery -cne $query -or (-not $allowEmpty -and $rows.Count -eq 0) -or $rows.Count -gt 100) { throw 'Search did not return the requested bounded first page.' }
    if ($null -ne $status.selectedDetails) { throw 'A new search retained stale selected details.' }
    foreach ($row in $rows) {
        if ($row.isMetadataResolved -isnot [bool] -or $row.isMetadataResolved) { throw 'The first search page eagerly resolved package metadata.' }
        if ([string]::IsNullOrWhiteSpace($row.id) -or [string]::IsNullOrWhiteSpace($row.name) -or $row.source -cne 'winget') { throw 'Search returned an invalid catalog identity.' }
        if ($row.installedVersion -cne '' -or $row.availableVersion -cne '' -or $row.manualUpgradeCommand -cne '' -or $row.canUninstall) { throw 'An unresolved search row claimed version, installed state, manual command, or uninstall capability.' }
    }
    $keys = @($rows | ForEach-Object { "$($_.source)|$($_.id)" })
    if (@($keys | Group-Object | Where-Object Count -gt 1).Count) { throw 'Search returned duplicate source/package identities.' }
}

function Assert-ResolvedSearchDetail($status, $original) {
    $resolved = $status.selectedDetails.package
    if ($null -eq $resolved -or $resolved.isMetadataResolved -ne $true -or $resolved.id -ine $original.id -or $resolved.source -ine $original.source -or [string]::IsNullOrWhiteSpace($resolved.availableVersion)) {
        throw 'Selected detail did not resolve the selected source/ID to a concrete version.'
    }
    $row = @($status.searchResults | Where-Object { $_.id -ieq $original.id -and $_.source -ieq $original.source })
    if ($row.Count -ne 1 -or $row[0].isMetadataResolved -ne $true -or -not (Test-SamePackageIdentity $row[0] $resolved)) {
        throw 'Selected detail was not reconciled into the original search result identity.'
    }
}

function Assert-PreparationRejected([hashtable]$identity, [string]$reportName) {
    $ack = Send-Command 'prepare' @{ operationKind = 'install'; packages = @($identity) }
    [IO.File]::WriteAllText((Join-Path $reportRoot ($reportName + '-ack.json')), ($ack | ConvertTo-Json -Depth 20))
    if ($ack.accepted) { throw "$reportName unexpectedly accepted an invalid/stale installation preparation." }
    $status = Send-Command 'get-status'
    Assert-NoNativeOperation $status
    Write-Result "PASS $reportName rejected before metadata/native execution: $($ack.error)" | Out-Host
}

function Test-ColdBroadDiscovery {
    # This must precede every check/details/inventory operation in this fresh
    # service process. Do not delete or claim to cold-start WinGet's disk cache.
    $search = Invoke-ReadOnlyQuery 'search' @{ query = 'go' } 'cold-go-first-page' $DiscoverTimeoutSeconds
    Assert-LightweightSearch $search 'go'
    Write-Result "PASS cold service-process query go: rows=$(@($search.searchResults).Count); truncated=$($search.searchResultsTruncated); every row unresolved; no prior catalog query in this process. Host WinGet disk caches were preserved." | Out-Host
    $selected = $search.searchResults[0]
    $beforeKeys = @($search.searchResults | ForEach-Object { "$($_.source)|$($_.id)" } | Sort-Object)
    $details = Invoke-ReadOnlyQuery 'details' @{ packages = @(Get-PackageIdentity $selected) } 'cold-go-selected-details'
    Assert-ResolvedSearchDetail $details $selected
    $afterKeys = @($details.searchResults | ForEach-Object { "$($_.source)|$($_.id)" } | Sort-Object)
    if (($beforeKeys | ConvertTo-Json -Compress) -cne ($afterKeys | ConvertTo-Json -Compress)) { throw 'Selected details replaced the search result set.' }
    $otherResolved = @($details.searchResults | Where-Object { $_.id -ine $selected.id -and $_.isMetadataResolved })
    if ($otherResolved.Count) { throw 'Resolving one selected package also eagerly resolved other result rows.' }
    Assert-PreparationRejected (Get-PackageIdentity $selected) 'stale-lightweight-after-details'
    Write-Result "PASS selected-only resolution: $($selected.id); other search rows remained unresolved." | Out-Host
    return $details
}

function Test-InstallPreparation($resolved, [bool]$expectInstalled, [string]$reportPrefix) {
    # Repeat the identity search to obtain a genuine unresolved row from the
    # current service snapshot, then prepare only. No enqueue API is available.
    $search = Invoke-ReadOnlyQuery 'search' @{ query = $resolved.id } "$reportPrefix-prepare-search"
    Assert-LightweightSearch $search $resolved.id
    $row = @($search.searchResults | Where-Object { $_.id -ieq $resolved.id -and $_.source -eq 'winget' } | Select-Object -First 1)
    if ($row.Count -ne 1) { throw 'The preparation candidate disappeared from the current search.' }
    $identity = Get-PackageIdentity $row[0]
    $forged = $identity.Clone()
    $forged.availableVersion = 'kit-smoke-impossible-version'
    Assert-PreparationRejected $forged "$reportPrefix-forged-version"
    $prepared = Invoke-ReadOnlyQuery 'prepare' @{ operationKind = 'install'; packages = @($identity); options = @{ silent = $false } } "$reportPrefix-prepare" 0 (-not $expectInstalled)
    if ($expectInstalled) {
        if ($null -ne $prepared.preparedOperation) { throw 'An already-installed package produced an install confirmation.' }
        if ($prepared.selectedDetails.package.isMetadataResolved -ne $true -or [string]::IsNullOrWhiteSpace($prepared.selectedDetails.package.installedVersion)) { throw 'Rejected installation preparation did not report the verified installed state.' }
        Write-Result 'PASS unresolved installed package was resolved and refused before installation confirmation.' | Out-Host
        return $prepared
    }
    $confirmation = $prepared.preparedOperation
    if ($null -eq $confirmation -or $confirmation.kind -cne 'install' -or [string]::IsNullOrWhiteSpace($confirmation.id) -or @($confirmation.packages).Count -ne 1) { throw 'Read-only preparation did not produce exactly one install confirmation.' }
    $package = $confirmation.packages[0].package
    if ($package.isMetadataResolved -ne $true -or $package.id -ine $resolved.id -or $package.source -cne 'winget' -or $package.installedVersion -cne '' -or [string]::IsNullOrWhiteSpace($package.availableVersion)) { throw 'Preparation did not pin a fully resolved new-install identity.' }
    if ($confirmation.options.silent -or $confirmation.options.includeUnknown -or $confirmation.options.scope -or $confirmation.options.architecture) { throw 'Preparation changed the selected native installer options.' }
    $matching = @($prepared.searchResults | Where-Object { $_.id -ieq $package.id -and $_.source -eq $package.source })
    if ($matching.Count -ne 1 -or -not (Test-SamePackageIdentity $matching[0] $package)) { throw 'Prepared identity differs from the reconciled current search row.' }
    Assert-PreparationRejected $identity "$reportPrefix-stale-lightweight-after-prepare"
    Write-Result "PASS installation preparation only: id=$($package.id); version=$($package.availableVersion); agreements=$(@($confirmation.packages[0].agreements).Count); no enqueue/native action." | Out-Host
    return $prepared
}

function Test-InstalledInventory {
    $inventory = Invoke-ReadOnlyQuery 'list-installed' @{} 'installed-inventory'
    $rows = @($inventory.installedPackages)
    if ($rows.Count -gt 4096) { throw 'Installed inventory exceeded its 4096-row limit.' }
    $fullKeys = @($rows | ForEach-Object { @($_.source, $_.id, $_.installedVersion, $_.availableVersion) | ConvertTo-Json -Compress })
    $duplicates = @($fullKeys | Group-Object | Where-Object Count -gt 1)
    if ($duplicates.Count) { throw 'Installed inventory contains duplicate complete identities.' }
    $counts = @{}
    foreach ($row in $rows) {
        if ([string]::IsNullOrWhiteSpace($row.id) -or [string]::IsNullOrWhiteSpace($row.installedVersion)) { throw 'Installed inventory returned an empty ID or installed version.' }
        if ($row.source -notin @('winget', 'local', 'msix', 'msstore')) { throw "Unclassified installed source: $($row.source)" }
        if ($row.canUninstall -isnot [bool]) { throw 'Installed inventory omitted its CanUninstall capability.' }
        $key = @($row.id, $row.installedVersion) | ConvertTo-Json -Compress
        $counts[$key] = [int]$counts[$key] + 1
    }
    foreach ($row in $rows) {
        $key = @($row.id, $row.installedVersion) | ConvertTo-Json -Compress
        if ($row.canUninstall -and $counts[$key] -ne 1) { throw "An ambiguous installed ID/version is marked removable: $($row.id) $($row.installedVersion)" }
    }
    $removable = @($rows | Where-Object canUninstall).Count
    Write-Result "PASS installed inventory: total=$($rows.Count); unique=$($fullKeys.Count); CanUninstall=$removable. Capability was checked without invoking uninstall." | Out-Host
    foreach ($source in @('winget', 'local', 'msix', 'msstore')) {
        $sourceRows = @($rows | Where-Object source -eq $source)
        if ($sourceRows.Count -eq 0) {
            Write-Result "LIMITATION installed source $source was not present; its details path was not covered on this host." | Out-Host
            continue
        }
        $eligible = @($sourceRows | Where-Object canUninstall | Select-Object -First 1)
        if ($eligible.Count -eq 0) {
            Write-Result "LIMITATION installed source $source has $($sourceRows.Count) rows but no uniquely removable identity for details coverage." | Out-Host
            continue
        }
        $package = $eligible[0]
        $inventory = Invoke-ReadOnlyQuery 'details' @{ packages = @(Get-PackageIdentity $package) } "installed-source-$source-details"
        $selected = $inventory.selectedDetails.package
        if ($null -eq $selected -or $selected.id -cne $package.id -or $selected.source -cne $package.source -or $selected.installedVersion -cne $package.installedVersion -or $selected.availableVersion -cne $package.availableVersion) {
            throw "Installed $source details did not preserve its complete selected identity."
        }
        if (-not $selected.canUninstall) { throw "Installed $source details lost the verified uninstall capability." }
        if ($source -in @('local', 'msix') -and @($inventory.selectedDetails.agreements).Count -ne 0) { throw 'Installed-only details unexpectedly require remote package agreements.' }
        $currentKeys = @($inventory.installedPackages | ForEach-Object { @($_.source, $_.id, $_.installedVersion, $_.availableVersion) | ConvertTo-Json -Compress } | Sort-Object)
        if ((@($fullKeys | Sort-Object) | ConvertTo-Json -Compress) -cne ($currentKeys | ConvertTo-Json -Compress)) { throw 'Loading installed details changed installed inventory identities.' }
        Write-Result "PASS installed source $source count=$($sourceRows.Count); details=$($selected.id); installed=$($selected.installedVersion); CanUninstall=$($selected.canUninstall)." | Out-Host
    }
    return $inventory
}

function Test-SearchAndDetails([bool]$expectInstalled, [string[]]$candidates, [string]$reportPrefix) {
    $index = 0
    foreach ($query in $candidates) {
        $index++
        $search = Invoke-ReadOnlyQuery 'search' @{ query = $query } "$reportPrefix-search-$index"
        Assert-LightweightSearch $search $query $true
        $package = @($search.searchResults | Where-Object {
            $_.id -eq $query -and $_.source -eq 'winget'
        } | Select-Object -First 1)
        if ($package.Count -eq 0) {
            Write-Result "SEARCH candidate $query was not returned; trying next known catalog ID." | Out-Host
            continue
        }
        $package = $package[0]
        $detail = Invoke-ReadOnlyQuery 'details' @{
            packages = @(Get-PackageIdentity $package)
        } "$reportPrefix-details"
        Assert-ResolvedSearchDetail $detail $package
        $selected = $detail.selectedDetails.package
        if ((-not [string]::IsNullOrEmpty($selected.installedVersion)) -ne $expectInstalled) {
            Write-Result "DETAILS candidate $query resolved installed=$(-not [string]::IsNullOrEmpty($selected.installedVersion)); trying next candidate for expectedInstalled=$expectInstalled." | Out-Host
            continue
        }
        if (-not $expectInstalled -and $selected.installEligibility -notin @('UserWithoutElevation', 'RequiresElevation', 1, 2)) {
            Write-Result "DETAILS candidate $query is manual-only; trying another candidate for read-only preparation coverage." | Out-Host
            continue
        }
        $expectedCommand = if ($expectInstalled) { 'winget upgrade ' } else { 'winget install ' }
        if (-not $selected.manualUpgradeCommand.StartsWith($expectedCommand, [StringComparison]::Ordinal)) { throw 'The manual command used the wrong install/upgrade operation.' }
        if (-not $expectInstalled -and -not $selected.manualUpgradeCommand.EndsWith(' --no-upgrade', [StringComparison]::Ordinal)) { throw 'New installation must not implicitly upgrade an existing package.' }
        Write-Result "PASS search/details: id=$($selected.id); installed=$($selected.installedVersion); available=$($selected.availableVersion); eligibility=$($selected.installEligibility); expectedInstalled=$expectInstalled. No installer was started." | Out-Host
        return Test-InstallPreparation $selected $expectInstalled $reportPrefix
    }
    throw "No known catalog candidate matched installed=$expectInstalled; search/details coverage was not completed."
}

try {
    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    if ($TraceService) { [IO.File]::WriteAllText($logSettingsPath, '{"logLevel":"trace"}', [Text.UTF8Encoding]::new($false)) }
    [IO.File]::WriteAllText($settingsPath, '{"name":"Winget","version":"1.0","properties":{"daily_check_enabled":{"value":false},"notify_updates":{"value":false}}}', [Text.UTF8Encoding]::new($false))
    # Exclude existing recovery/check state from the fresh-process query order.
    # The original state is restored byte-for-byte after the controlled service exits.
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($statePath)) | Out-Null
    [IO.File]::WriteAllText($statePath, '{}', [Text.UTF8Encoding]::new($false))
    # Keep the service's manual-reset event in the initial state for this controlled launch.
    $exitEvent = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, 'Local\Kit.Winget.Exit-41d22b8c-40d5-4eb4-b9db-51aa37802d53')
    $exitEvent.Reset() | Out-Null
    $parentStart = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh.exe).Source)
    $parentStart.UseShellExecute = $false
    $parentStart.CreateNoWindow = $true
    $parentStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', '[Console]::ReadLine() | Out-Null')) { $parentStart.ArgumentList.Add($argument) }
    $parentStart.RedirectStandardInput = $true
    $parentProcess = [Diagnostics.Process]::Start($parentStart)
    $start = [Diagnostics.ProcessStartInfo]::new($serviceExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $RuntimeRoot
    $start.ArgumentList.Add('--pid')
    $start.ArgumentList.Add([string]$parentProcess.Id)
    $serviceProcess = [Diagnostics.Process]::Start($start)
    Write-Result "Service PID $($serviceProcess.Id); controlled parent PID $($parentProcess.Id). Search/details/prepare are read-only; no downloads, enqueue, install, upgrade or uninstall API is available in this script."
    foreach ($name in @('Kit.WingetService.dll', 'Kit.WingetLib.dll', 'Kit.ManagedCommon.dll')) {
        $artifactPath = Join-Path $RuntimeRoot $name
        $hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
        Write-Result "ARTIFACT $name SHA256=$hash"
    }

    $startupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    $status = $null
    do {
        if ($serviceProcess.HasExited) { throw "Service exited during startup: $($serviceProcess.ExitCode)" }
        try { $status = Send-Command 'get-status' } catch {
            [IO.File]::AppendAllText($report, "IPC startup attempt failed: $($_.Exception.Message)`r`n")
            Start-Sleep -Milliseconds 200
        }
    } while (-not $status -and [DateTimeOffset]::UtcNow -lt $startupDeadline)
    if (-not $status) { throw 'Service pipe did not become ready.' }
    if ($status.dailyCheckEnabled -or $status.notifyUpdates) { throw 'The smoke settings did not disable automatic checks and notifications.' }
    while ($status.isBusy -and [DateTimeOffset]::UtcNow -lt $startupDeadline) {
        Start-Sleep -Milliseconds 200
        $status = Send-Command 'get-status'
    }
    if ($status.isBusy) { throw 'WinGet availability detection did not finish.' }
    if ($null -eq $status.availability) { throw 'No WinGet availability result was returned.' }
    Write-Result "PASS detect: available=$($status.availability.isAvailable); version=$($status.availability.version)."
    [IO.File]::WriteAllText((Join-Path $reportRoot 'detect-status.json'), ($status | ConvertTo-Json -Depth 30))
    Assert-NoNativeOperation $status
    if ($status.availability.isAvailable) {
        $status = Test-ColdBroadDiscovery
    }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $ack = Send-Command 'check'
    $timer.Stop()
    if (-not $ack.accepted -or -not $ack.operationId) { throw 'The manual check was not accepted.' }
    if ($timer.ElapsedMilliseconds -ge 3000) { throw 'The manual check blocked the IPC acknowledgement.' }
    Write-Result "PASS ACK: operation=$($ack.operationId); elapsed=$($timer.ElapsedMilliseconds)ms."
    $checkDeadline = [DateTimeOffset]::UtcNow.AddSeconds($CheckTimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 300
        $status = Send-Command 'get-status'
    } while ($status.isBusy -and [DateTimeOffset]::UtcNow -lt $checkDeadline)
    if ($status.isBusy) {
        $null = Send-Command 'cancel' @{ operationId = $ack.operationId }
        throw 'The manual check timed out; cancellation was requested.'
    }
    if ($status.lastResult.operationId -ne $ack.operationId -or $status.lastResult.kind -ne 'check') { throw 'Completion did not match the acknowledged check.' }
    [IO.File]::WriteAllText((Join-Path $reportRoot 'check-status.json'), ($status | ConvertTo-Json -Depth 30))
    if ($status.lastResult.succeeded) {
        Write-Result "PASS check: completed; updates=$(@($status.packages).Count); lastSuccess=$($status.lastSuccessUtc)."
        # Resolve actual installed state through the production COM-backed search;
        # never infer it from a guessed package ID, and never invoke an installer.
        $checkedIds = @($status.packages | ForEach-Object { "$($_.id)|$($_.source)|$($_.availableVersion)" } | Sort-Object)
        $installedCandidates = @(@($status.packages | Select-Object -First 1 -ExpandProperty id) + @('Microsoft.PowerShell', 'Git.Git', 'Microsoft.VisualStudioCode') | Select-Object -Unique)
        $status = Test-SearchAndDetails $true $installedCandidates 'installed'
        $status = Test-SearchAndDetails $false @('KDE.Kate', 'WinSCP.WinSCP', '7zip.7zip', 'VideoLAN.VLC', 'ShareX.ShareX') 'uninstalled'
        $status = Test-InstalledInventory
        $remainingIds = @($status.packages | ForEach-Object { "$($_.id)|$($_.source)|$($_.availableVersion)" } | Sort-Object)
        if (($checkedIds | ConvertTo-Json -Compress) -ne ($remainingIds | ConvertTo-Json -Compress)) { throw 'Read-only search/details changed the checked update identities.' }
        Write-Result 'PASS separation: lightweight search, selected details, read-only install preparation and installed inventory completed; checked update identities remained unchanged. No enqueue, download, install, upgrade, or uninstall command was sent. Cancellation/timeout safety remains covered by fake tests, not this host smoke.'
    } elseif (-not $status.availability.isAvailable) {
        Write-Result "LIMITATION check: Winget is unavailable; the service returned a completed failure: $($status.lastResult.message)"
    } else {
        throw "WinGet check failed: $($status.lastResult.message)"
    }
    if ($status.notifyUpdates -or $status.dailyCheckEnabled) { throw 'Automatic settings changed during the test.' }
    $parentProcess.StandardInput.WriteLine('exit')
    if (-not $parentProcess.WaitForExit(5000)) { throw 'The controlled parent did not exit.' }
    if (-not $serviceProcess.WaitForExit(15000)) { throw 'WingetService outlived its parent.' }
    if ($serviceProcess.ExitCode -ne 0) { throw "Service shutdown failed: $($serviceProcess.ExitCode)" }
    Write-Result 'PASS lifecycle: service exited cleanly after its parent exited.'
    Write-Result 'PASS service smoke (see any LIMITATION above for host WinGet availability).'
} catch {
    Write-Result ('FAIL: ' + $_.Exception.Message)
    throw
} finally {
    if ($serviceProcess -and -not $serviceProcess.HasExited) {
        if ($exitEvent) { $exitEvent.Set() | Out-Null }
        if (-not $serviceProcess.WaitForExit(10000)) { $serviceProcess.Kill(); $serviceProcess.WaitForExit(5000) | Out-Null }
    }
    if ($parentProcess -and -not $parentProcess.HasExited) { $parentProcess.Kill(); $parentProcess.WaitForExit(5000) | Out-Null }
    if ($exitEvent) { $exitEvent.Dispose() }
    foreach ($path in $backups.Keys) {
        if ($null -ne $backups[$path]) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.File]::WriteAllBytes($path, $backups[$path])
            $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($path)))
            $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$backups[$path]))
            if ($actualHash -ne $expectedHash) { throw "Restore verification failed: $path" }
        } elseif (Test-Path -LiteralPath $path) {
            [IO.File]::Delete($path)
            if (Test-Path -LiteralPath $path) { throw "Unexpected smoke state remains: $path" }
        }
    }
    $restoredItems = if ($TraceService) { 'Winget settings, service state, and global log settings' } else { 'Winget settings and service state' }
    Write-Result "RESTORE verified: $restoredItems match their original bytes/existence."
    Write-Output "Report: $report"
}
