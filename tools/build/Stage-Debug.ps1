[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Version) {
    [xml]$versionProps = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Version.props') -Raw
    $Version = $versionProps.Project.PropertyGroup.Version
}
$reportDirectory = Join-Path $repoRoot 'TestResults\FrameworkReview'
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
$sourceRoot = Join-Path $repoRoot 'x64\Debug'
$destinationRoot = Join-Path $repoRoot "bin\debug\$Version"
$reportPath = Join-Path $reportDirectory "Debug-$Version.manifest.json"

if (Test-Path -LiteralPath $destinationRoot) {
    throw "The $Version delivery directory already exists; refusing to overwrite it."
}
foreach ($path in @($sourceRoot, (Split-Path -Parent $destinationRoot))) {
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to stage through a reparse point.'
    }
}

$excludedNames = @(
    'e_sqlite3.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.batteries_v2.dll',
    'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll'
)
$excludedRootFiles = @(
    'Kit.Settings.exe', 'Kit.Settings.deps.json', 'Kit.Settings.runtimeconfig.json',
    'Kit.QuickAccess.exe', 'Kit.QuickAccess.deps.json', 'Kit.QuickAccess.runtimeconfig.json'
)
$excludedExtensions = @('.lib', '.exp', '.idb', '.ilk', '.obj', '.pch', '.tlog', '.lastcodeanalysissucceeded')
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object {
    $relative = [IO.Path]::GetRelativePath($sourceRoot, $_.FullName)
    $include = $relative -notmatch '^(tests|LightSwitchLib)[\\/]'
    $include = $include -and ($_.Name -notlike 'PowerToys.*' -or $_.Name -in @('PowerToys.ActionRunner.exe', 'PowerToys.ActionRunner.pdb'))
    $include = $include -and $_.Extension -notin $excludedExtensions
    $include = $include -and $_.Name -notin $excludedNames
    $include = $include -and $relative -notin $excludedRootFiles
    if ($include -and $_.Extension -eq '.pdb') {
        $include = (Test-Path -LiteralPath (Join-Path $_.DirectoryName ($_.BaseName + '.exe'))) -or
            (Test-Path -LiteralPath (Join-Path $_.DirectoryName ($_.BaseName + '.dll')))
    }
    $include
})
if ($sourceFiles.Count -eq 0) { throw 'No runtime files were selected.' }

$versionedFiles = @(
    'Kit.exe', 'Kit.Awake.exe', 'Kit.Awake.dll',
    'Kit.ManagedCommon.dll', 'Kit.Interop.dll', 'Kit.GPOWrapper.dll', 'Kit.Settings.UI.Lib.dll',
    'Kit.BackgroundActivatorDLL.dll', 'PowerToys.ActionRunner.exe',
    'Kit.AwakeModuleInterface.dll', 'Kit.LightSwitchModuleInterface.dll',
    'Kit.LocalserverModuleInterface.dll',
    'LightSwitchService\Kit.LightSwitchService.exe',
    'WinUI3Apps\Kit.Settings.exe', 'WinUI3Apps\Kit.Settings.dll',
    'WinUI3Apps\Kit.QuickAccess.exe', 'WinUI3Apps\Kit.QuickAccess.dll',
    'WinUI3Apps\Kit.AiHub.dll', 'WinUI3Apps\LocalserverLib.dll'
)
$expectedQuadVersion = "${Version}.0"
foreach ($relative in $versionedFiles) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $sourceRoot $relative))
    $binaryVersion = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
    if ($binaryVersion -ne $expectedQuadVersion) { throw "Unexpected binary version for ${relative}: $binaryVersion (expected $expectedQuadVersion)" }
}

[IO.Directory]::CreateDirectory($destinationRoot) | Out-Null
$manifest = [Collections.Generic.List[object]]::new()
foreach ($file in $sourceFiles) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to copy a redirected runtime file.'
    }
    $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
    $targetPath = Join-Path $destinationRoot $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $targetPath)) | Out-Null
    [IO.File]::Copy($file.FullName, $targetPath, $false)
    $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) { throw "Runtime file hash mismatch: $relative" }
    $manifest.Add([ordered]@{ Path = $relative; Bytes = $file.Length; Sha256 = $targetHash })
}

