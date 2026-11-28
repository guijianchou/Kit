# Isolate exactly the two files owned by Settings' rotating CrashLog. Original
# bytes stay in memory; only test-generated observations are copied to reports.
function New-TestCrashLogSnapshot {
    param(
        [Parameter(Mandatory)] [string]$KitDataDirectory,
        [Parameter(Mandatory)] [string]$ReportDirectory
    )

    $fixtureId = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [guid]::NewGuid().ToString('N')
    $entries = foreach ($name in @('crash.log', 'crash.1.log')) {
        $path = [IO.Path]::GetFullPath((Join-Path $KitDataDirectory $name))
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Refusing to isolate a redirected or non-file crash log: $path"
            }
        }
        $existed = [IO.File]::Exists($path)
        [pscustomobject]@{
            Name = $name
            Path = $path
            Existed = $existed
            Bytes = if ($existed) { ,([IO.File]::ReadAllBytes($path)) } else { $null }
            Isolated = $false
        }
    }
    [pscustomobject]@{
        Entries = @($entries)
        OutputDirectory = Join-Path ([IO.Path]::GetFullPath($ReportDirectory)) "fixture-crash-logs-$fixtureId"
    }
}

function Clear-TestCrashLogFixture {
    param([Parameter(Mandatory)] $Snapshot)

    # Call only inside the wrapper's try/finally, after its no-Kit-process check.
    foreach ($entry in $Snapshot.Entries) {
        [IO.File]::Delete($entry.Path)
        $entry.Isolated = $true
    }
}

function Restore-TestCrashLogFixture {
    param([Parameter(Mandatory)] $Snapshot)

    $failures = [Collections.Generic.List[string]]::new()
    $records = [Collections.Generic.List[object]]::new()
    foreach ($entry in $Snapshot.Entries) {
        if (-not $entry.Isolated) { continue }
        $captured = $false
        $restored = $false
        $generatedLength = 0L
        try {
            if ([IO.File]::Exists($entry.Path)) {
                $generated = [IO.File]::ReadAllBytes($entry.Path)
                [IO.Directory]::CreateDirectory($Snapshot.OutputDirectory) | Out-Null
                [IO.File]::WriteAllBytes((Join-Path $Snapshot.OutputDirectory ("fixture-" + $entry.Name)), $generated)
                $generatedLength = $generated.Length
                $captured = $true
            }
        } catch {
            $failures.Add("Could not archive test-generated $($entry.Name): $($_.Exception.Message)")
        } finally {
            # Restoration is independent of archiving and is attempted for both
            # files, including when one archive or one restore has already failed.
            try {
                if ($entry.Existed) {
                    [IO.File]::WriteAllBytes($entry.Path, [byte[]]$entry.Bytes)
                    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($entry.Path)) -ne [Convert]::ToBase64String([byte[]]$entry.Bytes)) {
                        throw 'Restored bytes do not match the original snapshot.'
                    }
                } else {
                    [IO.File]::Delete($entry.Path)
                    if ([IO.File]::Exists($entry.Path)) { throw 'A test-created crash log remains.' }
                }
                $restored = $true
                $entry.Isolated = $false
            } catch {
                $failures.Add("Could not restore original $($entry.Name): $($_.Exception.Message)")
            }
        }
        $records.Add([pscustomobject]@{
            Name = $entry.Name
            OriginallyExisted = $entry.Existed
            FixtureCaptured = $captured
            FixtureBytes = $generatedLength
            OriginalRestored = $restored
        })
    }
    try {
        [IO.Directory]::CreateDirectory($Snapshot.OutputDirectory) | Out-Null
        $manifest = [ordered]@{
            Classification = 'Isolated UI smoke fixture diagnostics; not evidence of a production crash.'
            Notes = 'These logs may contain deliberately injected exceptions. Original user crash logs were kept in memory; each byte-for-byte restoration result is listed below.'
            Files = $records.ToArray()
            Failures = $failures.ToArray()
        }
        [IO.File]::WriteAllText((Join-Path $Snapshot.OutputDirectory 'fixture-manifest.json'),
            ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    } catch {
        $failures.Add("Could not write fixture crash-log manifest: $($_.Exception.Message)")
    }
    if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
    Write-Host "Fixture exception diagnostics: $($Snapshot.OutputDirectory) (test observations, not production crashes); original crash logs restored byte for byte."
}
