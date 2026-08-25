param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
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
$tagRef = "refs/tags/$Version"

git -C $repo show-ref --verify --quiet $tagRef
if ($LASTEXITCODE -ne 0) {
    throw "Verified local tag '$Version' is required before creating a release."
}
if ((git -C $repo cat-file -t $tagRef).Trim() -ne "tag") {
    throw "Release tag '$Version' must be annotated."
}
if ((git -C $repo rev-list -n 1 $tagRef).Trim().ToLowerInvariant() -ne $normalizedCommit) {
    throw "Release tag '$Version' does not resolve to '$normalizedCommit'."
}

$remoteTarget = @(git -C $repo ls-remote --tags origin "$tagRef^{}")
if ($remoteTarget.Count -ne 1 -or ($remoteTarget[0] -split '\s+')[0].ToLowerInvariant() -ne $normalizedCommit) {
    throw "Remote tag '$Version' is missing or does not resolve to '$normalizedCommit'."
}

$existingReleases = @(Get-CustodianGitHubReleasesByTag -Repository $Repository -Version $Version)
Assert-CustodianReleaseAbsent -ReleaseExists ($existingReleases.Count -ne 0) -Version $Version

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
    -NotesPath $resolvedNotes

$createdReleaseId = $null
$draftCreationStartedAt = [DateTimeOffset]::UtcNow
try {
    gh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI failed to create the empty draft release."
    }

    $createdRelease = Wait-CustodianGitHubReleaseByTag -Repository $Repository -Version $Version
    if ($null -eq $createdRelease -or !$createdRelease.draft) {
        throw "GitHub release '$Version' was not uniquely created as a draft."
    }
    $createdReleaseId = [Int64]$createdRelease.id

    foreach ($assetPath in $assetPaths) {
        $uploaded = $false
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            gh release upload $Version --repo $Repository -- $assetPath
            if ($LASTEXITCODE -eq 0) {
                $uploaded = $true
                break
            }
            if ($attempt -lt 3) {
                Start-Sleep -Seconds (2 * $attempt)
            }
        }
        if (!$uploaded) {
            throw "Failed to upload '$([IO.Path]::GetFileName($assetPath))' after three attempts."
        }
    }

    $releaseMatches = @(Get-CustodianGitHubReleasesByTag -Repository $Repository -Version $Version)
    if ($releaseMatches.Count -ne 1 -or !$releaseMatches[0].draft -or [Int64]$releaseMatches[0].id -ne $createdReleaseId) {
        throw "GitHub draft '$Version' changed unexpectedly during upload."
    }

    $expectedNames = @($assetPaths | ForEach-Object { [IO.Path]::GetFileName($_) })
    $remoteNames = @($releaseMatches[0].assets.name)
    if (@($expectedNames | Where-Object { $_ -notin $remoteNames }).Count -ne 0 -or
        @($remoteNames | Where-Object { $_ -notin $expectedNames }).Count -ne 0) {
        throw "GitHub draft '$Version' does not contain the exact release asset set."
    }

    foreach ($asset in $releaseMatches[0].assets) {
        $assetPath = Join-Path $output $asset.name
        $expectedDigest = "sha256:$((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant())"
        if ($asset.digest -ne $expectedDigest) {
            throw "GitHub digest mismatch after upload for '$($asset.name)'."
        }
    }

    Write-Host "Draft release: $($releaseMatches[0].html_url)"
}
catch {
    $originalFailure = $_.Exception.Message
    $cleanupFailure = $null
    try {
        $cleanupMatches = @(Get-CustodianGitHubReleasesByTag -Repository $Repository -Version $Version)
        if ($cleanupMatches.Count -eq 1 -and $cleanupMatches[0].draft) {
            $candidateId = [Int64]$cleanupMatches[0].id
            $candidateCreatedAt = [DateTimeOffset]::Parse($cleanupMatches[0].created_at)
            if (($null -ne $createdReleaseId -and $candidateId -eq $createdReleaseId) -or
                ($null -eq $createdReleaseId -and $candidateCreatedAt -ge $draftCreationStartedAt.AddSeconds(-5))) {
                gh api --method DELETE "repos/$Repository/releases/$candidateId"
                if ($LASTEXITCODE -ne 0) {
                    throw "GitHub API rejected draft cleanup."
                }
            }
        }
    }
    catch {
        $cleanupFailure = $_.Exception.Message
    }

    if ($null -ne $cleanupFailure) {
        throw "$originalFailure Automatic cleanup of draft release $createdReleaseId also failed: $cleanupFailure"
    }
    throw $originalFailure
}
