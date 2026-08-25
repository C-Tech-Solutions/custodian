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
if ($changelog -notmatch "(?m)^## $([regex]::Escape($Version)) - \d{4}-\d{2}-\d{2}$") {
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

$remoteTag = @(git -C $repo ls-remote --tags origin "refs/tags/$Version" "refs/tags/$Version^{}")
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect remote tag state."
}
if ($remoteTag.Count -ne 0) {
    throw "Tag '$Version' already exists. Release tags are never moved or replaced."
}

gh release view $Version --repo $Repository --json tagName 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    throw "GitHub release '$Version' already exists. Releases and assets are never overwritten."
}

Write-Host "Release preflight passed for Custodian $Version at $normalizedCommit."
