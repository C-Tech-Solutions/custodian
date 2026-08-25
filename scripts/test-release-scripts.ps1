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

function Get-WorkflowJobLines {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$WorkflowLines,
        [Parameter(Mandatory = $true)]
        [string]$JobName
    )

    $start = [Array]::IndexOf($WorkflowLines, "  $JobName`:")
    if ($start -lt 0) {
        throw "Workflow job '$JobName' was not found."
    }

    $end = $WorkflowLines.Count
    for ($index = $start + 1; $index -lt $WorkflowLines.Count; $index++) {
        if ($WorkflowLines[$index] -match '^  [A-Za-z0-9_-]+:$') {
            $end = $index
            break
        }
    }
    return @($WorkflowLines[$start..($end - 1)])
}

function Get-CheckoutPersistCredentials {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines
    )

    $checkoutIndex = -1
    for ($index = 0; $index -lt $JobLines.Count; $index++) {
        if ($JobLines[$index] -match '^        uses: actions/checkout@') {
            $checkoutIndex = $index
            break
        }
    }
    if ($checkoutIndex -lt 0) {
        throw "Workflow job is missing its checkout step."
    }

    for ($index = $checkoutIndex + 1; $index -lt $JobLines.Count; $index++) {
        if ($JobLines[$index] -match '^      - name:') {
            break
        }
        if ($JobLines[$index] -match '^          persist-credentials:\s*(?<value>true|false)\s*$') {
            return $Matches.value
        }
    }
    throw "Workflow checkout does not declare persist-credentials explicitly."
}

Assert-CustodianPublishPhase -PrepareOnly $false -PackOnly $false -Sign $false -HasSigningOptions $false
Assert-CustodianPublishPhase -PrepareOnly $true -PackOnly $false -Sign $false -HasSigningOptions $false
Assert-CustodianPublishPhase -PrepareOnly $false -PackOnly $true -Sign $false -HasSigningOptions $false
Assert-Throws { Assert-CustodianPublishPhase -PrepareOnly $true -PackOnly $true -Sign $false -HasSigningOptions $false } "cannot be used together"
Assert-Throws { Assert-CustodianPublishPhase -PrepareOnly $true -PackOnly $false -Sign $true -HasSigningOptions $false } "Prepare-only publishing"
Assert-CustodianPublishPhase -PrepareOnly $false -PackOnly $true -Sign $true -HasSigningOptions $true
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
$expectedRelease = @($expectedVelopack) + @("Custodian-1.5.5.spdx.json", "SHA256SUMS.txt")
if ([string]::Join('|', $actualRelease) -ne [string]::Join('|', $expectedRelease)) {
    throw "Unexpected complete release asset contract: $($actualRelease -join ', ')"
}

Assert-CustodianReleaseAbsent -ReleaseExists $false -Version "1.5.5"
Assert-Throws { Assert-CustodianReleaseAbsent -ReleaseExists $true -Version "1.5.5" } "never overwritten"

$emptyDraft = [pscustomobject]@{
    id = 376286670
    tag_name = "1.5.5"
    draft = $true
    immutable = $false
    assets = @()
}
Assert-CustodianEmptyDraftRelease -Release $emptyDraft -DraftId 376286670 -Version "1.5.5"
Assert-Throws { Assert-CustodianEmptyDraftRelease -Release $emptyDraft -DraftId 376286671 -Version "1.5.5" } "does not match"
Assert-Throws { Assert-CustodianEmptyDraftRelease -Release $emptyDraft -DraftId 376286670 -Version "1.5.6" } "does not match"
Assert-Throws {
    Assert-CustodianEmptyDraftRelease -Release ([pscustomobject]@{
        id = 376286670; tag_name = "1.5.5"; draft = $false; immutable = $true; assets = @()
    }) -DraftId 376286670 -Version "1.5.5"
} "not a mutable draft"
Assert-Throws {
    Assert-CustodianEmptyDraftRelease -Release ([pscustomobject]@{
        id = 376286670; tag_name = "1.5.5"; draft = $true; immutable = $false; assets = @([pscustomobject]@{ name = "existing.exe" })
    }) -DraftId 376286670 -Version "1.5.5"
} "not empty"

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
if (!$uploadScript.Contains("Wait-CustodianGitHubReleaseByTag", [StringComparison]::Ordinal)) {
    throw "Draft creation does not tolerate GitHub release-list eventual consistency."
}

