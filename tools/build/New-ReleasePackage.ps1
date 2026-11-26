#Requires -Version 5.1

<#
.SYNOPSIS
Packages the staged Kit Release runtime into the portable archive published on GitHub Releases.

.DESCRIPTION
Reads the version from src/Version.props (unless -Version is supplied), validates the
bin/release/<version> delivery produced by Stage-Release.ps1 against its SHA-256 manifest,
creates kit_<version>_Portable.zip, writes the matching .sha256 checksum in sha256sum
format, and extracts the matching changelog section as release notes. It also writes
release-metadata.json for the workflow summary and artifacts.

The script does not build, stage, tag, or publish anything; the release workflow drives
those steps and uploads the resulting assets.

.PARAMETER Version
Release version in x.y.z form. Defaults to the value in src/Version.props.

.PARAMETER Tag
Git tag that identifies the release. Defaults to the version without a prefix.

.PARAMETER OutputDirectory
Directory that receives the archive, checksum, notes and metadata. Defaults to
bin/release-artifacts.

.PARAMETER CommitSha
Optional source commit recorded in the release notes and metadata.

.PARAMETER Repository
Optional owner/repository recorded in the metadata.

.PARAMETER SourceDirectory
Staged runtime directory. Defaults to bin/release/<version>. Intended for validation
against an alternate staging location.

.PARAMETER ManifestPath
Stage-Release.ps1 manifest. Defaults to
TestResults/FrameworkReview/Release-<version>.manifest.json.

.PARAMETER ChangelogPath
Changelog used for release notes. Defaults to changelog.md.

.EXAMPLE
.\tools\build\New-ReleasePackage.ps1 -Version 3.5.2 -CommitSha <sha>
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [ValidatePattern('^v?\d+\.\d+\.\d+$')]
    [string]$Tag,

    [string]$OutputDirectory,

    [string]$CommitSha = '',

    [string]$Repository = '',

    [string]$SourceDirectory,

    [string]$ManifestPath,

    [string]$ChangelogPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$BaseDirectory
    )
    $candidate = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $BaseDirectory $Path }
    return [IO.Path]::GetFullPath($candidate)
}

function ConvertTo-PackageRelativePath {
    param([Parameter(Mandatory)][string]$Path)

    $relative = $Path.Replace('/', '\')
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)) {
        throw "The package contains an invalid relative path: '$Path'."
    }
    foreach ($part in $relative.Split([char]'\')) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part -in @('.', '..') -or
            $part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw "The package contains an invalid relative path: '$Path'."
        }
    }
    return $relative
}

function Assert-ReleaseArchiveMatchesManifest {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, object]]$ExpectedFiles
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -ne $ExpectedFiles.Count) {
            throw "The portable archive contains $($archive.Entries.Count) entries but the manifest records $($ExpectedFiles.Count)."
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($zipEntry in $archive.Entries) {
            $relative = ConvertTo-PackageRelativePath -Path $zipEntry.FullName
            if (-not $seen.Add($relative)) {
                throw "The portable archive contains a duplicate entry: $relative"
            }
            $expected = $null
            if (-not $ExpectedFiles.TryGetValue($relative, [ref]$expected)) {
                throw "The portable archive contains an unexpected entry: $relative"
            }
            if ([long]$zipEntry.Length -ne [long]$expected.Bytes) {
                throw "Archive entry '$relative' has $($zipEntry.Length) bytes but the manifest records $($expected.Bytes)."
            }
            $stream = $zipEntry.Open()
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                $actualHash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
            }
            finally {
                $sha256.Dispose()
                $stream.Dispose()
            }
            if (-not [StringComparer]::OrdinalIgnoreCase.Equals($actualHash, [string]$expected.Sha256)) {
                throw "Archive entry '$relative' SHA-256 does not match the staged manifest."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Read-VersionFromProps {
    $versionPropsPath = Join-Path $repoRoot 'src\Version.props'
    if (-not (Test-Path -LiteralPath $versionPropsPath -PathType Leaf)) {
        throw "Version file not found: $versionPropsPath"
    }
    [xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw
    $value = [string]$versionProps.Project.PropertyGroup.Version
    if ($value -notmatch '^\d+\.\d+\.\d+$') {
        throw "src/Version.props contains an unsupported version '$value'; expected x.y.z."
    }
    return $value
}

function Get-ChangelogSection {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ReleaseVersion
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Changelog not found: $Path"
    }
    $lines = [IO.File]::ReadAllLines($Path)
    $headingPattern = [regex]::new("^###\s+$([regex]::Escape($ReleaseVersion))(\s|\(|（|$)")
    $start = -1
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($headingPattern.IsMatch($lines[$index])) {
            $start = $index
            break
        }
    }
    if ($start -lt 0) {
        throw "changelog.md has no '### $ReleaseVersion' section. Add release notes before publishing."
    }

    $end = $lines.Length - 1
    for ($index = $start + 1; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match '^(##|###)\s') {
            $end = $index - 1
            break
        }
    }
    while ($end -gt $start -and [string]::IsNullOrWhiteSpace($lines[$end])) {
        $end--
    }
    return (($lines[$start..$end]) -join ([string][char]10))
}

