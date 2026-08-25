param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$CommitSha,
    [string]$Repository = "C-Tech-Solutions/custodian"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force
$expectedVersion = "1.5.5"
$normalizedCommit = $CommitSha.ToLowerInvariant()

if ($Version -ne $expectedVersion) {
    throw "This protected workflow may only release Custodian $expectedVersion."
}
if (![string]::IsNullOrWhiteSpace($env:GITHUB_REF) -and $env:GITHUB_REF -ne "refs/heads/master") {
    throw "Release dispatches must run from master, not '$env:GITHUB_REF'."
}
if (![string]::IsNullOrWhiteSpace($env:GITHUB_SHA) -and $env:GITHUB_SHA.ToLowerInvariant() -ne $normalizedCommit) {
    throw "Dispatch SHA '$env:GITHUB_SHA' does not match requested commit '$normalizedCommit'."
}

$head = (git -C $repo rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $head -ne $normalizedCommit) {
    throw "Checked-out HEAD '$head' does not match requested commit '$normalizedCommit'."
}
if (@(git -C $repo status --porcelain).Count -ne 0) {
    throw "Release checkout is not clean."
}

$changelog = Get-Content -Raw -LiteralPath (Join-Path $repo "CHANGELOG.md")
if (!(Test-CustodianDatedChangelogEntry -ChangelogText $changelog -Version $Version)) {
    throw "CHANGELOG.md does not contain a dated $Version release entry."
}

$lockFailures = @()
foreach ($project in Get-ChildItem -LiteralPath $repo -Recurse -File -Filter "*.csproj" |
    Where-Object { $_.FullName -notmatch '[\\/]artifacts[\\/]' }) {
    $projectText = Get-Content -Raw -LiteralPath $project.FullName
    if ($projectText -match '<PackageReference\b' -and
        !(Test-Path -LiteralPath (Join-Path $project.DirectoryName "packages.lock.json") -PathType Leaf)) {
        $lockFailures += $project.FullName
    }
}
if ($lockFailures.Count -gt 0) {
    throw "Projects with package dependencies are missing lock files: $($lockFailures -join ', ')"
}

$tagRef = "refs/tags/$Version"
$remoteTag = @(git -C $repo ls-remote --tags origin $tagRef "$tagRef^{}" | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect remote tag state."
}
if ($remoteTag.Count -ne 0) {
    $directTag = @($remoteTag | Where-Object { ($_ -split '\s+')[1] -ceq $tagRef })
    $peeledTag = @($remoteTag | Where-Object { ($_ -split '\s+')[1] -ceq "$tagRef^{}" })
    if ($directTag.Count -ne 1 -or
        $peeledTag.Count -ne 1 -or
        ($peeledTag[0] -split '\s+')[0].ToLowerInvariant() -ne $normalizedCommit) {
        throw "Existing tag '$Version' is not an annotated tag at '$normalizedCommit'. Release tags are never moved or replaced."
    }
}

$existingReleases = @(Get-CustodianGitHubReleasesByTag -Repository $Repository -Version $Version)
$resumePublished = $false
if ($existingReleases.Count -gt 1) {
    throw "Multiple GitHub releases exist for '$Version'. Releases and assets are never overwritten."
}
if ($existingReleases.Count -eq 1) {
    $existingRelease = $existingReleases[0]
    if ($existingRelease.draft -or !$existingRelease.immutable) {
        throw "GitHub release '$Version' already exists but is not published and immutable. Releases and assets are never overwritten."
    }
    $resumePublished = $true
    Write-Host "Existing published immutable release '$Version' will be re-verified without rebuilding or republishing."
}

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "resume_published=$($resumePublished.ToString().ToLowerInvariant())" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

Write-Host "Release preflight passed for Custodian $Version at $normalizedCommit."
