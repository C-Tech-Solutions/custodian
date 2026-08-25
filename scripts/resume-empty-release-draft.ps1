param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCommit,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 9223372036854775807)]
    [Int64]$DraftId,
    [Parameter(Mandatory = $true)]
    [string]$SourceRepositoryRoot,
    [string]$OutputRoot = "artifacts\velopack",
    [string]$Repository = "C-Tech-Solutions/custodian"
)

$ErrorActionPreference = "Stop"
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force

if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw "GH_TOKEN must be available to GitHub CLI through the environment."
}

$sourceRepo = (Resolve-Path -LiteralPath $SourceRepositoryRoot).Path
$output = (Resolve-Path -LiteralPath (Join-Path $sourceRepo $OutputRoot)).Path
$normalizedCommit = $ExpectedCommit.ToLowerInvariant()
Assert-CustodianRemoteReleaseTagIdentity `
    -RepositoryRoot $sourceRepo `
    -Version $Version `
    -ExpectedCommit $normalizedCommit

$release = gh api "repos/$Repository/releases/$DraftId" | ConvertFrom-Json -Depth 50
if ($LASTEXITCODE -ne 0 -or $null -eq $release) {
    throw "Unable to retrieve draft release '$DraftId'."
}
$notes = Get-Content -Raw -LiteralPath (Join-Path $sourceRepo "docs\releases\$Version.md")
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

$assetPaths = foreach ($assetName in Get-CustodianReleaseAssetNames -Version $Version) {
    $assetPath = Join-Path $output $assetName
    if (!(Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Required release asset was not found: '$assetPath'."
    }
    $assetPath
}

$uploadedAssetIds = [Collections.Generic.List[Int64]]::new()
try {
    foreach ($assetPath in $assetPaths) {
        $assetName = [IO.Path]::GetFileName($assetPath)
        $escapedName = [Uri]::EscapeDataString($assetName)
        $uploadUrl = "https://uploads.github.com/repos/$Repository/releases/$DraftId/assets?name=$escapedName"
        $uploadedJson = (gh api `
            --method POST `
            -H "Content-Type: application/octet-stream" `
            --input $assetPath `
            $uploadUrl | Out-String)
        $uploadExitCode = $LASTEXITCODE
        if ($uploadExitCode -ne 0) {
            throw "Upload outcome for '$assetName' was not confirmed. No unknown asset will be deleted automatically."
        }
        $uploaded = ConvertFrom-Json -InputObject $uploadedJson -Depth 20
        if ($null -eq $uploaded -or [Int64]$uploaded.id -le 0 -or $uploaded.name -cne $assetName) {
            throw "Upload response for '$assetName' did not prove ownership of the created asset."
        }
        $uploadedAssetIds.Add([Int64]$uploaded.id)
    }

    $completed = gh api "repos/$Repository/releases/$DraftId" | ConvertFrom-Json -Depth 50
    if ($LASTEXITCODE -ne 0 -or !$completed.draft -or $completed.immutable -or [Int64]$completed.id -ne $DraftId) {
        throw "GitHub draft '$DraftId' changed state unexpectedly during recovery upload."
    }

    $expectedNames = @($assetPaths | ForEach-Object { [IO.Path]::GetFileName($_) })
    $remoteNames = @($completed.assets.name)
    if (@($expectedNames | Where-Object { $_ -notin $remoteNames }).Count -ne 0 -or
        @($remoteNames | Where-Object { $_ -notin $expectedNames }).Count -ne 0) {
        throw "GitHub draft '$DraftId' does not contain the exact release asset set."
    }

    foreach ($asset in $completed.assets) {
        $assetPath = Join-Path $output $asset.name
        $expectedDigest = "sha256:$((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant())"
        if ($asset.digest -ne $expectedDigest) {
            throw "GitHub digest mismatch after recovery upload for '$($asset.name)'."
        }
    }

    Write-Host "Recovered draft release: $($completed.html_url)"
}
catch {
    $originalFailure = $_.Exception.Message
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    foreach ($assetId in $uploadedAssetIds) {
        gh api --method DELETE "repos/$Repository/releases/assets/$assetId"
        if ($LASTEXITCODE -ne 0) {
            $cleanupFailures.Add($assetId.ToString())
        }
    }
    if ($cleanupFailures.Count -ne 0) {
        throw "$originalFailure Cleanup also failed for newly uploaded asset IDs: $($cleanupFailures -join ', ')."
    }
    throw $originalFailure
}