if (-not $Version) {
    $Version = Read-VersionFromProps
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Unsupported release version '$Version'; expected x.y.z."
}
if (-not $Tag) {
    $Tag = $Version
}

if (-not $SourceDirectory) {
    $SourceDirectory = Join-Path $repoRoot "bin\release\$Version"
}
if (-not $ManifestPath) {
    $ManifestPath = Join-Path $repoRoot "TestResults\FrameworkReview\Release-$Version.manifest.json"
}
if (-not $ChangelogPath) {
    $ChangelogPath = Join-Path $repoRoot 'changelog.md'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'bin\release-artifacts'
}

$SourceDirectory = Resolve-AbsolutePath -Path $SourceDirectory -BaseDirectory $repoRoot
$ManifestPath = Resolve-AbsolutePath -Path $ManifestPath -BaseDirectory $repoRoot
$ChangelogPath = Resolve-AbsolutePath -Path $ChangelogPath -BaseDirectory $repoRoot
$OutputDirectory = Resolve-AbsolutePath -Path $OutputDirectory -BaseDirectory $repoRoot

if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
    throw "Staged runtime directory not found: $SourceDirectory. Run tools/build/Stage-Release.ps1 first."
}
$sourceItem = Get-Item -LiteralPath $SourceDirectory -Force
if ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw "Refusing to package through a reparse point: $SourceDirectory"
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Stage-Release.ps1 manifest not found: $ManifestPath. Run tools/build/Stage-Release.ps1 first."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.Version -ne $Version) {
    throw "The staged manifest records version '$($manifest.Version)' but the release version is '$Version'."
}
if ([string]$manifest.Configuration -ne 'Release') {
    throw "The staged manifest records configuration '$($manifest.Configuration)'; only Release output can be published."
}

$manifestFiles = @($manifest.Files)
if ($manifestFiles.Count -eq 0) {
    throw "The staged manifest lists no files: $ManifestPath"
}
if ([int]$manifest.FileCount -ne $manifestFiles.Count) {
    throw "The staged manifest is inconsistent: FileCount is $($manifest.FileCount) but $($manifestFiles.Count) entries are listed."
}

$expectedFiles = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifestFiles) {
    $relative = ConvertTo-PackageRelativePath -Path ([string]$entry.Path)
    if ($expectedFiles.ContainsKey($relative)) {
        throw "The staged manifest contains a duplicate path: $relative"
    }
    if ([string]$entry.Sha256 -cnotmatch '\A[0-9a-fA-F]{64}\z') {
        throw "The staged manifest contains an invalid SHA-256 for '$relative'."
    }
    $expectedFiles.Add($relative, $entry)
}

$actualFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -ErrorAction Stop)
if ($actualFiles.Count -ne $expectedFiles.Count) {
    throw "The staged directory has $($actualFiles.Count) files but the manifest records $($expectedFiles.Count). Re-run tools/build/Stage-Release.ps1."
}
foreach ($relative in $expectedFiles.Keys) {
    $entry = $expectedFiles[$relative]
    $fullPath = Join-Path $SourceDirectory $relative
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "The staged manifest lists a missing file: $relative"
    }
    $length = (Get-Item -LiteralPath $fullPath -Force).Length
    if ([long]$length -ne [long]$entry.Bytes) {
        throw "The staged file '$relative' has $length bytes but the manifest records $($entry.Bytes). Re-run tools/build/Stage-Release.ps1."
    }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($actualHash, [string]$entry.Sha256)) {
        throw "The staged file '$relative' SHA-256 does not match the staged manifest. Re-run tools/build/Stage-Release.ps1."
    }
}

