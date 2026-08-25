$ErrorActionPreference = "Stop"
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected error containing '$ExpectedMessage', got '$($_.Exception.Message)'."
        }
        return
    }

    throw "Expected an error containing '$ExpectedMessage'."
}

Assert-CustodianPublishPhase -PrepareOnly $false -PackOnly $false -Sign $false -HasSigningOptions $false
Assert-CustodianPublishPhase -PrepareOnly $true -PackOnly $false -Sign $false -HasSigningOptions $false
Assert-CustodianPublishPhase -PrepareOnly $false -PackOnly $true -Sign $false -HasSigningOptions $false
Assert-Throws { Assert-CustodianPublishPhase -PrepareOnly $true -PackOnly $true -Sign $false -HasSigningOptions $false } "cannot be used together"
Assert-Throws { Assert-CustodianPublishPhase -PrepareOnly $true -PackOnly $false -Sign $true -HasSigningOptions $false } "Phase-only publishing"
Assert-Throws { Assert-CustodianPublishPhase -PrepareOnly $false -PackOnly $true -Sign $false -HasSigningOptions $true } "Phase-only publishing"
Assert-Throws { Assert-CustodianPublishPhase -PrepareOnly $false -PackOnly $false -Sign $false -HasSigningOptions $true } "without -Sign"

$expectedVelopack = @(
    "Custodian.DiskAnalyzer-1.5.5-full.nupkg",
    "Custodian.DiskAnalyzer-win-Portable.zip",
    "Custodian.DiskAnalyzer-win-Setup.exe",
    "RELEASES",
    "releases.win.json"
)
$actualVelopack = @(Get-CustodianVelopackAssetNames -Version "1.5.5")
if ([string]::Join('|', $actualVelopack) -ne [string]::Join('|', $expectedVelopack)) {
    throw "Unexpected Velopack asset contract: $($actualVelopack -join ', ')"
}

$actualRelease = @(Get-CustodianReleaseAssetNames -Version "1.5.5")
if ($actualRelease.Count -ne 7 -or "Custodian-1.5.5.spdx.json" -notin $actualRelease -or "SHA256SUMS.txt" -notin $actualRelease) {
    throw "Unexpected complete release asset contract: $($actualRelease -join ', ')"
}

Assert-CustodianReleaseAbsent -ReleaseExists $false -Version "1.5.5"
Assert-Throws { Assert-CustodianReleaseAbsent -ReleaseExists $true -Version "1.5.5" } "never overwritten"

$secretMarker = "must-not-appear-in-process-arguments"
$arguments = New-CustodianDraftReleaseArguments `
    -Repository "C-Tech-Solutions/custodian" `
    -Version "1.5.5" `
    -NotesPath "notes.md"
$argumentText = $arguments -join ' '
if ($argumentText -match '(?i)(--token|github_token|gh_token)' -or $argumentText.Contains($secretMarker)) {
    throw "Draft release arguments contain credential material."
}
if ($argumentText -match '(?:one\.nupkg|two\.exe)' -or "--draft" -notin $arguments) {
    throw "Draft creation must create an empty draft before separate asset uploads."
}

$uploadScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "upload-velopack-github.ps1")
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($uploadScript, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) {
    throw "Upload script has PowerShell parse errors."
}
$parameterNames = @($ast.ParamBlock.Parameters.Name.VariablePath.UserPath)
if ($parameterNames -contains "Token" -or $uploadScript -match '(?i)--token') {
    throw "Upload script exposes credentials through parameters or command arguments."
}

$releaseWorkflow = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\.github\workflows\release.yml")
$requiredActionPins = @(
    "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
    "actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68",
    "azure/login@f5d393ae46f8fde4be8b75f32e3fc50e654ad0ca",
    "Azure/artifact-signing-action@c7ab2a863ab5f9a846ddb8265964877ef296ee82",
    "actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6"
)
foreach ($pin in $requiredActionPins) {
    if (!$releaseWorkflow.Contains($pin, [StringComparison]::Ordinal)) {
        throw "Release workflow is missing required action pin '$pin'."
    }
}
if ($releaseWorkflow -match '(?i)(client-secret|azure-client-secret|--token)') {
    throw "Release workflow contains a stored-secret or command-line token path."
}
if ($releaseWorkflow -notmatch 'IMMUTABLE_RELEASES_ACCEPTED_FOR' -or
    $releaseWorkflow -notmatch '\$env:RELEASE_VERSION`:\$env:RELEASE_SHA') {
    throw "Release workflow does not bind immutable-release acceptance to the exact version and commit."
}
if ($releaseWorkflow -match 'if \("\$\{\{ vars\.IMMUTABLE_RELEASES_ACCEPTED_FOR \}\}"' -or
    $releaseWorkflow -notmatch 'IMMUTABLE_RELEASES_ACCEPTED_FOR:\s*\$\{\{ vars\.IMMUTABLE_RELEASES_ACCEPTED_FOR \}\}') {
    throw "Immutable-release acceptance must enter PowerShell through the environment, not template-expanded script source."
}
foreach ($resumeContract in @("resume_published", "RequireDraftOrPublishedImmutable", "publish-github-release.ps1")) {
    if (!$releaseWorkflow.Contains($resumeContract, [StringComparison]::Ordinal)) {
        throw "Release workflow is missing resumable publication contract '$resumeContract'."
    }
}

$publishGitHubScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "publish-github-release.ps1")
if ($publishGitHubScript -notmatch '\$release\.draft' -or
    $publishGitHubScript -notmatch '\$release\.immutable' -or
    $publishGitHubScript -match '(?i)--token') {
    throw "GitHub publication does not safely distinguish draft publication from immutable verification resume."
}

$verifyGitHubScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "verify-github-release.ps1")
foreach ($requiredAttestationArgument in @("--signer-workflow", "--source-digest", "--source-ref")) {
    if (!$verifyGitHubScript.Contains($requiredAttestationArgument, [StringComparison]::Ordinal)) {
        throw "GitHub release verification is missing '$requiredAttestationArgument'."
    }
}

$verifyAssetsScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "verify-release-assets.ps1")
foreach ($requiredIndexField in @("PackageId", "FileName", "SHA1", "SHA256", "Size")) {
    if (!$verifyAssetsScript.Contains($requiredIndexField, [StringComparison]::Ordinal)) {
        throw "Release index verification is missing '$requiredIndexField'."
    }
}

$ciWorkflow = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\.github\workflows\ci.yml")
if ([regex]::Matches($ciWorkflow, 'persist-credentials:\s*false').Count -ne 2 -or
    [regex]::Matches($releaseWorkflow, 'persist-credentials:\s*false').Count -ne 2) {
    throw "Non-pushing checkout steps must disable persisted GitHub credentials."
}
if ($releaseWorkflow -notmatch '(?s)\n  validate:.*?permissions:\s*\n\s*contents:\s*read.*?\n  sign-and-draft:') {
    throw "The pre-environment validation job must not have release-write permission."
}

Write-Host "Release script contract tests passed."
