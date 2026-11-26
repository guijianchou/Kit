[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [switch]$Force
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
    if ($Force) {
        Remove-Item -LiteralPath $destinationRoot -Recurse -Force
    } else {
        throw "The $Version delivery directory already exists; use -Force to overwrite it."
    }
}
foreach ($path in @($sourceRoot, (Split-Path -Parent $destinationRoot))) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to stage through a reparse point.'
    }
}

function Get-RelativePathCompat([string]$relativeTo, [string]$path) {
    if ([type]::GetType('System.IO.Path').GetMethod('GetRelativePath', [type[]]@([string], [string]))) {
        return [System.IO.Path]::GetRelativePath($relativeTo, $path)
    }
    $root = [IO.Path]::GetFullPath($relativeTo).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($path)
    if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($root.Length)
    }
    return $full
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
    $relative = Get-RelativePathCompat $sourceRoot $_.FullName
    $include = $relative -notmatch '^(tests|LightSwitchLib)[\\/]'
    # Isolated UI harnesses can leave files beside the app as well as under tests.
    $include = $include -and $relative -notmatch '(?:^|[\\/])(?:[^\\/]*(?:Smoke|Harness)[^\\/]*|\.csc[^\\/]*)(?:[\\/]|$)'
    $include = $include -and ($_.Name -notlike 'PowerToys.*')
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

# Reject identifiable test outputs instead of deleting all names containing
# "test": UDPtest and TestableIO/Testably are real production dependencies.
$testArtifactOrigins = [ordered]@{
    '^Microsoft\.Interop\.Tests(?:\.|$)' = 'src/common/interop/interop-tests/Common.Interop.UnitTests.csproj'
    '^(?:MSTest|Microsoft\.(?:Testing|TestPlatform)|Microsoft\.VisualStudio\.(?:TestPlatform|TestTools|Coverage)|testhost)(?:\.|$)' = 'MSTest / Microsoft.Testing.Platform test infrastructure'
    '^(?:UITestAutomation|UnitTests-[^.]+)(?:\.|$)|(?:^|\.)(?:UnitTests|UITests|Tests)(?:\.|$)' = 'unit/UI test project output'
}
$testArtifacts = @(foreach ($file in $sourceFiles) {
    foreach ($pattern in $testArtifactOrigins.Keys) {
        if ($file.Name -match $pattern) {
            '{0} (source: {1})' -f (Get-RelativePathCompat $sourceRoot $file.FullName), $testArtifactOrigins[$pattern]
            break
        }
    }
})
if ($testArtifacts.Count) {
    throw ("Test artifacts remain outside the excluded harness/test directories; review their build output paths before staging: `n" + ($testArtifacts -join "`n"))
}

