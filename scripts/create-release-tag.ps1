param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$CommitSha
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$normalizedCommit = $CommitSha.ToLowerInvariant()
$head = (git -C $repo rev-parse HEAD).Trim().ToLowerInvariant()
if ($head -ne $normalizedCommit) {
    throw "Refusing to tag HEAD '$head'; expected '$normalizedCommit'."
}

git -C $repo show-ref --verify --quiet "refs/tags/$Version"
if ($LASTEXITCODE -eq 0) {
    throw "Local tag '$Version' already exists."
}
if (@(git -C $repo ls-remote --tags origin "refs/tags/$Version" "refs/tags/$Version^{}").Count -ne 0) {
    throw "Remote tag '$Version' already exists."
}

git -C $repo tag --annotate $Version $normalizedCommit --message "Custodian $Version"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create annotated tag '$Version'."
}
if ((git -C $repo cat-file -t $Version).Trim() -ne "tag") {
    throw "Tag '$Version' is not annotated."
}

git -C $repo push origin "refs/tags/$Version"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to push tag '$Version'."
}

$remoteTarget = @(git -C $repo ls-remote --tags origin "refs/tags/$Version^{}")
if ($remoteTarget.Count -ne 1 -or ($remoteTarget[0] -split '\s+')[0].ToLowerInvariant() -ne $normalizedCommit) {
    throw "Remote annotated tag '$Version' does not resolve to '$normalizedCommit'."
}

Write-Host "Created and verified annotated tag $Version at $normalizedCommit."
