param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCommit,
    [string]$OutputRoot = "artifacts\velopack",
    [string]$Repository = "C-Tech-Solutions/custodian",
    [string]$NotesPath
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force

if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw "GH_TOKEN must be available to GitHub CLI through the environment."
}

$output = (Resolve-Path -LiteralPath (Join-Path $repo $OutputRoot)).Path
if ([string]::IsNullOrWhiteSpace($NotesPath)) {
    $NotesPath = Join-Path $repo "docs\releases\$Version.md"
}
$resolvedNotes = (Resolve-Path -LiteralPath $NotesPath).Path
$normalizedCommit = $ExpectedCommit.ToLowerInvariant()

git -C $repo show-ref --verify --quiet "refs/tags/$Version"
if ($LASTEXITCODE -ne 0) {
    throw "Verified local tag '$Version' is required before creating a release."
}
if ((git -C $repo cat-file -t $Version).Trim() -ne "tag") {
    throw "Release tag '$Version' must be annotated."
}
if ((git -C $repo rev-list -n 1 $Version).Trim().ToLowerInvariant() -ne $normalizedCommit) {
    throw "Release tag '$Version' does not resolve to '$normalizedCommit'."
}

$remoteTarget = @(git -C $repo ls-remote --tags origin "refs/tags/$Version^{}")
if ($remoteTarget.Count -ne 1 -or ($remoteTarget[0] -split '\s+')[0].ToLowerInvariant() -ne $normalizedCommit) {
    throw "Remote tag '$Version' is missing or does not resolve to '$normalizedCommit'."
}

gh release view $Version --repo $Repository --json tagName 2>$null | Out-Null
$releaseExists = $LASTEXITCODE -eq 0
Assert-CustodianReleaseAbsent -ReleaseExists $releaseExists -Version $Version

$assetPaths = foreach ($assetName in Get-CustodianReleaseAssetNames -Version $Version) {
    $assetPath = Join-Path $output $assetName
    if (!(Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Required release asset was not found: '$assetPath'."
    }
    $assetPath
}

$arguments = New-CustodianDraftReleaseArguments `
    -Repository $Repository `
    -Version $Version `
    -NotesPath $resolvedNotes `
    -AssetPaths $assetPaths
gh @arguments
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI failed to create the draft release."
}

$release = gh release view $Version --repo $Repository --json isDraft,tagName,url | ConvertFrom-Json
if (!$release.isDraft -or $release.tagName -ne $Version) {
    throw "GitHub release '$Version' was not created as a draft."
}

Write-Host "Draft release: $($release.url)"
