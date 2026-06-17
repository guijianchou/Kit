param (
    [string]$certSubject = "CN=Kit Dev",
    [switch]$RequireMachineRoot,
    [Parameter(Mandatory = $true)]
    [string[]]$TargetPaths
)

. "$PSScriptRoot\cert-management.ps1" -certSubject $certSubject -RequireMachineRoot:$RequireMachineRoot

function Find-SignTool {
    $signTool = Get-Command "signtool" -ErrorAction SilentlyContinue
    if ($signTool) {
        return $signTool.Source
    }

    $kitsRootPaths = @(
        "C:\Program Files (x86)\Windows Kits\10\bin",
        "C:\Program Files\Windows Kits\10\bin"
    )

    foreach ($root in $kitsRootPaths) {
        if (-not (Test-Path $root)) {
            continue
        }

        $versions = Get-ChildItem -Path $root -Directory | Where-Object {
            $_.Name -match '^\d+\.\d+\.\d+\.\d+$'
        } | Sort-Object Name -Descending

        foreach ($version in $versions) {
            foreach ($architecture in @("x64", "x86", "arm64")) {
                $candidatePath = Join-Path -Path $version.FullName -ChildPath $architecture
                $exePath = Join-Path -Path $candidatePath -ChildPath "signtool.exe"
                if (Test-Path $exePath) {
                    return $exePath
                }
            }
        }
    }

    return $null
}

$signToolPath = Find-SignTool
if (-not $signToolPath) {
    Write-Error "SignTool not found. Please ensure Windows SDK is installed."
    exit 1
}

$cert = EnsureCertificate -certSubject $certSubject -RequireMachineRoot:$RequireMachineRoot

if (-not $cert) {
    Write-Error "Failed to prepare certificate."
    exit 1
}

Write-Host "Certificate ready: $($cert.Thumbprint)"

if (-not $TargetPaths -or $TargetPaths.Count -eq 0) {
    Write-Error "No target files provided to sign."
    exit 1
}

$signedCount = 0
foreach ($filePath in $TargetPaths) {
    if (-not (Test-Path $filePath)) {
        Write-Error "Target file does not exist: $filePath"
        exit 1
    }

    Write-Host "Signing: $filePath"
    & $signToolPath sign /sha1 $($cert.Thumbprint) /fd SHA256 /t http://timestamp.digicert.com "$filePath"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Signing failed for: $filePath"
        exit $LASTEXITCODE
    }

    $signedCount++
}

if ($signedCount -eq 0) {
    Write-Error "No files were signed."
    exit 1
}