$archiveName = 'kit_{0}_Portable.zip' -f $Version
$checksumName = "$archiveName.sha256"
$notesName = 'release-notes.md'
$metadataName = 'release-metadata.json'

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$archivePath = Join-Path $OutputDirectory $archiveName
$checksumPath = Join-Path $OutputDirectory $checksumName
$notesPath = Join-Path $OutputDirectory $notesName
$metadataPath = Join-Path $OutputDirectory $metadataName
foreach ($existing in @($archivePath, $checksumPath, $notesPath, $metadataPath)) {
    if (Test-Path -LiteralPath $existing) {
        Remove-Item -LiteralPath $existing -Force
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
Write-Host "Compressing $($manifestFiles.Count) staged files into $archiveName ..."
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
try {
    [IO.Compression.ZipFile]::CreateFromDirectory($SourceDirectory, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
}
catch {
    throw "Failed to create the portable archive: $($_.Exception.Message)"
}
finally {
    $stopwatch.Stop()
}
Write-Host ("Archive created in {0:N1}s: {1:N0} bytes" -f $stopwatch.Elapsed.TotalSeconds, (Get-Item -LiteralPath $archivePath).Length)

Assert-ReleaseArchiveMatchesManifest -ArchivePath $archivePath -ExpectedFiles $expectedFiles
Write-Host "Verified $($manifestFiles.Count) archive entry sizes and SHA-256 hashes against the staged manifest."

$archiveBytes = (Get-Item -LiteralPath $archivePath).Length
$archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksumPath, ('{0}  {1}' -f $archiveSha256, $archiveName) + [string][char]10, [Text.UTF8Encoding]::new($false))
Write-Host "SHA-256: $archiveSha256"

$changelogSection = Get-ChangelogSection -Path $ChangelogPath -ReleaseVersion $Version
$sizeMiB = [math]::Round($archiveBytes / 1MB, 1)
$noteLines = @(
    $changelogSection,
    '',
    '**Release verification**',
    '',
    ('- Archive: {0} ({1:N0} files, {2} MiB)' -f $archiveName, $manifestFiles.Count, $sizeMiB),
    ('- SHA-256: {0}' -f $archiveSha256)
)
if ($CommitSha -match '^[0-9a-fA-F]{7,40}$') {
    $noteLines += ('- Build: commit {0} / x64 Release' -f $CommitSha.Substring(0, 7))
}
$noteLines += @(
    '',
    'Extract the complete archive to a folder and launch Kit.exe; keep the folder contents together.'
)
[IO.File]::WriteAllText($notesPath, (($noteLines) -join ([string][char]10)) + [string][char]10, [Text.UTF8Encoding]::new($false))

$metadata = [ordered]@{
    version         = $Version
    tag             = $Tag
    commit          = $CommitSha
    repository      = $Repository
    configuration   = 'Release'
    platform        = 'x64'
    archive         = $archiveName
    archiveBytes    = $archiveBytes
    archiveSha256   = $archiveSha256
    fileCount       = $manifestFiles.Count
    checksum        = $checksumName
    notes           = $notesName
    sourceDirectory = $SourceDirectory
    stagedManifest  = $ManifestPath
    createdAtUtc    = [DateTime]::UtcNow.ToString('o')
}
[IO.File]::WriteAllText($metadataPath, (($metadata | ConvertTo-Json -Depth 4) -join ([string][char]10)) + [string][char]10, [Text.UTF8Encoding]::new($false))

$result = [pscustomobject]@{
    Version       = $Version
    Tag           = $Tag
    Archive       = $archivePath
    ArchiveBytes  = $archiveBytes
    ArchiveSha256 = $archiveSha256
    FileCount     = $manifestFiles.Count
    Checksum      = $checksumPath
    Notes         = $notesPath
    Metadata      = $metadataPath
}
Write-Host ("Release package ready: {0} ({1:N0} files, {2} MiB)" -f $archiveName, $manifestFiles.Count, $sizeMiB)
$result