$dependencyResults = [Collections.Generic.List[object]]::new()
foreach ($relative in @(
    'Kit.Awake.deps.json',
    'WinUI3Apps\Kit.Settings.deps.json',
    'WinUI3Apps\Kit.QuickAccess.deps.json'
)) {
    $depsPath = Join-Path $destinationRoot $relative
    $depsText = [IO.File]::ReadAllText($depsPath)
    if ($depsText -match '(?i)sqlite') { throw "Unexpected SQLite dependency: $relative" }
    $deps = $depsText | ConvertFrom-Json -AsHashtable
    $target = $deps.targets[$deps.runtimeTarget.name]
    $assetCount = 0
    foreach ($library in $target.Values) {
        foreach ($kind in @('runtime', 'native', 'resources')) {
            if (-not $library.ContainsKey($kind)) { continue }
            foreach ($asset in $library[$kind].GetEnumerator()) {
                $assetName = [IO.Path]::GetFileName($asset.Key)
                if ($assetName -eq '_._') { continue }
                if ($kind -eq 'resources') {
                    $assetName = Join-Path $asset.Value.locale $assetName
                }
                $assetPath = Join-Path (Split-Path -Parent $depsPath) $assetName
                if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
                    throw "Missing $kind dependency in ${relative}: $($asset.Key)"
                }
                $assetCount++
            }
        }
        if ($library.ContainsKey('runtimeTargets')) {
            throw "Review runtimeTargets before changing this delivery's asset validation: $relative"
        }
    }
    $dependencyResults.Add([ordered]@{ Path = $relative; RuntimeAssets = $assetCount; Missing = 0 })
}

foreach ($relative in @(
    'Kit.pri', 'PowerToys.ActionRunner.exe', 'Kit.Interop.dll', 'Kit.GPOWrapper.dll',
    'Kit.BackgroundActivatorDLL.dll', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
    'Kit.Awake.runtimeconfig.json', 'Assets\Awake\Awake.ico',
    'WinUI3Apps\Kit.Settings.pri', 'WinUI3Apps\Kit.QuickAccess.pri',
    'WinUI3Apps\Kit.Settings.runtimeconfig.json', 'WinUI3Apps\Kit.QuickAccess.runtimeconfig.json'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $destinationRoot $relative) -PathType Leaf)) {
        throw "Required runtime asset is missing: $relative"
    }
}
foreach ($relative in @('svgs', 'WinUI3Apps\SettingsXAML', 'WinUI3Apps\QuickAccessXaml', 'WinUI3Apps\Assets', 'WinUI3Apps\zh-CN')) {
    if (@(Get-ChildItem -LiteralPath (Join-Path $destinationRoot $relative) -Recurse -File).Count -eq 0) {
        throw "Required runtime resource directory is empty: $relative"
    }
}

$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $destinationRoot 'KitSparse.msix'))
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if (-not $entry) { throw 'The sparse package manifest is missing.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$package = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    if ($package.Package.Identity.Version -ne $expectedQuadVersion) { throw "The sparse package version is stale ($($package.Package.Identity.Version) vs expected $expectedQuadVersion)." }
    $logos = @($package.Package.Properties.Logo)
    foreach ($app in $package.Package.Applications.Application) {
        $logos += $app.VisualElements.Square150x150Logo
        $logos += $app.VisualElements.Square44x44Logo
    }
    foreach ($logo in $logos) {
        if (-not $logo -or -not $archive.GetEntry($logo.Replace('\', '/'))) {
            throw "The sparse package references a missing logo: $logo"
        }
    }
}
finally { $archive.Dispose() }

$report = [ordered]@{
    Version = $Version
    Configuration = 'Debug'
    Platform = 'x64'
    Directory = $destinationRoot
    FileCount = $manifest.Count
    TotalBytes = ($manifest | ForEach-Object { $_.Bytes } | Measure-Object -Sum).Sum
    PdbCount = @($manifest | Where-Object { $_.Path.EndsWith('.pdb') }).Count
    DependencyChecks = @($dependencyResults)
    SparsePackageLogoReferences = $logos.Count
    Files = @($manifest)
}
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
[pscustomobject]$report | Select-Object Version, Configuration, Platform, Directory, FileCount, TotalBytes, PdbCount, SparsePackageLogoReferences
$dependencyResults
Write-Output "SHA-256 manifest: $reportPath"
