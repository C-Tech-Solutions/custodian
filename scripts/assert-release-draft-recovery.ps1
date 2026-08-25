param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$WorkflowCommitSha,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$SourceCommitSha,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 9223372036854775807)]
    [Int64]$DraftId,
    [string]$Repository = "C-Tech-Solutions/custodian"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force
$expectedVersion = "1.5.5"
$normalizedWorkflowCommit = $WorkflowCommitSha.ToLowerInvariant()
$normalizedSourceCommit = $SourceCommitSha.ToLowerInvariant()

if ($Version -ne $expectedVersion) {
    throw "This protected workflow may only recover Custodian $expectedVersion."
}
if (![string]::IsNullOrWhiteSpace($env:GITHUB_REF) -and $env:GITHUB_REF -ne "refs/heads/master") {
    throw "Release recovery must run from master, not '$env:GITHUB_REF'."
}
if (![string]::IsNullOrWhiteSpace($env:GITHUB_SHA) -and $env:GITHUB_SHA.ToLowerInvariant() -ne $normalizedWorkflowCommit) {
    throw "Dispatch SHA '$env:GITHUB_SHA' does not match workflow commit '$normalizedWorkflowCommit'."
}

$head = (git -C $repo rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $head -ne $normalizedWorkflowCommit) {
    throw "Checked-out workflow HEAD '$head' does not match '$normalizedWorkflowCommit'."
}
if (@(git -C $repo status --porcelain).Count -ne 0) {
    throw "Release recovery checkout is not clean."
}

Assert-CustodianRemoteReleaseTagIdentity `
    -RepositoryRoot $repo `
    -Version $Version `
    -ExpectedCommit $normalizedSourceCommit

$release = gh api "repos/$Repository/releases/$DraftId" | ConvertFrom-Json -Depth 50
if ($LASTEXITCODE -ne 0 -or $null -eq $release) {
    throw "Unable to retrieve draft release '$DraftId'."
}
$notes = Get-Content -Raw -LiteralPath (Join-Path $repo "docs\releases\$Version.md")
Assert-CustodianEmptyDraftRelease `
    -Release $release `
    -DraftId $DraftId `
    -Version $Version `
    -ExpectedTitle "Custodian $Version" `
    -ExpectedBody $notes

$tagMatches = @(Get-CustodianGitHubReleasesByTag -Repository $Repository -Version $Version)
if ($tagMatches.Count -ne 1 -or [Int64]$tagMatches[0].id -ne $DraftId) {
    throw "Draft release '$DraftId' is not the unique GitHub release for '$Version'."
}

Write-Host "Release recovery preflight passed for empty draft $DraftId, source $normalizedSourceCommit, workflow $normalizedWorkflowCommit."
