# Shared by isolated Settings harnesses. Back up only AI Hub's generated audit files;
# never delete a report directory recursively or touch unrelated files.
function Assert-AiHubFixturePath([string]$Path, [string]$StateDirectory) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($StateDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($full -ne $root -and -not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'AI Hub fixture path escaped its state directory.'
    }
    for ($component = $full; $component; $component = [IO.Path]::GetDirectoryName($component)) {
        if (Test-Path -LiteralPath $component) {
            if ((Get-Item -LiteralPath $component -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Refusing redirected AI Hub fixture storage.'
            }
        }
    }
    return $full
}

function Get-AiHubDerivedFixtureFiles([string]$StateDirectory) {
    $root = Assert-AiHubFixturePath $StateDirectory $StateDirectory
    if (-not (Test-Path -LiteralPath $root)) { return }
    foreach ($item in Get-ChildItem -LiteralPath $root -Force) {
        if ($item.Name -match '^audit_analysis_cache\.json(?:\.[a-fA-F0-9]{32}\.tmp)?$') {
            $null = Assert-AiHubFixturePath $item.FullName $root
            if ($item.PSIsContainer) { throw 'Expected a regular AI Hub analysis cache file.' }
            $item.FullName
        }
    }
    $weeklyScans = Assert-AiHubFixturePath (Join-Path $root 'weekly-scans') $root
    if (Test-Path -LiteralPath $weeklyScans) {
        if (-not (Get-Item -LiteralPath $weeklyScans).PSIsContainer) { throw 'Expected a weekly scan snapshot directory.' }
        foreach ($item in Get-ChildItem -LiteralPath $weeklyScans -Force) {
            if ($item.Name -match '^weekly-scan-\d{4}-\d{2}-\d{2}\.json(?:\.[a-fA-F0-9]{32}\.tmp)?$') {
                $null = Assert-AiHubFixturePath $item.FullName $root
                if ($item.PSIsContainer) { throw 'Expected a regular weekly scan snapshot.' }
                $item.FullName
            }
        }
    }
    $reports = Assert-AiHubFixturePath (Join-Path $root 'reports') $root
    if (-not (Test-Path -LiteralPath $reports)) { return }
    if (-not (Get-Item -LiteralPath $reports).PSIsContainer) { throw 'Expected an AI Hub report directory.' }
    foreach ($item in Get-ChildItem -LiteralPath $reports -Force) {
        if ($item.Name -match '^(?:daily-summary\.json|(?:weekly|monthly)-\d{4}-\d{2}-\d{2}\.md)(?:\.[a-fA-F0-9]{32}\.tmp)?$' -or
            $item.Name -match '^stability-(?:weekly-(?:(?:fast|full)-)?|monthly-)\d{4}-\d{2}-\d{2}(?:\.md|-[a-f0-9]{32}(?:-(?:zh|en)\.md|(?:-(?:zh|en))?(?:-(?:light|dark)-v[1-9][0-9]*)?\.png))(?:\.[a-fA-F0-9]{32}\.tmp)?$') {
            $null = Assert-AiHubFixturePath $item.FullName $root
            if ($item.PSIsContainer) { throw 'Expected a regular generated AI Hub report.' }
            $item.FullName
        }
    }
}

function New-AiHubDerivedStateSnapshot([string]$StateDirectory) {
    $root = Assert-AiHubFixturePath $StateDirectory $StateDirectory
    $reports = Assert-AiHubFixturePath (Join-Path $root 'reports') $root
    $weeklyScans = Assert-AiHubFixturePath (Join-Path $root 'weekly-scans') $root
    $files = @{}
    foreach ($path in @(Get-AiHubDerivedFixtureFiles $root)) {
        $files[$path] = [IO.File]::ReadAllBytes($path)
    }
    return [pscustomobject]@{
        StateDirectory = $root
        ReportsDirectory = $reports
        WeeklyScansDirectory = $weeklyScans
        StateExisted = (Test-Path -LiteralPath $root)
        ReportsExisted = (Test-Path -LiteralPath $reports)
        WeeklyScansExisted = (Test-Path -LiteralPath $weeklyScans)
        Files = $files
    }
}

function Clear-AiHubDerivedStateFixture($Snapshot) {
    foreach ($path in $Snapshot.Files.Keys) {
        $null = Assert-AiHubFixturePath $path $Snapshot.StateDirectory
        Remove-Item -LiteralPath $path -Force
    }
}

function Restore-AiHubDerivedStateFixture($Snapshot) {
    $failures = [Collections.Generic.List[string]]::new()
    # Remove only generated files absent from the original snapshot, including
    # atomic-write temporary files left behind by a terminated smoke process.
    foreach ($path in @(Get-AiHubDerivedFixtureFiles $Snapshot.StateDirectory)) {
        if (-not $Snapshot.Files.ContainsKey($path)) {
            try { Remove-Item -LiteralPath $path -Force }
            catch { $failures.Add('Could not remove a generated AI Hub fixture file.') }
        }
    }
    foreach ($path in $Snapshot.Files.Keys) {
        try {
            $null = Assert-AiHubFixturePath $path $Snapshot.StateDirectory
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.File]::WriteAllBytes($path, $Snapshot.Files[$path])
        } catch { $failures.Add('Could not restore a generated AI Hub file.') }
    }
    foreach ($path in $Snapshot.Files.Keys) {
        try {
            $null = Assert-AiHubFixturePath $path $Snapshot.StateDirectory
            if (-not [Linq.Enumerable]::SequenceEqual[byte]([byte[]]$Snapshot.Files[$path], [IO.File]::ReadAllBytes($path))) {
                throw 'Restored file differs.'
            }
        } catch { $failures.Add('AI Hub derived-state byte verification failed.') }
    }
    $remaining = @(Get-AiHubDerivedFixtureFiles $Snapshot.StateDirectory)
    if ($remaining.Count -ne $Snapshot.Files.Count) { $failures.Add('AI Hub derived-state file count was not restored.') }
    foreach ($entry in @(@($Snapshot.ReportsDirectory, $Snapshot.ReportsExisted), @($Snapshot.WeeklyScansDirectory, $Snapshot.WeeklyScansExisted), @($Snapshot.StateDirectory, $Snapshot.StateExisted))) {
        $directory = [string]$entry[0]
        if (-not $entry[1] -and (Test-Path -LiteralPath $directory)) {
            try {
                $null = Assert-AiHubFixturePath $directory $Snapshot.StateDirectory
                if (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
                    Remove-Item -LiteralPath $directory
                }
            } catch { $failures.Add('Could not remove an empty AI Hub fixture directory.') }
        }
    }
    if ($failures.Count) { throw ($failures -join ' ') }
    Write-Host 'AI Hub generated reports and analysis cache restored byte-for-byte; new fixture files removed.'
}
