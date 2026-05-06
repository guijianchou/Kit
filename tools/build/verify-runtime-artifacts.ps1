<#
.SYNOPSIS
Verifies that the active Kit runtime output does not contain known slim-build artifacts.

.DESCRIPTION
Checks the active versioned runtime output folder for link-only native artifacts,
debug symbols, removed model assets, and non-English locale satellite folders.
#>

[CmdletBinding()]
param (
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',

    [string]$Configuration = 'Release',

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$repoRoot = (Resolve-Path "$scriptDir\..\..").Path
$versionPropsPath = Join-Path $repoRoot 'src\Version.props'

if (-not (Test-Path $versionPropsPath)) {
    throw "Version.props not found at $versionPropsPath"
}

[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath
$propertyGroup = $versionProps.Project.PropertyGroup | Select-Object -First 1
$version = ($propertyGroup.Version | Out-String).Trim()
$devEnvironment = ($propertyGroup.DevEnvironment | Out-String).Trim()

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version.props does not define Version."
}

$activeVersionDirName = if ([string]::IsNullOrWhiteSpace($devEnvironment)) {
    $version
} else {
    "$version $devEnvironment"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $platformRoot = Join-Path $repoRoot $Platform
    $outputRoot = if ($Configuration -eq 'Release') {
        $versionedOutputRoot = Join-Path $platformRoot $activeVersionDirName
        if (Test-Path $versionedOutputRoot) {
            $versionedOutputRoot
        } else {
            Join-Path $platformRoot 'Release'
        }
    } else {
        Join-Path $platformRoot $Configuration
    }
} else {
    $outputRoot = $OutputRoot
}

$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($outputRoot)

if (-not (Test-Path $outputRoot)) {
    throw "Runtime output folder not found: $outputRoot"
}

$failures = New-Object System.Collections.Generic.List[string]

foreach ($pattern in @('*.lib', '*.exp', '*.pdb', '*.lib.lastcodeanalysissucceeded')) {
    $matches = Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue
    foreach ($match in $matches) {
        $failures.Add("Unexpected runtime artifact: $($match.FullName)")
    }
}

$foundryMatches = Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter '*Foundry*' -ErrorAction SilentlyContinue
foreach ($match in $foundryMatches) {
    $failures.Add("Unexpected Foundry artifact: $($match.FullName)")
}

$localePattern = '^[a-z]{2,3}(-[A-Za-z]{2,8}){1,2}$'
$allowedLocaleNames = @('en-US', 'en-us')
$localeDirs = Get-ChildItem -LiteralPath $outputRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match $localePattern -and $allowedLocaleNames -notcontains $_.Name }

foreach ($dir in $localeDirs) {
    $failures.Add("Non-English locale directory: $($dir.FullName)")
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Runtime artifact verification passed for $outputRoot"
