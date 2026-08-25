param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$CommitSha
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$normalizedCommit = $CommitSha.ToLowerInvariant()
$tagRef = "refs/tags/$Version"
$taggerName = "github-actions[bot]"
$taggerEmail = "41898282+github-actions[bot]@users.noreply.github.com"
$head = (git -C $repo rev-parse HEAD).Trim().ToLowerInvariant()
if ($head -ne $normalizedCommit) {
    throw "Refusing to tag HEAD '$head'; expected '$normalizedCommit'."
}

function Test-CustodianTaggerLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TaggerLine,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedName,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedEmail
    )

    return $TaggerLine.StartsWith("tagger $ExpectedName <$ExpectedEmail> ", [StringComparison]::Ordinal)
}

function Assert-LocalTagIdentity {
    git -C $repo show-ref --verify --quiet $tagRef
    if ($LASTEXITCODE -ne 0) {
        throw "Local tag '$Version' does not exist."
    }
    if ((git -C $repo cat-file -t $tagRef).Trim() -ne "tag") {
        throw "Local tag '$Version' is not annotated."
    }
    if ((git -C $repo rev-list -n 1 $tagRef).Trim().ToLowerInvariant() -ne $normalizedCommit) {
        throw "Local tag '$Version' does not resolve to '$normalizedCommit'."
    }
    $taggerLine = @(git -C $repo cat-file -p $tagRef | Where-Object { $_ -like "tagger *" } | Select-Object -First 1)
    if ($taggerLine.Count -ne 1 -or
        !(Test-CustodianTaggerLine -TaggerLine $taggerLine[0] -ExpectedName $taggerName -ExpectedEmail $taggerEmail)) {
        throw "Local tag '$Version' does not use the expected release tagger identity."
    }
}

function Get-RemoteTagState {
    $lines = @(git -C $repo ls-remote --tags origin $tagRef "$tagRef^{}" | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect remote tag '$Version'."
    }
    if ($lines.Count -eq 0) {
        return $false
    }

    $directTag = @($lines | Where-Object { ($_ -split '\s+')[1] -ceq $tagRef })
    $peeledTag = @($lines | Where-Object { ($_ -split '\s+')[1] -ceq "$tagRef^{}" })
    if ($directTag.Count -ne 1 -or
        $peeledTag.Count -ne 1 -or
        ($peeledTag[0] -split '\s+')[0].ToLowerInvariant() -ne $normalizedCommit) {
        throw "Remote tag '$Version' is not an annotated tag at '$normalizedCommit'. It will not be moved or replaced."
    }

    $remoteTagObject = ($directTag[0] -split '\s+')[0].ToLowerInvariant()
    git -C $repo fetch --no-tags origin $tagRef
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to fetch remote tag object for '$Version'."
    }
    if ((git -C $repo cat-file -t $remoteTagObject).Trim() -ne "tag") {
        throw "Remote tag '$Version' does not resolve to an annotated tag object."
    }
    $remoteTaggerLine = @(git -C $repo cat-file -p $remoteTagObject | Where-Object { $_ -like "tagger *" } | Select-Object -First 1)
    if ($remoteTaggerLine.Count -ne 1 -or
        !(Test-CustodianTaggerLine -TaggerLine $remoteTaggerLine[0] -ExpectedName $taggerName -ExpectedEmail $taggerEmail)) {
        throw "Remote tag '$Version' does not use the expected release tagger identity. It will not be accepted."
    }
    return $true
}

$remoteExists = Get-RemoteTagState
git -C $repo show-ref --verify --quiet $tagRef
$localExists = $LASTEXITCODE -eq 0

if ($localExists) {
    Assert-LocalTagIdentity
}
elseif ($remoteExists) {
    git -C $repo fetch --no-tags origin "$tagRef`:$tagRef"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to fetch existing remote tag '$Version'."
    }
    Assert-LocalTagIdentity
}
else {
    $previousCommitterName = [Environment]::GetEnvironmentVariable("GIT_COMMITTER_NAME", "Process")
    $previousCommitterEmail = [Environment]::GetEnvironmentVariable("GIT_COMMITTER_EMAIL", "Process")
    try {
        [Environment]::SetEnvironmentVariable("GIT_COMMITTER_NAME", $taggerName, "Process")
        [Environment]::SetEnvironmentVariable("GIT_COMMITTER_EMAIL", $taggerEmail, "Process")
        git -C $repo `
            -c "user.name=$taggerName" `
            -c "user.email=$taggerEmail" `
            tag --annotate $Version $normalizedCommit --message "Custodian $Version"
        $tagExitCode = $LASTEXITCODE
    }
    finally {
        [Environment]::SetEnvironmentVariable("GIT_COMMITTER_NAME", $previousCommitterName, "Process")
        [Environment]::SetEnvironmentVariable("GIT_COMMITTER_EMAIL", $previousCommitterEmail, "Process")
    }
    if ($tagExitCode -ne 0) {
        throw "Failed to create annotated tag '$Version'."
    }
    Assert-LocalTagIdentity
}

if (!$remoteExists) {
    git -C $repo push origin $tagRef
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to push tag '$Version'."
    }
}

if (!(Get-RemoteTagState)) {
    throw "Remote annotated tag '$Version' was not created."
}

Write-Host "Verified annotated tag $Version at $normalizedCommit."