$versionedFiles = @(
    'Updater\Kit.Updater.exe',
    'Kit.exe', 'Kit.Awake.exe', 'Kit.Awake.dll',
    'Kit.ManagedCommon.dll', 'Kit.Interop.dll', 'Kit.GPOWrapper.dll', 'Kit.Settings.UI.Lib.dll',
    'Kit.BackgroundActivatorDLL.dll', 'Kit.ActionRunner.exe',
    'Kit.AwakeModuleInterface.dll', 'Kit.LightSwitchModuleInterface.dll',
    'Kit.LocalserverModuleInterface.dll', 'Kit.UDPtestModuleInterface.dll',
    'Kit.NetMapModuleInterface.dll', 'Kit.AIHubModuleInterface.dll', 'Kit.WingetModuleInterface.dll',
    'LightSwitchService\Kit.LightSwitchService.exe',
    'Kit.AIHubWorker.exe',
    'Kit.LocalserverWorker.exe',
    'Kit.NetMapService.exe',
    'Kit.UDPtestService.exe',
    'Kit.WingetService.exe',
    'WinUI3Apps\Kit.Settings.exe', 'WinUI3Apps\Kit.Settings.dll',
    'WinUI3Apps\Kit.QuickAccess.exe', 'WinUI3Apps\Kit.QuickAccess.dll',
    'WinUI3Apps\Kit.AIHubLib.dll', 'WinUI3Apps\Kit.AiHub.dll', 'WinUI3Apps\Kit.ManagedCommon.dll',
    'WinUI3Apps\LocalserverLib.dll', 'WinUI3Apps\UDPtestLib.dll',
    'WinUI3Apps\Kit.NetMapLib.dll', 'WinUI3Apps\Kit.WingetLib.dll'
)
$expectedQuadVersion = "${Version}.0"
$workerFallbacks = @{
    'Kit.AIHubWorker.exe' = 'AIHubWorker\Kit.AIHubWorker.exe'
    'Kit.LocalserverWorker.exe' = 'LocalserverWorker\Kit.LocalserverWorker.exe'
    'Kit.NetMapService.exe' = 'NetMapService\Kit.NetMapService.exe'
    'Kit.UDPtestService.exe' = 'UDPtestService\Kit.UDPtestService.exe'
}
$resolvedVersionedFiles = @{}
foreach ($relative in $versionedFiles) {
    $fullPath = Join-Path $sourceRoot $relative
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        # Match native module launchers: prefer the flat executable, then the
        # supported worker subfolder. Winget only supports the flat layout.
        $altRelative = $workerFallbacks[$relative]
        if ($altRelative -and (Test-Path -LiteralPath (Join-Path $sourceRoot $altRelative) -PathType Leaf)) {
            $fullPath = Join-Path $sourceRoot $altRelative
        } else {
            throw "Versioned file not found: $relative"
        }
    }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($fullPath)
    $binaryVersion = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
    if ($binaryVersion -ne $expectedQuadVersion) { throw "Unexpected binary version for ${relative}: $binaryVersion (expected $expectedQuadVersion)" }
    $resolvedVersionedFiles[$relative] = Get-RelativePathCompat $sourceRoot $fullPath
}

[IO.Directory]::CreateDirectory($destinationRoot) | Out-Null
$manifest = [Collections.Generic.List[object]]::new()
foreach ($file in $sourceFiles) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to copy a redirected runtime file.'
    }
    $relative = Get-RelativePathCompat $sourceRoot $file.FullName
    $targetPath = Join-Path $destinationRoot $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $targetPath)) | Out-Null
    [IO.File]::Copy($file.FullName, $targetPath, $false)
    # Copied apphosts inherit the destination label; preserve normal-user integrity.
    if ($file.Extension -ieq '.exe') {
        & icacls.exe $targetPath /setintegritylevel M /L | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Cannot set executable integrity: $relative" }
    }
    $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) { throw "Runtime file hash mismatch: $relative" }
    $manifest.Add([ordered]@{ Path = $relative; Bytes = $file.Length; Sha256 = $targetHash })
}

