<#
.SYNOPSIS
Shared build helper functions for PowerToys build scripts.

.DESCRIPTION
This file provides reusable helper functions used by the build scripts:
- Get-BuildPaths: returns ScriptDir, OriginalCwd, RepoRoot (repo root detection)
- RunMSBuild: wrapper around msbuild.exe (accepts optional Platform/Configuration)
- RestoreThenBuild: performs restore and optionally builds the solution/project
- BuildProjectsInDirectory: discovers and builds local .sln/.slnx/.slnf/.csproj/.vcxproj files
- Ensure-VsDevEnvironment: initializes the Visual Studio developer environment when possible.
  It prefers the DevShell PowerShell module (Microsoft.VisualStudio.DevShell.dll / Enter-VsDevShell),
  falls back to running VsDevCmd.bat and importing its environment into the current PowerShell session,
  and restores the caller's working directory after initialization.

USAGE
Dot-source this file from a script to load helpers:
. "$PSScriptRoot\build-common.ps1"

ERROR DETAILS
When a build fails, check the logs written next to the solution/project folder:
- build.<configuration>.<platform>.all.log — full MSBuild text log
- build.<configuration>.<platform>.errors.log — extracted errors only
- build.<configuration>.<platform>.warnings.log — extracted warnings only
- build.<configuration>.<platform>.trace.binlog — binary log (open with the MSBuild Structured Log Viewer)

.NOTES
Do not execute this file directly; dot-source it from `build.ps1` or `build-installer.ps1` so helpers are available in your script scope.
#>