foreach ($recoveryScriptName in @("assert-release-draft-recovery.ps1", "resume-empty-release-draft.ps1")) {
    $recoveryScriptPath = Join-Path $PSScriptRoot $recoveryScriptName
    $recoveryTokens = $null
    $recoveryErrors = $null
    $recoveryScript = Get-Content -Raw -LiteralPath $recoveryScriptPath
    [void][Management.Automation.Language.Parser]::ParseInput($recoveryScript, [ref]$recoveryTokens, [ref]$recoveryErrors)
    if ($recoveryErrors.Count -ne 0) {
        throw "Recovery script '$recoveryScriptName' has PowerShell parse errors: $($recoveryErrors.Message -join '; ')"
    }
    if ($recoveryScript -match '(?i)--token') {
        throw "Recovery script '$recoveryScriptName' exposes a command-line token path."
    }
}

$releaseWorkflow = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\.github\workflows\release.yml")
$releaseWorkflowLines = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\.github\workflows\release.yml"))
$ciWorkflow = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\.github\workflows\ci.yml")
$ciWorkflowLines = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\.github\workflows\ci.yml"))
foreach ($lineEnding in @("`n", "`r`n")) {
    $changelog = "# Changelog${lineEnding}${lineEnding}## 1.5.5 - 2026-08-25${lineEnding}"
    if (!(Test-CustodianDatedChangelogEntry -ChangelogText $changelog -Version "1.5.5")) {
        throw "Release preflight changelog validation rejected a valid heading with '$([BitConverter]::ToString([Text.Encoding]::UTF8.GetBytes($lineEnding)))' line endings."
    }
}
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
$releaseStepOrder = @(
    "- name: Pack signed tree and sign generated Velopack PEs",
    "- name: Verify final Velopack assets and signatures"
)
$previousStepIndex = -1
foreach ($step in $releaseStepOrder) {
    $stepIndex = $releaseWorkflow.IndexOf($step, [StringComparison]::Ordinal)
    if ($stepIndex -le $previousStepIndex) {
        throw "Release workflow is missing or misorders required step '$step'."
    }
    $previousStepIndex = $stepIndex
}
foreach ($scriptName in @("prepare-portable-signing-catalog.ps1", "complete-portable-signing.ps1", "sign-azure-artifact.ps1")) {
    $scriptPath = Join-Path $PSScriptRoot $scriptName
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "Release signing script '$scriptName' has PowerShell parse errors."
    }
}
foreach ($generatedSigningContract in @(
    "-PackOnly -Sign",
    "CUSTODIAN_AZURE_SIGNING_ENDPOINT",
    "CUSTODIAN_AZURE_SIGNING_ACCOUNT",
    "CUSTODIAN_AZURE_SIGNING_PROFILE"
)) {
    if (!$releaseWorkflow.Contains($generatedSigningContract, [StringComparison]::Ordinal)) {
        throw "Release workflow is missing generated-PE signing contract '$generatedSigningContract'."
    }
}

$publishVelopackScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "publish-velopack.ps1")
if (!$publishVelopackScript.Contains("-PreserveValidSignature", [StringComparison]::Ordinal)) {
    throw "Velopack's signing template must preserve valid Authenticode signatures."
}
if (!$publishVelopackScript.Contains('"pwsh -NoProfile', [StringComparison]::Ordinal) -or
    $publishVelopackScript.Contains('"powershell -NoProfile', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Velopack's signing template must run under PowerShell 7."
}

$signScriptPath = Join-Path $PSScriptRoot "sign-azure-artifact.ps1"
$signTokens = $null
$signErrors = $null
$signAst = [Management.Automation.Language.Parser]::ParseFile($signScriptPath, [ref]$signTokens, [ref]$signErrors)
$cacheResolverAst = $signAst.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq "Find-NewestArtifactSigningCacheFile"
}, $true)
if ($null -eq $cacheResolverAst) {
    throw "The signing script is missing its version-aware cache resolver."
}
Invoke-Expression $cacheResolverAst.Extent.Text

