param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCommit,
    [Parameter(Mandatory = $true)]
    [string]$AssetRoot,
    [string]$Repository = "C-Tech-Solutions/custodian",
    [switch]$RequireDraft,
    [switch]$RequirePublishedImmutable,
    [switch]$VerifyAttestations
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force

if ($RequireDraft -and $RequirePublishedImmutable) {
    throw "Release verification cannot require both draft and published states."
}

$normalizedCommit = $ExpectedCommit.ToLowerInvariant()
$remoteTarget = @(git -C $repo ls-remote --tags origin "refs/tags/$Version^{}")
if ($remoteTarget.Count -ne 1 -or ($remoteTarget[0] -split '\s+')[0].ToLowerInvariant() -ne $normalizedCommit) {
    throw "Remote annotated tag '$Version' does not resolve to '$normalizedCommit'."
}

$releaseMatches = @(Get-CustodianGitHubReleasesByTag -Repository $Repository -Version $Version)
if ($releaseMatches.Count -ne 1) {
    throw "Expected exactly one GitHub release tagged '$Version'; found $($releaseMatches.Count)."
}
$release = $releaseMatches[0]
if ($RequireDraft -and !$release.draft) {
    throw "Release '$Version' is not a draft."
}
if ($RequirePublishedImmutable -and ($release.draft -or !$release.immutable)) {
    throw "Release '$Version' is not published and immutable."
}

$expectedNames = @(Get-CustodianReleaseAssetNames -Version $Version)
$remoteNames = @($release.assets.name)
$missingRemote = @($expectedNames | Where-Object { $_ -notin $remoteNames })
$unexpectedRemote = @($remoteNames | Where-Object { $_ -notin $expectedNames })
if ($missingRemote.Count -gt 0 -or $unexpectedRemote.Count -gt 0) {
    throw "GitHub asset set mismatch. Missing: [$($missingRemote -join ', ')]. Unexpected: [$($unexpectedRemote -join ', ')]."
}

$resolvedAssets = (Resolve-Path -LiteralPath $AssetRoot).Path
$checksumPath = Join-Path $resolvedAssets "SHA256SUMS.txt"
$checksumLines = @(Get-Content -LiteralPath $checksumPath)
$checksummedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in $checksumLines) {
    if ($line -notmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^\\/]+)$') {
        throw "Invalid checksum line: '$line'."
    }

    $assetName = $Matches.name
    $assetPath = Join-Path $resolvedAssets $assetName
    if (!(Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Checksummed asset is missing: '$assetName'."
    }
    if (!$checksummedNames.Add($assetName)) {
        throw "Duplicate checksum entry for '$assetName'."
    }

    $actual = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Matches.hash) {
        throw "Checksum mismatch for '$assetName'."
    }
}

$expectedChecksummedNames = @($expectedNames | Where-Object { $_ -ne "SHA256SUMS.txt" })
if (@($expectedChecksummedNames | Where-Object { !$checksummedNames.Contains($_) }).Count -ne 0 -or
    $checksummedNames.Count -ne $expectedChecksummedNames.Count) {
    throw "SHA256SUMS.txt does not cover the exact non-checksum release asset set."
}

foreach ($asset in $release.assets) {
    $localPath = Join-Path $resolvedAssets $asset.name
    if (!(Test-Path -LiteralPath $localPath -PathType Leaf)) {
        throw "Downloaded release asset is missing: '$($asset.name)'."
    }

    $localDigest = "sha256:$((Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant())"
    if ($asset.digest -ne $localDigest) {
        throw "GitHub digest mismatch for '$($asset.name)'."
    }

    if ($VerifyAttestations) {
        gh attestation verify $localPath `
            --repo $Repository `
            --signer-workflow "$Repository/.github/workflows/release.yml" `
            --source-digest $normalizedCommit `
            --source-ref "refs/heads/master" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Artifact attestation verification failed for '$($asset.name)'."
        }
    }
}

Write-Host "Verified GitHub release $Version at $normalizedCommit."
