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
$packagePath = Join-Path $output $packageName
$packageInfo = Get-Item -LiteralPath $packagePath
$packageSha1 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA1).Hash.ToUpperInvariant()
$packageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToUpperInvariant()

$releaseLines = @(Get-Content -LiteralPath (Join-Path $output "RELEASES") | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
if ($releaseLines.Count -ne 1 -or
    $releaseLines[0] -notmatch '^(?<sha1>[0-9A-Fa-f]{40})\s+(?<file>\S+)\s+(?<size>\d+)$') {
    throw "RELEASES must contain exactly one valid package record."
}
if ($Matches.sha1.ToUpperInvariant() -ne $packageSha1 -or
    $Matches.file -cne $packageName -or
    [UInt64]$Matches.size -ne [UInt64]$packageInfo.Length) {
    throw "RELEASES package identity, digest, or size does not match '$packageName'."
}

$releaseIndex = Get-Content -Raw -LiteralPath (Join-Path $output "releases.win.json") | ConvertFrom-Json -Depth 20
$indexAssets = @($releaseIndex.Assets)
if ($indexAssets.Count -ne 1) {
    throw "releases.win.json must contain exactly one asset."
}
$indexAsset = $indexAssets[0]
if ($indexAsset.PackageId -cne "Custodian.DiskAnalyzer" -or
    $indexAsset.Version -cne $Version -or
    $indexAsset.Type -cne "Full" -or
    $indexAsset.FileName -cne $packageName -or
    $indexAsset.SHA1.ToUpperInvariant() -ne $packageSha1 -or
    $indexAsset.SHA256.ToUpperInvariant() -ne $packageSha256 -or
    [UInt64]$indexAsset.Size -ne [UInt64]$packageInfo.Length) {
    throw "releases.win.json package identity, digests, or size do not match '$packageName'."
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

    $portableRootExecutables = @($portablePeFiles | Where-Object {
        $_.Extension -eq ".exe" -and
        -not [IO.Path]::GetDirectoryName([IO.Path]::GetRelativePath($temporaryDirectory, $_.FullName))
    })
    if ($portableRootExecutables.Count -eq 0) {
        throw "Portable release contains no Velopack root executables."
    }
    foreach ($file in $portableRootExecutables) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=C-Tech Solutions LLC(?:,|$)') {
            throw "Portable root executable '$($file.Name)' is not signed by C-Tech Solutions LLC."
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
