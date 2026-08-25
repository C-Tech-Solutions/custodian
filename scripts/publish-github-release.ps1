param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$Repository = "C-Tech-Solutions/custodian"
)

$ErrorActionPreference = "Stop"
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force

if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw "GH_TOKEN must be available to GitHub CLI through the environment."
}

$releaseMatches = @(Get-CustodianGitHubReleasesByTag -Repository $Repository -Version $Version)
if ($releaseMatches.Count -ne 1) {
    throw "Expected exactly one GitHub release tagged '$Version'; found $($releaseMatches.Count)."
}

$release = $releaseMatches[0]
if ($release.draft) {
    gh release edit $Version --repo $Repository --draft=false --latest
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI failed to publish release '$Version'."
    }
    Write-Host "Published Custodian $Version as Latest."
}
elseif ($release.immutable) {
    Write-Host "Custodian $Version is already published and immutable; resuming final verification without editing it."
}
else {
    throw "Release '$Version' is already published but is not immutable. It will not be edited or replaced."
}
