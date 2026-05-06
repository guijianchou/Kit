<#
.SYNOPSIS
Removes stale Kit versioned output folders.

.DESCRIPTION
Reads src\Version.props, computes the active version output folder, and removes
older sibling folders under the selected platform output root. Debug is always
preserved. Use -WhatIf first to preview deletions.

.PARAMETER Platform
Output platform folder to inspect. Defaults to x64.

.EXAMPLE
.\tools\build\clean-stale-versions.ps1 -WhatIf

.EXAMPLE
.\tools\build\clean-stale-versions.ps1 -Platform x64

.NOTES
For Kit 1.0.3, this keeps x64\1.0.3, x64\Debug, and x64\Release, then
removes older version folders such as x64\1.0.1 or x64\1.0.2 beta1.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param (
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64'
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

$platformRoot = Join-Path $repoRoot $Platform
if (-not (Test-Path $platformRoot)) {
    Write-Host "No $Platform output folder found."
    return
}

$preservedNames = @($activeVersionDirName, 'Debug', 'Release')
$staleDirs = Get-ChildItem -LiteralPath $platformRoot -Directory |
    Where-Object { $preservedNames -notcontains $_.Name }

if ($staleDirs.Count -eq 0) {
    Write-Host "No stale $Platform version folders found. Preserved: $($preservedNames -join ', ')"
    return
}

foreach ($dir in $staleDirs) {
    if ($PSCmdlet.ShouldProcess($dir.FullName, 'Remove stale Kit version output directory')) {
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force
    }
}
