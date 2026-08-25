param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputRoot = "artifacts\velopack",
    [switch]$IncludeAncillaryAssets
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force

$output = (Resolve-Path -LiteralPath (Join-Path $repo $OutputRoot)).Path
$expectedNames = if ($IncludeAncillaryAssets) {
    @(Get-CustodianReleaseAssetNames -Version $Version)
}
else {
    @(Get-CustodianVelopackAssetNames -Version $Version)
}
$actualFiles = @(Get-ChildItem -LiteralPath $output -File)
$actualNames = @($actualFiles.Name)

$missing = @($expectedNames | Where-Object { $_ -notin $actualNames })
$unexpected = @($actualNames | Where-Object { $_ -notin $expectedNames })
if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
    throw "Release asset set mismatch. Missing: [$($missing -join ', ')]. Unexpected: [$($unexpected -join ', ')]."
}

foreach ($file in $actualFiles) {
    if ($file.Length -le 0) {
        throw "Release asset '$($file.Name)' is empty."
    }
}

$packageName = "Custodian.DiskAnalyzer-$Version-full.nupkg"
$releasesText = Get-Content -Raw -LiteralPath (Join-Path $output "RELEASES")
if (!$releasesText.Contains($packageName, [StringComparison]::Ordinal)) {
    throw "RELEASES does not reference '$packageName'."
}

$releaseIndex = Get-Content -Raw -LiteralPath (Join-Path $output "releases.win.json") | ConvertFrom-Json -Depth 20
$releaseIndexText = $releaseIndex | ConvertTo-Json -Depth 20 -Compress
if (!$releaseIndexText.Contains($Version, [StringComparison]::Ordinal)) {
    throw "releases.win.json does not reference version '$Version'."
}

$setupPath = Join-Path $output "Custodian.DiskAnalyzer-win-Setup.exe"
$setupSignature = Get-AuthenticodeSignature -LiteralPath $setupPath
if ($setupSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $setupSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=C-Tech Solutions LLC(?:,|$)') {
    throw "Setup.exe is not validly signed by C-Tech Solutions LLC: $($setupSignature.StatusMessage)"
}

$portablePath = Join-Path $output "Custodian.DiskAnalyzer-win-Portable.zip"
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("custodian-portable-verify-{0}" -f [Guid]::NewGuid().ToString("N"))
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($portablePath, $temporaryDirectory)
    $portablePeFiles = @(Get-ChildItem -LiteralPath $temporaryDirectory -Recurse -File |
        Where-Object { $_.Extension -in @(".dll", ".exe") })
    if ($portablePeFiles.Count -eq 0) {
        throw "Portable release contains no PE files."
    }

    foreach ($file in $portablePeFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
            throw "Portable PE '$($file.FullName)' failed Authenticode verification: $($signature.StatusMessage)"
        }
    }

    Write-Host "Verified $($portablePeFiles.Count) portable PE signatures."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory -PathType Container) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

Write-Host "Verified release asset set for Custodian $Version."