$cacheFixture = Join-Path ([IO.Path]::GetTempPath()) ("custodian-signing-cache-{0}" -f [Guid]::NewGuid().ToString("N"))
try {
    foreach ($version in @("10.0.9", "10.0.10")) {
        $toolDirectory = Join-Path $cacheFixture "Microsoft.Windows.SDK.BuildTools.$version\bin\x64"
        New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $toolDirectory "signtool.exe") -Force | Out-Null
    }
    $selectedCachedTool = Find-NewestArtifactSigningCacheFile `
        -CacheRoot $cacheFixture `
        -PackageName "Microsoft.Windows.SDK.BuildTools" `
        -Filter "signtool.exe" `
        -RequiredPathPattern '\\x64\\signtool\.exe$'
    if ($null -eq $selectedCachedTool -or $selectedCachedTool.FullName -notmatch 'Microsoft\.Windows\.SDK\.BuildTools\.10\.0\.10\\') {
        throw "The signing cache resolver did not select the newest parsed package version."
    }
}
finally {
    $resolvedCacheFixture = [IO.Path]::GetFullPath($cacheFixture)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolvedCacheFixture.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected signing-cache fixture path."
    }
    if (Test-Path -LiteralPath $resolvedCacheFixture -PathType Container) {
        Remove-Item -LiteralPath $resolvedCacheFixture -Recurse -Force
    }
}

$knownSignedFile = (Get-Command pwsh -ErrorAction Stop).Source
$knownSignature = Get-AuthenticodeSignature -LiteralPath $knownSignedFile
if ($knownSignature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
    throw "The release contract test requires a validly signed PowerShell host."
}
$knownHashBefore = (Get-FileHash -LiteralPath $knownSignedFile -Algorithm SHA256).Hash
& $signScriptPath -Path $knownSignedFile -PreserveValidSignature
$knownHashAfter = (Get-FileHash -LiteralPath $knownSignedFile -Algorithm SHA256).Hash
if ($knownHashAfter -cne $knownHashBefore) {
    throw "Preserving a valid Authenticode signature changed the signed file."
}

$tamperedSignedFile = [IO.Path]::GetTempFileName()
try {
    Copy-Item -LiteralPath $knownSignedFile -Destination $tamperedSignedFile -Force
    $stream = [IO.File]::Open($tamperedSignedFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $stream.Position = [Math]::Min(1024, $stream.Length - 1)
        $originalByte = $stream.ReadByte()
        $stream.Position--
        $stream.WriteByte($originalByte -bxor 1)
    }
    finally {
        $stream.Dispose()
    }
    Assert-Throws { & $signScriptPath -Path $tamperedSignedFile -PreserveValidSignature } "Refusing to replace the invalid Authenticode signature"
}
finally {
    Remove-Item -LiteralPath $tamperedSignedFile -Force
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
foreach ($recoveryContract in @(
    "recovery_source_sha",
    "recovery_draft_id",
    "validate-recovery:",
    "recover-draft:",
    "publish-recovered:",
    "assert-release-draft-recovery.ps1",
    "resume-empty-release-draft.ps1",
    "-AttestationSourceDigest `$env:WORKFLOW_SHA"
)) {
    if (!$releaseWorkflow.Contains($recoveryContract, [StringComparison]::Ordinal)) {
        throw "Release workflow is missing protected empty-draft recovery contract '$recoveryContract'."
    }
}

$publishGitHubScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "publish-github-release.ps1")
if ($publishGitHubScript -notmatch '\$release\.draft' -or
    $publishGitHubScript -notmatch '\$release\.immutable' -or
    $publishGitHubScript -match '(?i)--token') {
    throw "GitHub publication does not safely distinguish draft publication from immutable verification resume."
}

$createTagScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "create-release-tag.ps1")
foreach ($taggerIdentityContract in @(
    '$taggerName = "github-actions[bot]"',
    '$taggerEmail = "41898282+github-actions[bot]@users.noreply.github.com"',
    '[Environment]::SetEnvironmentVariable("GIT_COMMITTER_NAME", $taggerName, "Process")',
    '[Environment]::SetEnvironmentVariable("GIT_COMMITTER_EMAIL", $taggerEmail, "Process")',
    '-c "user.name=$taggerName"',
    '-c "user.email=$taggerEmail"'
)) {
    if (!$createTagScript.Contains($taggerIdentityContract, [StringComparison]::Ordinal)) {
        throw "Annotated tag creation is missing deterministic tagger identity contract '$taggerIdentityContract'."
    }
}
foreach ($remoteTagContract in @('$remoteTagObject', 'cat-file -p $remoteTagObject', 'Remote tag ''$Version'' does not use the expected release tagger identity')) {
    if (!$createTagScript.Contains($remoteTagContract, [StringComparison]::Ordinal)) {
        throw "Annotated tag verification is missing remote tag identity contract '$remoteTagContract'."
    }
}