function Normalize-ProcessPathEnvironment {
    $processEnvironment = [Environment]::GetEnvironmentVariables('Process')
    $pathKeys = @($processEnvironment.Keys | Where-Object { $_ -ieq 'Path' })
    if ($pathKeys.Count -eq 0) {
        return
    }

    $pathValue = $null
    foreach ($pathKey in $pathKeys) {
        $candidate = [Environment]::GetEnvironmentVariable($pathKey, 'Process')
        if ([string]::IsNullOrEmpty($candidate)) {
            continue
        }

        if (($candidate -match 'Visual Studio|MSBuild') -or [string]::IsNullOrEmpty($pathValue) -or $candidate.Length -gt $pathValue.Length) {
            $pathValue = $candidate
        }
    }

    if ([string]::IsNullOrEmpty($pathValue)) {
        return
    }

    [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
    [Environment]::SetEnvironmentVariable('Path', $pathValue, 'Process')
}

function RunMSBuild {
    param (
        [string]$Solution,
        [string[]]$ExtraArgs = @(),
        [string]$Platform,
        [string]$Configuration
    )

    # Prefer the solution's folder for logs; fall back to current directory
    $logRoot = Split-Path -Path $Solution
    if (-not $logRoot) { $logRoot = '.' }

    $cfg = $null
    if ($Configuration) { $cfg = $Configuration.ToLower() } else { $cfg = 'unknown' }
    $plat = $null
    if ($Platform) { $plat = $Platform.ToLower() } else { $plat = 'unknown' }

    $allLog = Join-Path $logRoot ("build.{0}.{1}.all.log" -f $cfg, $plat)
    $warningLog = Join-Path $logRoot ("build.{0}.{1}.warnings.log" -f $cfg, $plat)
    $errorsLog = Join-Path $logRoot ("build.{0}.{1}.errors.log" -f $cfg, $plat)
    $binLog = Join-Path $logRoot ("build.{0}.{1}.trace.binlog" -f $cfg, $plat)

    $base = @(
        $Solution
        "/p:Platform=$Platform"
        "/p:Configuration=$Configuration"
        "/verbosity:normal"
        '/clp:Summary;PerformanceSummary;ErrorsOnly;WarningsOnly'
        "/fileLoggerParameters:LogFile=$allLog;Verbosity=detailed"
        "/fileLoggerParameters1:LogFile=$warningLog;WarningsOnly"
        "/fileLoggerParameters2:LogFile=$errorsLog;ErrorsOnly"
        "/bl:$binLog"
        '/nologo'
        '/nodeReuse:false'
    )

    $extra = @($ExtraArgs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $cmd = $base + $extra
    Write-Host (("[MSBUILD] {0}" -f ($cmd -join ' ')))

    $msbuildExe = $script:MSBuildExe
    if (-not $msbuildExe) {
        $msbuildCommand = Get-Command msbuild.exe -ErrorAction SilentlyContinue
        if ($msbuildCommand) {
            $msbuildExe = $msbuildCommand.Source
        }
        else {
            $msbuildExe = 'msbuild.exe'
        }
    }

    Push-Location $script:RepoRoot
    try {
        & $msbuildExe @cmd
        if ($LASTEXITCODE -ne 0) {
            Write-Error (("Build failed: {0}  {1}`nSee logs:`n  All: {2}`n  Errors: {3}`n  Binlog: {4}" -f $Solution, $ExtraArgs, $allLog, $errorsLog, $binLog))
            exit $LASTEXITCODE
        }
    } finally {
        Pop-Location
    }
}

function RestoreThenBuild {
    param (
        [string]$Solution,
        [string[]]$ExtraArgs = @(),
        [string]$Platform,
        [string]$Configuration,
        [bool]$RestoreOnly=$false
    )

    $restoreArgs = @('/t:restore', '/p:RestorePackagesConfig=true') + @($ExtraArgs)
    RunMSBuild -Solution $Solution -ExtraArgs $restoreArgs -Platform $Platform -Configuration $Configuration

    if (-not $RestoreOnly) {
        $buildArgs = @('/m') + @($ExtraArgs)
        RunMSBuild -Solution $Solution -ExtraArgs $buildArgs -Platform $Platform -Configuration $Configuration
    }
}

function BuildProjectsInDirectory {
    param(
        [string]$DirectoryPath,
        [string[]]$ExtraArgs = @(),
        [string]$Platform,
        [string]$Configuration,
        [switch]$RestoreOnly
    )

    if (-not (Test-Path $DirectoryPath)) {
        return $false
    }

    $files = @()
    try {
        $files = Get-ChildItem -Path (Join-Path $DirectoryPath '*') -Include *.sln,*.slnx,*.slnf,*.csproj,*.vcxproj -File -ErrorAction SilentlyContinue
    } catch {
        $files = @()
    }

    if (-not $files -or $files.Count -eq 0) {
        return $false
    }

    $names = ($files | ForEach-Object { $_.Name }) -join ', '
    Write-Host ("[LOCAL BUILD] Found {0} project(s) in {1}: {2}" -f $files.Count, $DirectoryPath, $names)

    $preferredOrder = @('.sln', '.slnx', '.slnf', '.csproj', '.vcxproj')
    $solutionExtensions = @('.sln', '.slnx', '.slnf')
    $files = $files | Sort-Object @{Expression = { [array]::IndexOf($preferredOrder, $_.Extension.ToLower()) }}

    foreach ($f in $files) {
        Write-Host ("[LOCAL BUILD] Building {0}" -f $f.FullName)
        if ($solutionExtensions -contains $f.Extension.ToLowerInvariant()) {
            RestoreThenBuild -Solution $f.FullName -ExtraArgs $ExtraArgs -Platform $Platform -Configuration $Configuration -RestoreOnly:$RestoreOnly
        } else {
            $buildArgs = @('/m') + @($ExtraArgs)
            RunMSBuild -Solution $f.FullName -ExtraArgs $buildArgs -Platform $Platform -Configuration $Configuration
        }
    }

    return $true
}

function Get-DefaultPlatform {
    <#
    Returns a default target platform string based on the host machine (x64, arm64, x86).
    #>
    try {
        $envArch = $env:PROCESSOR_ARCHITECTURE
        if ($envArch) { $envArch = $envArch.ToLower() }
        if ($envArch -eq 'amd64' -or $envArch -eq 'x86_64') { return 'x64' }
        if ($envArch -match 'arm64') { return 'arm64' }
        if ($envArch -eq 'x86') { return 'x86' }

        if ($env:PROCESSOR_ARCHITEW6432) {
            $envArch2 = $env:PROCESSOR_ARCHITEW6432.ToLower()
            if ($envArch2 -eq 'amd64') { return 'x64' }
            if ($envArch2 -match 'arm64') { return 'arm64' }
        }

        try {
            $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
            switch ($osArch.ToString().ToLower()) {
                'x64' { return 'x64' }
                'arm64' { return 'arm64' }
                'x86' { return 'x86' }
            }
        } catch {
            # ignore - RuntimeInformation may not be available
        }
    } catch {
        # ignore any errors and fall back
    }

    return 'x64'
}

function Ensure-VsDevEnvironment {
    $OriginalLocationForVsInit = Get-Location
    try {
    Normalize-ProcessPathEnvironment

    if ($env:VSINSTALLDIR -or $env:VCINSTALLDIR -or $env:DevEnvDir -or $env:VCToolsInstallDir) {
        Write-Host "[VS] VS developer environment already present"
        $existingMsbuild = Get-Command msbuild.exe -ErrorAction SilentlyContinue
        if ($existingMsbuild) {
            $script:MSBuildExe = $existingMsbuild.Source
        }
        return $true
    }

    # Locate vswhere if available
    $vswhereCandidates = @(
        "$env:ProgramFiles (x86)\Microsoft Visual Studio\Installer\vswhere.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\Installer\vswhere.exe"
    )
    $vswhere = $vswhereCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($vswhere) { Write-Host "[VS] vswhere found: $vswhere" } else { Write-Host "[VS] vswhere not found" }

    $instPaths = @()
    if ($vswhere) {
        # First try with the VC tools requirement (preferred)
        try { $p = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null; if ($p) { $instPaths += $p } } catch {}
        # Fallback: try without -requires to find any VS installations
        if (-not $instPaths) {
            try { $p2 = & $vswhere -latest -products * -property installationPath 2>$null; if ($p2) { $instPaths += $p2 } } catch {}
        }
    }

    # Add explicit common year-based candidates as a last resort
    if (-not $instPaths) {
        $explicit = @(
            "$env:ProgramFiles (x86)\Microsoft Visual Studio\2022\Community",
            "$env:ProgramFiles (x86)\Microsoft Visual Studio\2022\Professional",
            "$env:ProgramFiles (x86)\Microsoft Visual Studio\2022\Enterprise",
            "$env:ProgramFiles\Microsoft Visual Studio\2022\Community",
            "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional",
            "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise"
        )
        foreach ($c in $explicit) { if (Test-Path $c) { $instPaths += $c } }
    }

    if (-not $instPaths -or $instPaths.Count -eq 0) {
        Write-Warning "[VS] Could not locate Visual Studio installation (no candidates found)"
        return $false
    }

    # Try each candidate installation path until one works
    foreach ($inst in $instPaths) {
        if (-not $inst) { continue }
        Write-Host "[VS] Checking candidate: $inst"

        $devDll = Join-Path $inst 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
        if (Test-Path $devDll) {
            try {
                Import-Module $devDll -DisableNameChecking -ErrorAction Stop

                # Call Enter-VsDevShell using only the install path to avoid parameter name differences
                try {
                    Enter-VsDevShell -VsInstallPath $inst -ErrorAction Stop
                    Write-Host "[VS] Entered Visual Studio DevShell at $inst"
                    return $true
                } catch {
                    Write-Warning ("[VS] DevShell import/Enter-VsDevShell failed: {0}" -f $_)
                }
            } catch {
                Write-Warning ("[VS] DevShell import failed: {0}" -f $_)
            }
        }

        $vsDevCmd = Join-Path $inst 'Common7\Tools\VsDevCmd.bat'
        if (Test-Path $vsDevCmd) {
            Write-Host "[VS] Running VsDevCmd.bat and importing environment from $vsDevCmd"
            try {
                $cmdOut = cmd.exe /c "`"$vsDevCmd`" && set"
                $envVars = @{}
                foreach ($line in $cmdOut) {
                    $parts = $line -split('=',2)
                    if ($parts.Length -eq 2) {
                        $name = $parts[0]
                        $value = $parts[1]
                        if ($name -ieq 'Path' -and $envVars.ContainsKey('Path')) {
                            $existingPath = $envVars['Path']
                            if (($value -match 'Visual Studio|MSBuild') -or ($existingPath -notmatch 'Visual Studio|MSBuild' -and $value.Length -gt $existingPath.Length)) {
                                $envVars['Path'] = $value
                            }
                        }
                        elseif ($name -ieq 'Path') {
                            $envVars['Path'] = $value
                        }
                        else {
                            $envVars[$name] = $value
                        }
                    }
                }
                foreach ($entry in $envVars.GetEnumerator()) {
                    try { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') } catch {}
                }
                Normalize-ProcessPathEnvironment

                $msbuildCommand = Get-Command msbuild.exe -ErrorAction SilentlyContinue
                if (-not $msbuildCommand) {
                    $msbuildCandidates = @(
                        (Join-Path $inst 'MSBuild\Current\Bin\amd64\MSBuild.exe'),
                        (Join-Path $inst 'MSBuild\Current\Bin\MSBuild.exe')
                    )
                    $msbuildPath = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
                    if ($msbuildPath) {
                        $script:MSBuildExe = $msbuildPath
                        $env:Path = "{0};{1}" -f (Split-Path -Parent $msbuildPath), $env:Path
                        Normalize-ProcessPathEnvironment
                    }
                }
                else {
                    $script:MSBuildExe = $msbuildCommand.Source
                }

                Write-Host "[VS] Imported environment from VsDevCmd.bat at $inst"
                return $true
            } catch {
                Write-Warning ("[VS] Failed to run/import VsDevCmd.bat at {0}: {1}" -f $inst, $_)
            }
        }
    }

    Write-Warning "[VS] Neither DevShell module nor VsDevCmd.bat found in any candidate paths"
    return $false

    } finally {
        try { Set-Location $OriginalLocationForVsInit } catch {}
    }
}
