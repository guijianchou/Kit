param (
    [string]$certSubject = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
    [Parameter(Mandatory = $true)]
    [string[]]$TargetPaths
)

. "$PSScriptRoot\cert-management.ps1"
$cert = EnsureCertificate -certSubject $certSubject

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
    & signtool sign /sha1 $($cert.Thumbprint) /fd SHA256 /t http://timestamp.digicert.com "$filePath"
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