$tagTokens = $null
$tagErrors = $null
$tagAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot "create-release-tag.ps1"),
    [ref]$tagTokens,
    [ref]$tagErrors)
$taggerLineFunction = $tagAst.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq "Test-CustodianTaggerLine"
}, $true)
if ($null -eq $taggerLineFunction) {
    throw "Annotated tag verification is missing its shared tagger identity check."
}
Invoke-Expression $taggerLineFunction.Extent.Text
$exactTaggerLine = "tagger github-actions[bot] <41898282+github-actions[bot]@users.noreply.github.com> 1787654321 +0000"
if (!(Test-CustodianTaggerLine `
    -TaggerLine $exactTaggerLine `
    -ExpectedName "github-actions[bot]" `
    -ExpectedEmail "41898282+github-actions[bot]@users.noreply.github.com")) {
    throw "Exact release tagger identity was rejected."
}
if (Test-CustodianTaggerLine `
    -TaggerLine $exactTaggerLine `
    -ExpectedName "github-actions[bot]" `
    -ExpectedEmail "unexpected@example.com") {
    throw "A mismatched release tagger identity was accepted."
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
foreach ($portablePublisherContract in @("portableRootExecutables", "O=C-Tech Solutions LLC")) {
    if (!$verifyAssetsScript.Contains($portablePublisherContract, [StringComparison]::Ordinal)) {
        throw "Release asset verification is missing portable publisher contract '$portablePublisherContract'."
    }
}

$checkoutContracts = @(
    @{ Lines = $ciWorkflowLines; Job = "build-test"; Expected = "false" },
    @{ Lines = $ciWorkflowLines; Job = "vulnerability-scan"; Expected = "false" },
    @{ Lines = $releaseWorkflowLines; Job = "validate"; Expected = "false" },
    @{ Lines = $releaseWorkflowLines; Job = "sign-and-draft"; Expected = "true" },
    @{ Lines = $releaseWorkflowLines; Job = "publish"; Expected = "false" },
    @{ Lines = $releaseWorkflowLines; Job = "validate-recovery"; Expected = "false" },
    @{ Lines = $releaseWorkflowLines; Job = "recover-draft"; Expected = "false" },
    @{ Lines = $releaseWorkflowLines; Job = "publish-recovered"; Expected = "false" }
)
foreach ($contract in $checkoutContracts) {
    $jobLines = @(Get-WorkflowJobLines -WorkflowLines $contract.Lines -JobName $contract.Job)
    $actualPersistence = Get-CheckoutPersistCredentials -JobLines $jobLines
    if ($actualPersistence -cne $contract.Expected) {
        throw "Workflow job '$($contract.Job)' must set checkout persist-credentials to '$($contract.Expected)'."
    }
}
$validateJobLines = @(Get-WorkflowJobLines -WorkflowLines $releaseWorkflowLines -JobName "validate")
$permissionsStart = [Array]::IndexOf($validateJobLines, "    permissions:")
if ($permissionsStart -lt 0) {
    throw "The pre-environment validation job must not have release-write permission."
}
$permissionEntries = [Collections.Generic.List[string]]::new()
for ($index = $permissionsStart + 1; $index -lt $validateJobLines.Count; $index++) {
    $line = $validateJobLines[$index]
    if ($line -match '^    \S') {
        break
    }
    if ($line -match '^      (?<entry>[^#].*\S)\s*$') {
        $permissionEntries.Add($Matches.entry.Trim())
    }
}
if ($permissionEntries.Count -ne 1 -or $permissionEntries[0] -cne "contents: read") {
    throw "The pre-environment validation job must not have release-write permission."
}

foreach ($auditContract in @("--no-restore", "NU1900", "NU1905")) {
    if (!$releaseWorkflow.Contains($auditContract, [StringComparison]::Ordinal) -or
        !$ciWorkflow.Contains($auditContract, [StringComparison]::Ordinal)) {
        throw "CI or release vulnerability audit is missing fail-closed contract '$auditContract'."
    }
}

Write-Host "Release script contract tests passed."