$dependencyResults = [Collections.Generic.List[object]]::new()
$sharedLibraryDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$null = $sharedLibraryDirectories.Add('')
$null = $sharedLibraryDirectories.Add('WinUI3Apps')
foreach ($executable in @(
    'Kit.Awake.exe',
    'Kit.AIHubWorker.exe',
    'Kit.LocalserverWorker.exe',
    'Kit.NetMapService.exe',
    'Kit.UDPtestService.exe',
    'Kit.WingetService.exe',
    'WinUI3Apps\Kit.Settings.exe',
    'WinUI3Apps\Kit.QuickAccess.exe'
)) {
    # Resolve dependencies beside the executable that the native launcher will
    # actually start. A deps file in the unused layout must not mask a missing one.
    $runtimeExecutable = $resolvedVersionedFiles[$executable]
    $relative = [IO.Path]::ChangeExtension($runtimeExecutable, '.deps.json')
    $depsPath = Join-Path $destinationRoot $relative
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { throw "Required dependency manifest is missing: $relative" }
    $runtimeConfig = [IO.Path]::ChangeExtension($runtimeExecutable, '.runtimeconfig.json')
    if (-not (Test-Path -LiteralPath (Join-Path $destinationRoot $runtimeConfig) -PathType Leaf)) {
        throw "Required runtime configuration is missing: $runtimeConfig"
    }
    $null = $sharedLibraryDirectories.Add([IO.Path]::GetDirectoryName($relative))
    $depsText = [IO.File]::ReadAllText($depsPath)
    if ($depsText -match '(?i)sqlite') { throw "Unexpected SQLite dependency: $relative" }
    $deps = $depsText | ConvertFrom-Json -AsHashtable
    $target = $deps.targets[$deps.runtimeTarget.name]
    if (-not $target -or $target.Count -eq 0) { throw "Dependency manifest has no active runtime target: $relative" }
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

$sharedLibraryResults = [Collections.Generic.List[object]]::new()
foreach ($name in @('Kit.AIHubLib.dll', 'Kit.AiHub.dll', 'Kit.ManagedCommon.dll')) {
    $referenceRelative = Join-Path 'WinUI3Apps' $name
    $referencePath = Join-Path $destinationRoot $referenceRelative
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) { throw "Required shared library is missing: $referenceRelative" }
    $referenceHash = (Get-FileHash -LiteralPath $referencePath -Algorithm SHA256).Hash
    foreach ($directory in ($sharedLibraryDirectories | Sort-Object)) {
        $relative = if ($directory) { Join-Path $directory $name } else { $name }
        $path = Join-Path $destinationRoot $relative
        # Each deps manifest already requires the libraries used by that process.
        # Also check any root copy, even when a worker uses its subfolder layout.
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        $binaryVersion = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
        if ($binaryVersion -ne $expectedQuadVersion) { throw "Unexpected shared library version for ${relative}: $binaryVersion (expected $expectedQuadVersion)" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($hash -ne $referenceHash) { throw "Shared library differs between $relative and $referenceRelative" }
        $sharedLibraryResults.Add([ordered]@{ Path = $relative; Version = $binaryVersion; Sha256 = $hash })
    }
}

foreach ($relative in @(
    'Kit.pri', 'Kit.Interop.dll', 'Kit.GPOWrapper.dll',
    'Kit.BackgroundActivatorDLL.dll', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
    'Kit.Awake.runtimeconfig.json', 'Assets\Awake\Awake.ico', 'Assets\Winget.png',
    'Microsoft.Management.Deployment.dll', 'Microsoft.Management.Deployment.winmd', 'Winget.ComInterop.NOTICE.txt',
    'WinUI3Apps\Microsoft.Management.Deployment.dll', 'WinUI3Apps\Microsoft.Management.Deployment.winmd', 'WinUI3Apps\Winget.ComInterop.NOTICE.txt',
    'WinUI3Apps\Assets\Settings\Icons\Winget.png', 'WinUI3Apps\Assets\Settings\Modules\Winget.png',
    'WinUI3Apps\Kit.Settings.pri', 'WinUI3Apps\Kit.QuickAccess.pri',
    'WinUI3Apps\Kit.Settings.runtimeconfig.json', 'WinUI3Apps\Kit.QuickAccess.runtimeconfig.json'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $destinationRoot $relative) -PathType Leaf)) {
        throw "Required runtime asset is missing: $relative"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $destinationRoot 'Kit.ActionRunner.exe') -PathType Leaf)) {
    throw "Required runtime asset is missing: Kit.ActionRunner.exe"
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

$portableManifest = [ordered]@{
    version = $Version
    files = @($manifest | ForEach-Object { [ordered]@{ path = $_.Path.Replace('\', '/'); sha256 = $_.Sha256.ToLowerInvariant() } })
}
$portableManifestPath = Join-Path $destinationRoot 'portable-manifest.json'
[IO.File]::WriteAllText($portableManifestPath, ($portableManifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$manifest.Add([ordered]@{ Path = 'portable-manifest.json'; Bytes = (Get-Item -LiteralPath $portableManifestPath).Length; Sha256 = (Get-FileHash -LiteralPath $portableManifestPath -Algorithm SHA256).Hash })

$report = [ordered]@{
    Version = $Version
    Configuration = 'Debug'
    Platform = 'x64'
    Directory = $destinationRoot
    FileCount = $manifest.Count
    TotalBytes = ($manifest | ForEach-Object { $_.Bytes } | Measure-Object -Sum).Sum
    PdbCount = @($manifest | Where-Object { $_.Path.EndsWith('.pdb') }).Count
    DependencyChecks = @($dependencyResults)
    SharedLibraryChecks = @($sharedLibraryResults)
    SparsePackageLogoReferences = $logos.Count
    Files = @($manifest)
}
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
[pscustomobject]$report | Select-Object Version, Configuration, Platform, Directory, FileCount, TotalBytes, PdbCount, SparsePackageLogoReferences
$dependencyResults
Write-Output "SHA-256 manifest: $reportPath"
