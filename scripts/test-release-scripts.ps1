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

function Get-WorkflowRootEnvironmentLines {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$WorkflowLines
    )

    $rootEnvironmentIndexes = [Collections.Generic.List[int]]::new()
    for ($index = 0; $index -lt $WorkflowLines.Count; $index++) {
        if ($WorkflowLines[$index] -match '^[''"]?env[''"]?\s*:') {
            $rootEnvironmentIndexes.Add($index)
        }
    }
    if ($rootEnvironmentIndexes.Count -gt 1) {
        throw "Workflow must not contain duplicate root environment blocks."
    }
    if ($rootEnvironmentIndexes.Count -eq 0) {
        return @()
    }

    $environmentLines = [Collections.Generic.List[string]]::new()
    for ($index = $rootEnvironmentIndexes[0]; $index -lt $WorkflowLines.Count; $index++) {
        if ($index -gt $rootEnvironmentIndexes[0] -and
            $WorkflowLines[$index] -match '^\S' -and
            $WorkflowLines[$index] -notmatch '^\s*(?:#|$)') {
            break
        }
        $environmentLines.Add($WorkflowLines[$index])
    }
    return @($environmentLines)
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

function Get-WorkflowStepLines {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines,
        [Parameter(Mandatory = $true)]
        [string]$StepName
    )

    $matchingIndexes = [Collections.Generic.List[int]]::new()
    for ($index = 0; $index -lt $JobLines.Count; $index++) {
        if ($JobLines[$index] -ceq "      - name: $StepName") {
            $matchingIndexes.Add($index)
        }
    }
    if ($matchingIndexes.Count -eq 0) {
        throw "Workflow step '$StepName' was not found."
    }
    if ($matchingIndexes.Count -ne 1) {
        throw "Workflow step '$StepName' must occur exactly once."
    }
    $start = $matchingIndexes[0]

    $end = $JobLines.Count
    for ($index = $start + 1; $index -lt $JobLines.Count; $index++) {
        if ($JobLines[$index] -match '^      -(?:\s|$)') {
            $end = $index
            break
        }
    }
    return @($JobLines[$start..($end - 1)])
}

function Assert-ExactWorkflowLines {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        [string[]]$ActualLines,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        [string[]]$ExpectedLines,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    $normalizedActual = [Collections.Generic.List[string]]::new()
    foreach ($line in $ActualLines) {
        $normalizedActual.Add($line)
    }
    while ($normalizedActual.Count -gt 0 -and
        $normalizedActual[$normalizedActual.Count - 1] -ceq "") {
        $normalizedActual.RemoveAt($normalizedActual.Count - 1)
    }
    if ([string]::Join("`n", $normalizedActual) -cne
        [string]::Join("`n", $ExpectedLines)) {
        throw $FailureMessage
    }
}

Assert-Throws {
    Get-WorkflowStepLines -JobLines @(
        "      - name: Checkout exact release source",
        "        uses: actions/checkout@first",
        "      - name: Checkout exact release source",
        "        uses: actions/checkout@second"
    ) -StepName "Checkout exact release source"
} "must occur exactly once"
Assert-Throws {
    Assert-ExactWorkflowLines `
        -ActualLines @("trusted", "malicious") `
        -ExpectedLines @("trusted") `
        -FailureMessage "Workflow lines changed unexpectedly."
} "changed unexpectedly"

function Get-WorkflowPermissionEntries {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines
    )

    $permissionBlockIndexes = [Collections.Generic.List[int]]::new()
    for ($index = 0; $index -lt $JobLines.Count; $index++) {
        if ($JobLines[$index] -ceq "    permissions:") {
            $permissionBlockIndexes.Add($index)
        }
    }
    if ($permissionBlockIndexes.Count -ne 1) {
        throw "Workflow job must contain exactly one permissions block."
    }

    $permissionEntries = [Collections.Generic.List[string]]::new()
    for ($index = $permissionBlockIndexes[0] + 1; $index -lt $JobLines.Count; $index++) {
        $line = $JobLines[$index]
        if ($line -match '^\s*#') {
            continue
        }
        if ($line -match '^    \S') {
            break
        }
        if ($line -match '^      (?<entry>[^#].*\S)\s*$') {
            $permissionEntries.Add($Matches.entry.Trim())
        }
    }
    return @($permissionEntries)
}

$commentedPermissionEntries = @(Get-WorkflowPermissionEntries -JobLines @(
    "  fixture-job:",
    "    permissions:",
    "      contents: read",
    "    # keep scanning the permissions mapping",
    "      id-token: write",
    "    steps:"
))
if ($commentedPermissionEntries.Count -ne 2 -or
    $commentedPermissionEntries[0] -cne "contents: read" -or
    $commentedPermissionEntries[1] -cne "id-token: write") {
    throw "Workflow permission parsing must retain entries after comments."
}

function Get-GitHubCliTokenAssignments {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$WorkflowLines
    )

    return @($WorkflowLines | Where-Object {
        $_ -match '(?<![A-Za-z0-9_])(?:[''"]?(?:GH_TOKEN|GITHUB_TOKEN)[''"]?\s*:|\$env:(?:GH_TOKEN|GITHUB_TOKEN)\s*=|(?:GH_TOKEN|GITHUB_TOKEN)\s*=)'
    })
}

function Get-ExplicitGitHubTokenReferences {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$WorkflowLines
    )

    return @($WorkflowLines | Where-Object {
        $_ -match '\$\{\{.*?(?<![A-Za-z0-9_])(?:github\s*(?:\.\s*token|\[)|secrets\s*(?:\.\s*GITHUB_TOKEN|\[)).*?\}\}'
    })
}

function Assert-NoGitHubOrSecretsContextReferences {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$WorkflowLines
    )

    $workflowText = [string]::Join("`n", $WorkflowLines)
    if ($workflowText -match '(?s)\$\{\{.*?(?<![A-Za-z0-9_])(?:github|secrets)(?![A-Za-z0-9_]).*?\}\}') {
        throw "Historical release-source validation must not reference the GitHub or secrets contexts."
    }
    if (@($WorkflowLines | Where-Object {
        $_ -match '\\(?:x[0-9A-Fa-f]{2}|u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})'
    }).Count -ne 0) {
        throw "Historical release-source validation must not contain YAML Unicode escapes."
    }
    if ($workflowText -match "\\`n") {
        throw "Historical release-source validation must not contain YAML escaped line continuations."
    }
}

function Assert-RecoveryTokenScope {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$EffectiveJobLines,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$DraftInspectionLines
    )

    $tokenAssignments = @(Get-GitHubCliTokenAssignments -WorkflowLines $EffectiveJobLines)
    $tokenReferences = @(Get-ExplicitGitHubTokenReferences -WorkflowLines $EffectiveJobLines)
    if ($tokenAssignments.Count -ne 1 -or
        $tokenReferences.Count -ne 1 -or
        !($DraftInspectionLines -contains '          GH_TOKEN: ${{ github.token }}')) {
        throw "Recovery validation must expose the write-capable token only to the draft-inspection step."
    }

    $actualInspectionLines = [Collections.Generic.List[string]]::new()
    foreach ($line in $DraftInspectionLines) {
        $actualInspectionLines.Add($line)
    }
    while ($actualInspectionLines.Count -gt 0 -and
        $actualInspectionLines[$actualInspectionLines.Count - 1] -ceq "") {
        $actualInspectionLines.RemoveAt($actualInspectionLines.Count - 1)
    }
    $expectedInspectionLines = @(
        "      - name: Verify exact empty draft and annotated source tag",
        "        shell: pwsh",
        "        env:",
        '          GH_TOKEN: ${{ github.token }}',
        '        run: ./scripts/assert-release-draft-recovery.ps1 -Version $env:RELEASE_VERSION -WorkflowCommitSha $env:WORKFLOW_SHA -SourceCommitSha $env:RELEASE_SHA -DraftId $env:RECOVERY_DRAFT_ID'
    )
    if ([string]::Join("`n", $actualInspectionLines) -cne
        [string]::Join("`n", $expectedInspectionLines)) {
        throw "Recovery validation must use the exact credential-bearing draft-inspection command."
    }
}

function Get-CheckoutActionLines {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines
    )

    return @($JobLines | Where-Object {
        $_ -match '^(?:        uses:|      - uses:)\s+[''"]?actions/checkout@'
    })
}

function Get-ActionInvocationLines {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines
    )

    return @($JobLines | Where-Object {
        $_ -match '^(?:        uses:|      - uses:)\s+'
    })
}

function Assert-ExactActionAllowlist {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$ExpectedActionLines
    )

    $actual = @(Get-ActionInvocationLines -JobLines $JobLines)
    $actualKey = [string]::Join("`n", @($actual | Sort-Object -CaseSensitive))
    $expectedKey = [string]::Join("`n", @($ExpectedActionLines | Sort-Object -CaseSensitive))
    if ($actualKey -cne $expectedKey) {
        throw "Workflow job action invocations do not match the exact pinned allowlist."
    }
}

function Assert-SupportedRecoveryWorkflowSyntax {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        [string[]]$WorkflowLines,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        [string[]]$RootEnvironmentLines,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        [string[]]$RecoveryJobLines
    )

    $allWorkflowLines = @($WorkflowLines + $RootEnvironmentLines + $RecoveryJobLines)
    if (@($WorkflowLines | Where-Object {
        $_ -match '^defaults\s*:'
    }).Count -ne 0) {
        throw "Release workflow must not define root run defaults."
    }
    if (@($allWorkflowLines | Where-Object {
        $_ -match '\\(?:x[0-9A-Fa-f]{2}|u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})'
    }).Count -ne 0 -or
        [string]::Join("`n", $allWorkflowLines) -match "\\`n") {
        throw "Release workflow must not use YAML decoded escapes."
    }
    if (@($allWorkflowLines | Where-Object {
        $_ -notmatch '^\s*#' -and (
            $_ -match '^\s*\?(?:\s|$)' -or
            $_ -match '^\s*-\s+\?(?:\s|$)' -or
            $_ -match '^\s*(?:-\s*)?!{1,2}(?:<[^>]+>|[^\s]+)(?:\s|$)' -or
            $_ -match '^\s*(?:-\s+)?[^\r\n]+:\s*!{1,2}(?:<[^>]+>|[^\s]+)(?:\s|$)'
        )
    }).Count -ne 0) {
        throw "Release workflow must not use explicit or tagged YAML mapping keys."
    }
    if (@($WorkflowLines | Where-Object {
        $_ -match '^ {1,5}-(?:\s|$)' -or
        $_ -match '^ {7,}-(?:\s|$)'
    }).Count -ne 0) {
        throw "Release workflow sequence entries must use canonical six-space indentation."
    }
    $allowedFlowSequenceLines = @(
        '    needs: [validate, sign-and-draft]',
        '    needs: [validate-recovery-source, recover-draft]'
    )
    if (@($allWorkflowLines | Where-Object {
        $_ -notmatch '^\s*#' -and
        $_ -notin $allowedFlowSequenceLines -and (
            $_ -match '^      -\s*(?:#.*)?$' -or
            $_ -match '^      -\s*[\[{]' -or
            $_ -match '^\s*[\[{]' -or
            $_ -match '^\s*(?:-\s+)?[^\r\n]+:\s*[\[{]'
        )
    }).Count -ne 0) {
        throw "Release workflow must use canonical block-style steps."
    }
    if (@($allWorkflowLines | Where-Object {
        $_ -notmatch '^\s*#' -and (
            $_ -match '^\s*(?:-\s+)?(?:"[^"]*"|''[^'']*'')\s*:' -or
            $_ -match '^\s*(?:-\s+)?[^\r\n]+:\s*"(?:[^"\\]|\\.)*$' -or
            $_ -match '^\s*(?:-\s+)?[^\r\n]+:\s*''(?:[^'']|'''')*$' -or
            $_ -match '^\s*(?:-\s+)?"(?:[^"\\]|\\.)*$' -or
            $_ -match '^\s*(?:-\s+)?''(?:[^'']|'''')*$'
        )
    }).Count -ne 0) {
        throw "Release workflow must not use quoted YAML mapping keys or values."
    }
    if (@($WorkflowLines | Where-Object {
        ($_ -match ':\s*[>|][0-9+-]*\s*(?:#.*)?$' -and
            $_ -cne "        run: |") -or
        $_ -match '^\s*[>|][0-9+-]*\s*(?:#.*)?$'
    }).Count -ne 0) {
        throw "Release workflow block scalars are allowed only for canonical run steps."
    }

    $structuralUsesLines = @($WorkflowLines | Where-Object {
        $_ -match '^(?:        |      -\s+)[''"]?uses[''"]?\s*:'
    })
    if (@($structuralUsesLines | Where-Object {
        $_ -notmatch '^(?:        uses:|      - uses:)\s+'
    }).Count -ne 0) {
        throw "Release workflow must use canonical bare uses keys without whitespace before the colon."
    }

    if (@($allWorkflowLines | Where-Object {
        $_ -notmatch '^\s*#' -and
        ($_ -match '(?::\s*|^\s*-\s*)[&*][^\s\[\]{},]+' -or
            $_ -match '^\s*[&*][^\s\[\]{},]+')
    }).Count -ne 0) {
        throw "Release workflow must not use YAML anchors or aliases."
    }

    $effectiveRecoveryLines = @($RootEnvironmentLines + $RecoveryJobLines)
    if (@($effectiveRecoveryLines | Where-Object {
        $_ -match ':\s*[>|][0-9+-]*\s*(?:#.*)?$'
    }).Count -ne 0) {
        throw "Write-capable recovery scope must not use YAML block or folded scalars."
    }
    $allowedContextLines = @(
        '          ref: ${{ github.sha }}',
        '          GH_TOKEN: ${{ github.token }}'
    )
    $contextScanLines = @($effectiveRecoveryLines | Where-Object { $_ -notin $allowedContextLines })
    if ([string]::Join("`n", $contextScanLines) -match '(?s)\$\{\{.*?(?<![A-Za-z0-9_])(?:github|secrets)(?![A-Za-z0-9_]).*?\}\}') {
        throw "Write-capable recovery scope contains a GitHub or secrets context outside the exact allowlist."
    }
    if ($RootEnvironmentLines.Count -ne 0) {
        throw "Release workflow must not define a root environment."
    }
}

function Assert-ExpectedCheckoutCount {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines,
        [Parameter(Mandatory = $true)]
        [int]$ExpectedCount
    )

    $checkoutActions = @(Get-CheckoutActionLines -JobLines $JobLines)
    if ($checkoutActions.Count -ne $ExpectedCount) {
        throw "Workflow job must contain exactly $ExpectedCount checkout action(s)."
    }
}

function Assert-NoCheckoutInputKeys {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$StepLines,
        [Parameter(Mandatory = $true)]
        [string[]]$ForbiddenKeys
    )

    $keyPattern = [string]::Join('|', @($ForbiddenKeys | ForEach-Object { [Regex]::Escape($_) }))
    $mappingKeyPattern = '^\s+[''"]?(?:{0})[''"]?\s*:' -f $keyPattern
    if (@($StepLines | Where-Object { $_ -match $mappingKeyPattern }).Count -ne 0) {
        throw "Workflow checkout contains a forbidden input key."
    }
}

function Assert-ExactWorkflowStepLines {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$StepLines,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$ExpectedLines
    )

    $actual = @($StepLines | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
    $actualKey = [string]::Join("`n", $actual)
    $expectedKey = [string]::Join("`n", $ExpectedLines)
    if ($actualKey -cne $expectedKey) {
        throw "Workflow step does not match the exact canonical line allowlist."
    }
}

function Assert-SingleTrustedWorkflowCheckout {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$TrustedStepLines
    )

    Assert-ExpectedCheckoutCount -JobLines $JobLines -ExpectedCount 1
    $actionInvocations = @(Get-ActionInvocationLines -JobLines $JobLines)
    if ($actionInvocations.Count -ne 1 -or
        !($TrustedStepLines -contains "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1")) {
        throw "Recovery validation must contain only the pinned trusted checkout action."
    }
}

foreach ($tokenAssignmentFixture in @(
    '    env: { GH_TOKEN: token }',
    '        env: { GITHUB_TOKEN: token }',
    '        run: $env:GH_TOKEN = "${{ github.token }}"',
    '        run: GITHUB_TOKEN=${{ secrets.GITHUB_TOKEN }}'
)) {
    if (@(Get-GitHubCliTokenAssignments -WorkflowLines @($tokenAssignmentFixture)).Count -ne 1) {
        throw "GitHub CLI token assignment fixture was not detected: $tokenAssignmentFixture"
    }
    if ($tokenAssignmentFixture -match '\$\{\{' -and
        @(Get-ExplicitGitHubTokenReferences -WorkflowLines @($tokenAssignmentFixture)).Count -ne 1) {
        throw "Explicit GitHub token reference fixture was not detected: $tokenAssignmentFixture"
    }
}
foreach ($compoundTokenReferenceFixture in @(
    '        run: echo "${{ github.token || inputs.fallback }}"',
    '        run: echo "${{ format(''{0}'', secrets.GITHUB_TOKEN) }}"',
    '        run: echo "${{ github[''token''] }}"',
    '        run: echo "${{ secrets[''GITHUB_TOKEN''] }}"',
    '        run: echo "${{ github[format(''to{0}'', ''ken'')] }}"'
)) {
    if (@(Get-ExplicitGitHubTokenReferences -WorkflowLines @($compoundTokenReferenceFixture)).Count -ne 1) {
        throw "Compound GitHub token reference fixture was not detected: $compoundTokenReferenceFixture"
    }
}
foreach ($serializedContextFixture in @(
    '        run: echo "${{ toJSON(github) }}"',
    '        run: echo "${{ toJSON(secrets) }}"'
)) {
    Assert-Throws {
        Assert-NoGitHubOrSecretsContextReferences -WorkflowLines @($serializedContextFixture)
    } "must not reference the GitHub or secrets contexts"
}
foreach ($escapedContextFixture in @(
    '        env: { LEAK: "\u0024{{ toJSON(secrets) }}" }',
    '        env: { LEAK: "${{ toJSON(gith\u0075b) }}" }'
)) {
    Assert-Throws {
        Assert-NoGitHubOrSecretsContextReferences -WorkflowLines @($escapedContextFixture)
    } "must not contain YAML Unicode escapes"
}
Assert-Throws {
    Assert-NoGitHubOrSecretsContextReferences -WorkflowLines @(
        '        env: { LEAK: "${{ toJSON(secr\',
        '          ets) }}" }'
    )
} "must not contain YAML escaped line continuations"

$inheritedTokenWorkflowFixture = @(
    "name: Fixture",
    "env:",
    "# inherited environment comment",
    '  GH_TOKEN: ${{ github.token }}',
    "jobs:",
    "  validate-recovery:",
    "    steps:",
    "      - name: Verify exact empty draft and annotated source tag",
    "        env:",
    '          GH_TOKEN: ${{ github.token }}'
)
$inheritedTokenJobFixture = @(Get-WorkflowJobLines `
    -WorkflowLines $inheritedTokenWorkflowFixture `
    -JobName "validate-recovery")
$inheritedTokenStepFixture = @(Get-WorkflowStepLines `
    -JobLines $inheritedTokenJobFixture `
    -StepName "Verify exact empty draft and annotated source tag")
$inheritedTokenEnvironmentFixture = @(Get-WorkflowRootEnvironmentLines `
    -WorkflowLines $inheritedTokenWorkflowFixture)
Assert-Throws {
    Assert-RecoveryTokenScope `
        -EffectiveJobLines @($inheritedTokenEnvironmentFixture + $inheritedTokenJobFixture) `
        -DraftInspectionLines $inheritedTokenStepFixture
} "only to the draft-inspection step"

$inlineInheritedTokenWorkflowFixture = @(
    "name: Fixture",
    'env: { GH_TOKEN: "${{ github.token }}" }',
    "jobs:",
    "  validate-recovery:",
    "    steps:",
    "      - name: Verify exact empty draft and annotated source tag",
    "        env:",
    '          GH_TOKEN: ${{ github.token }}'
)
$inlineInheritedTokenJobFixture = @(Get-WorkflowJobLines `
    -WorkflowLines $inlineInheritedTokenWorkflowFixture `
    -JobName "validate-recovery")
$inlineInheritedTokenStepFixture = @(Get-WorkflowStepLines `
    -JobLines $inlineInheritedTokenJobFixture `
    -StepName "Verify exact empty draft and annotated source tag")
$inlineInheritedTokenEnvironmentFixture = @(Get-WorkflowRootEnvironmentLines `
    -WorkflowLines $inlineInheritedTokenWorkflowFixture)
Assert-Throws {
    Assert-RecoveryTokenScope `
        -EffectiveJobLines @($inlineInheritedTokenEnvironmentFixture + $inlineInheritedTokenJobFixture) `
        -DraftInspectionLines $inlineInheritedTokenStepFixture
} "only to the draft-inspection step"

$untrustedDraftInspectionFixture = @(
    "      - name: Verify exact empty draft and annotated source tag",
    "        shell: pwsh",
    "        env:",
    '          GH_TOKEN: ${{ github.token }}',
    "        run: ./scripts/untrusted-command.ps1"
)
Assert-Throws {
    Assert-RecoveryTokenScope `
        -EffectiveJobLines $untrustedDraftInspectionFixture `
        -DraftInspectionLines $untrustedDraftInspectionFixture
} "exact credential-bearing draft-inspection command"

$structuralEnvironmentFixture = @(
    "name: Fixture",
    "env:",
    "  FAKE_STEP: |",
    "      - name: Checkout exact workflow SHA",
    "        uses: actions/checkout@untrusted",
    '          ref: ${{ github.sha }}',
    "          persist-credentials: false",
    "jobs:",
    "  validate-recovery:",
    "    steps:",
    "      - name: Actual harmless step",
    "        run: echo harmless"
)
$structuralJobFixture = @(Get-WorkflowJobLines `
    -WorkflowLines $structuralEnvironmentFixture `
    -JobName "validate-recovery")
Assert-Throws {
    Get-WorkflowStepLines `
        -JobLines $structuralJobFixture `
        -StepName "Checkout exact workflow SHA"
} "was not found"

Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @(
            "name: Fixture",
            "jobs:",
            "  validate-recovery:",
            "    steps:",
            "      - { uses: actions/checkout@untrusted }"
        ) `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @()
} "canonical block-style steps"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @(
            '    recovery#guard: { FAKE: "',
            "      - name: Fake trusted action",
            "        uses: actions/checkout@trusted",
            '    " }'
        )
} "canonical block-style steps"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @(
            "    env:",
            '      { FAKE: "',
            "      - name: Fake trusted action",
            "        uses: actions/checkout@trusted",
            '      " }'
        )
} "canonical block-style steps"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @('    recovery#guard: [ "', '    " ]')
} "canonical block-style steps"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @(
            "name: Fixture",
            "jobs:",
            "  recover-draft:",
            "    steps:",
            "      -",
            "        { uses: attacker/action@ref }"
        ) `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @()
} "canonical block-style steps"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @(
            "name: Fixture",
            "jobs:",
            "  recover-draft:",
            "    steps:",
            "      -",
            "       uses: attacker/action@ref"
        ) `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @()
} "canonical block-style steps"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @(
            "name: Fixture",
            "jobs:",
            "  recover-draft:",
            "    steps:",
            "       - uses: attacker/action@ref"
        ) `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @()
} "canonical six-space indentation"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @(
            "name: Fixture",
            "jobs:",
            "  recover-draft:",
            "    name: |",
            "      - name: Fake trusted action",
            "        uses: actions/checkout@trusted"
        ) `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @()
} "allowed only for canonical run steps"
foreach ($multilineQuotedScalarFixture in @(
    @(
        '    recovery#guard: "',
        "      - name: Fake trusted action",
        "        uses: actions/checkout@trusted",
        '    "'
    ),
    @(
        '    name: "ignored \" text',
        "      - name: Fake trusted action",
        "        uses: actions/checkout@trusted",
        '    "'
    ),
    @(
        "    name: '",
        "      - name: Fake trusted action",
        "        uses: actions/checkout@trusted",
        "    '"
    )
)) {
    Assert-Throws {
        Assert-SupportedRecoveryWorkflowSyntax `
            -WorkflowLines @("name: Fixture") `
            -RootEnvironmentLines @() `
            -RecoveryJobLines $multilineQuotedScalarFixture
    } "must not use quoted YAML mapping keys or values"
}
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @(
            "    name:",
            '      "',
            "      - name: Fake trusted action",
            "        uses: actions/checkout@trusted",
            '      "'
        )
} "must not use quoted YAML mapping keys or values"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture", "  hidden:", "    |", "      ignored") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @()
} "allowed only for canonical run steps"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @(
            '    recovery#guard: !!str "',
            "      - name: Fake trusted action",
            "        uses: actions/checkout@trusted",
            '    "'
        )
} "must not use explicit or tagged YAML mapping keys"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @(
            "name: Fixture",
            "jobs:",
            "  validate-recovery:",
            "    steps:",
            "      - name: Quoted action key",
            '        "uses": actions/checkout@untrusted'
        ) `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @()
} "must not use quoted YAML mapping keys or values"
foreach ($nonCanonicalUsesFixture in @(
    "        uses : actions/cache@untrusted"
)) {
    Assert-Throws {
        Assert-SupportedRecoveryWorkflowSyntax `
            -WorkflowLines @(
                "name: Fixture",
                "jobs:",
                "  validate-recovery:",
                "    steps:",
                "      - name: Non-canonical action",
                $nonCanonicalUsesFixture
            ) `
            -RootEnvironmentLines @() `
            -RecoveryJobLines @()
    } "canonical bare uses keys"
}
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @(
            "name: Fixture",
            "jobs:",
            "  recover-draft:",
            "    steps:",
            "      - name: Encoded action key",
            '        "u\u0073es": attacker/action@ref'
        ) `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @()
} "must not use YAML decoded escapes"
foreach ($nonCanonicalMappingKeyFixture in @(
    @(
        "        ? uses",
        "        : attacker/action@ref"
    ),
    @(
        "      - ? run",
        "        : malicious-command"
    ),
    @('        !!str uses: attacker/action@ref')
)) {
    Assert-Throws {
        Assert-SupportedRecoveryWorkflowSyntax `
            -WorkflowLines @("name: Fixture") `
            -RootEnvironmentLines @() `
            -RecoveryJobLines $nonCanonicalMappingKeyFixture
    } "must not use explicit or tagged YAML mapping keys"
}
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @("env:", "  GH_TOKEN: *token_env") `
        -RecoveryJobLines @()
} "anchors or aliases"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @('        &action_key uses: attacker/action@ref')
} "anchors or aliases"
foreach ($numericAnchorFixture in @("env: &1", "env: *1")) {
    Assert-Throws {
        Assert-SupportedRecoveryWorkflowSyntax `
            -WorkflowLines @("name: Fixture") `
            -RootEnvironmentLines @($numericAnchorFixture) `
            -RecoveryJobLines @()
    } "anchors or aliases"
}
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @(
            "  validate-recovery:",
            "    steps:",
            "      - name: Folded token reference",
            "        run: >",
            '          echo "${{ github.token }}"'
        )
} "block or folded scalars"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @(
            "  validate-recovery:",
            "    steps:",
            "      - name: Dump context",
            '        run: echo "${{ toJSON(github) }}"'
        )
} "outside the exact allowlist"
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @() `
        -RecoveryJobLines @(
            "  validate-recovery:",
            "    env:",
            '      LEAK: ${{ toJSON(',
            '        github) }}'
        )
} "outside the exact allowlist"
foreach ($writeScopeEscapeFixture in @(
    @('        env: { LEAK: "${{ git\u0068ub.token }}" }'),
    @(
        '        env: { LEAK: "${{ secr\',
        '          ets.GITHUB_TOKEN }}" }'
    )
)) {
    Assert-Throws {
        Assert-SupportedRecoveryWorkflowSyntax `
            -WorkflowLines @("name: Fixture") `
            -RootEnvironmentLines @() `
            -RecoveryJobLines $writeScopeEscapeFixture
    } "must not use YAML decoded escapes"
}
foreach ($rootDefaultsFixture in @("defaults:", "defaults :")) {
    Assert-Throws {
        Assert-SupportedRecoveryWorkflowSyntax `
            -WorkflowLines @(
                "name: Fixture",
                $rootDefaultsFixture,
                "  run:",
                "    working-directory: attacker-controlled"
            ) `
            -RootEnvironmentLines @() `
            -RecoveryJobLines @()
    } "must not define root run defaults"
}
Assert-Throws {
    Assert-SupportedRecoveryWorkflowSyntax `
        -WorkflowLines @("name: Fixture") `
        -RootEnvironmentLines @("env:", "  PATH: attacker-controlled") `
        -RecoveryJobLines @()
} "must not define a root environment"

$trustedCheckoutFixture = @(
    "      - name: Checkout exact workflow SHA",
    "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1"
)
Assert-Throws {
    Assert-SingleTrustedWorkflowCheckout -JobLines @(
        $trustedCheckoutFixture
        "      - name: Renamed source checkout"
        "        uses: actions/checkout@untrusted"
    ) -TrustedStepLines $trustedCheckoutFixture
} "exactly 1 checkout action"
Assert-Throws {
    Assert-SingleTrustedWorkflowCheckout -JobLines @(
        $trustedCheckoutFixture
        "      - uses: actions/checkout@untrusted"
    ) -TrustedStepLines $trustedCheckoutFixture
} "exactly 1 checkout action"
Assert-Throws {
    Assert-SingleTrustedWorkflowCheckout -JobLines @(
        $trustedCheckoutFixture
        "      - uses: actions/cache@untrusted"
    ) -TrustedStepLines $trustedCheckoutFixture
} "only the pinned trusted checkout action"
Assert-Throws {
    Assert-ExactActionAllowlist `
        -JobLines @(
            $trustedCheckoutFixture
            "      - uses: actions/setup-dotnet@unpinned"
        ) `
        -ExpectedActionLines @(
            "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1"
        )
} "exact pinned allowlist"

$releaseSourceOverrideFixture = @(
    "      - name: Checkout exact release source",
    "        uses: actions/checkout@first",
    "        with:",
    '          ref: ${{ inputs.recovery_source_sha }}',
    "          path: release-source",
    "      - name: Replace release source",
    "        uses: actions/checkout@second",
    "        with:",
    "          ref: untrusted",
    "          path: release-source"
)
Assert-Throws {
    Assert-ExpectedCheckoutCount `
        -JobLines $releaseSourceOverrideFixture `
        -ExpectedCount 1
} "exactly 1 checkout action"
foreach ($repositoryOverrideFixture in @(
    '          "repository": attacker/repository',
    '          repository : attacker/repository'
)) {
    Assert-Throws {
        Assert-NoCheckoutInputKeys `
            -StepLines @($trustedCheckoutFixture + $repositoryOverrideFixture) `
            -ForbiddenKeys @("repository")
    } "forbidden input key"
}
Assert-Throws {
    Assert-ExactWorkflowStepLines `
        -StepLines @(
            $trustedCheckoutFixture
            "        with:"
            "          persist-credentials: false"
            '          "repo\u0073itory": attacker/repository'
        ) `
        -ExpectedLines @(
            $trustedCheckoutFixture
            "        with:"
            "          persist-credentials: false"
        )
} "exact canonical line allowlist"

function Get-CheckoutPersistCredentials {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$JobLines
    )

    $checkoutIndexes = [Collections.Generic.List[int]]::new()
    for ($index = 0; $index -lt $JobLines.Count; $index++) {
        if ($JobLines[$index] -match '^(?:        uses:|      - uses:)\s+[''"]?actions/checkout@') {
            $checkoutIndexes.Add($index)
        }
    }
    if ($checkoutIndexes.Count -eq 0) {
        throw "Workflow job is missing its checkout step."
    }

    $persistenceValues = [Collections.Generic.List[string]]::new()
    foreach ($checkoutIndex in $checkoutIndexes) {
        $persistenceValue = $null
        $withIndex = -1
        for ($index = $checkoutIndex + 1; $index -lt $JobLines.Count; $index++) {
            if ($JobLines[$index] -match '^      -(?:\s|$)') {
                break
            }
            if ($JobLines[$index] -ceq "        with:") {
                if ($withIndex -ge 0) {
                    throw "Workflow checkout must contain exactly one with block."
                }
                $withIndex = $index
                continue
            }
            if ($withIndex -ge 0 -and $JobLines[$index] -match '^        \S') {
                break
            }
            if ($withIndex -ge 0 -and
                $JobLines[$index] -match '^          persist-credentials:\s*(?<value>true|false)\s*$') {
                $persistenceValue = $Matches.value
                break
            }
        }
        if ($null -eq $persistenceValue) {
            throw "Every workflow checkout must declare persist-credentials explicitly."
        }
        $persistenceValues.Add($persistenceValue)
    }

    $uniquePersistenceValues = @($persistenceValues | Select-Object -Unique)
    if ($uniquePersistenceValues.Count -ne 1) {
        throw "Workflow checkout persist-credentials values must be consistent."
    }
    return $uniquePersistenceValues[0]
}

Assert-Throws {
    Get-CheckoutPersistCredentials -JobLines @(
        "      - name: First checkout",
        "        uses: actions/checkout@first",
        "        with:",
        "          persist-credentials: false",
        "      - uses: actions/checkout@second",
        "        with:",
        "          persist-credentials: true"
    )
} "persist-credentials values must be consistent"
Assert-Throws {
    Get-CheckoutPersistCredentials -JobLines @(
        "      - name: Checkout with misplaced persistence",
        "        uses: actions/checkout@first",
        "        env:",
        "          persist-credentials: false"
    )
} "must declare persist-credentials explicitly"
Assert-Throws {
    Get-CheckoutPersistCredentials -JobLines @(
        "      - name: Checkout without persistence",
        "        uses: actions/checkout@first",
        "      -",
        "        name: Following step",
        "        with:",
        "          persist-credentials: false"
    )
} "must declare persist-credentials explicitly"

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
    prerelease = $false
    name = "Custodian 1.5.5"
    body = "Approved notes`n"
    assets = @()
}
$emptyDraftArguments = @{
    Release = $emptyDraft
    DraftId = 376286670
    Version = "1.5.5"
    ExpectedTitle = "Custodian 1.5.5"
    ExpectedBody = "Approved notes`r`n"
}
Assert-CustodianEmptyDraftRelease @emptyDraftArguments
$wrongDraftIdArguments = $emptyDraftArguments.Clone()
$wrongDraftIdArguments.DraftId = 376286671
Assert-Throws { Assert-CustodianEmptyDraftRelease @wrongDraftIdArguments } "does not match"
$wrongVersionArguments = $emptyDraftArguments.Clone()
$wrongVersionArguments.Version = "1.5.6"
Assert-Throws { Assert-CustodianEmptyDraftRelease @wrongVersionArguments } "does not match"
Assert-Throws {
    Assert-CustodianEmptyDraftRelease -Release ([pscustomobject]@{
        id = 376286670; tag_name = "1.5.5"; draft = $false; immutable = $true; prerelease = $false
        name = "Custodian 1.5.5"; body = "Approved notes"; assets = @()
    }) -DraftId 376286670 -Version "1.5.5" -ExpectedTitle "Custodian 1.5.5" -ExpectedBody "Approved notes"
} "not a mutable draft"
Assert-Throws {
    Assert-CustodianEmptyDraftRelease -Release ([pscustomobject]@{
        id = 376286670; tag_name = "1.5.5"; draft = $true; immutable = $false; prerelease = $false
        name = "Custodian 1.5.5"; body = "Approved notes"; assets = @([pscustomobject]@{ name = "existing.exe" })
    }) -DraftId 376286670 -Version "1.5.5" -ExpectedTitle "Custodian 1.5.5" -ExpectedBody "Approved notes"
} "not empty"
foreach ($invalidMetadata in @(
    [pscustomobject]@{ id = 376286670; tag_name = "1.5.5"; draft = $true; immutable = $false; prerelease = $true; name = "Custodian 1.5.5"; body = "Approved notes"; assets = @() },
    [pscustomobject]@{ id = 376286670; tag_name = "1.5.5"; draft = $true; immutable = $false; prerelease = $false; name = "Wrong title"; body = "Approved notes"; assets = @() },
    [pscustomobject]@{ id = 376286670; tag_name = "1.5.5"; draft = $true; immutable = $false; prerelease = $false; name = "Custodian 1.5.5"; body = "Wrong notes"; assets = @() }
)) {
    Assert-Throws {
        Assert-CustodianEmptyDraftRelease -Release $invalidMetadata -DraftId 376286670 -Version "1.5.5" -ExpectedTitle "Custodian 1.5.5" -ExpectedBody "Approved notes"
    } "metadata does not match"
}

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
$resumeDraftScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "resume-empty-release-draft.ps1")
foreach ($ownedUploadContract in @(
    "https://uploads.github.com/",
    "`$uploadedAssetIds.Add([Int64]`$uploaded.id)",
    "No unknown asset will be deleted automatically"
)) {
    if (!$resumeDraftScript.Contains($ownedUploadContract, [StringComparison]::Ordinal)) {
        throw "Empty-draft recovery is missing exact asset-ownership contract '$ownedUploadContract'."
    }
}
foreach ($unsafeCleanupContract in @("attemptedAssetNames", "cleanupDraft.assets")) {
    if ($resumeDraftScript.Contains($unsafeCleanupContract, [StringComparison]::Ordinal)) {
        throw "Empty-draft recovery may delete a concurrent asset through unsafe contract '$unsafeCleanupContract'."
    }
}

$recoveryTestRoot = Join-Path ([IO.Path]::GetTempPath()) "custodian-release-recovery-$([Guid]::NewGuid())"
$recoveryOutput = Join-Path $recoveryTestRoot "artifacts\velopack"
$recoveryNotes = Join-Path $recoveryTestRoot "docs\releases\1.5.5.md"
$previousGhToken = $env:GH_TOKEN
try {
    New-Item -ItemType Directory -Force -Path $recoveryOutput, (Split-Path -Parent $recoveryNotes) | Out-Null
    [IO.File]::WriteAllText($recoveryNotes, "Approved notes`n", [Text.UTF8Encoding]::new($false))
    foreach ($assetName in $expectedRelease) {
        [IO.File]::WriteAllText((Join-Path $recoveryOutput $assetName), "fixture-$assetName", [Text.UTF8Encoding]::new($false))
    }

    $global:recoveryMockUploadCount = 0
    $global:recoveryMockConcurrentVisible = $false
    $global:recoveryMockDeletedAssetIds = [Collections.Generic.List[Int64]]::new()
    $sourceCommit = "54a5b4ce032c852f03db66e9802f92366cd22f1b"
    $tagObject = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    $concurrentAssetId = [Int64]9001
    $ownedAssetId = [Int64]1001
    $concurrentAssetName = $expectedRelease[1]

    function global:git {
        $invocation = [string]::Join(' ', $args)
        $global:LASTEXITCODE = 0
        if ($invocation -like "*-C * ls-remote --tags origin refs/tags/1.5.5 refs/tags/1.5.5^{}") {
            "$tagObject`trefs/tags/1.5.5"
            "$sourceCommit`trefs/tags/1.5.5^{}"
            return
        }
        if ($invocation -like "*-C * fetch --no-tags origin refs/tags/1.5.5") {
            return
        }
        if ($invocation -like "*-C * cat-file -t $tagObject") {
            "tag"
            return
        }
        if ($invocation -like "*-C * cat-file -p $tagObject") {
            "object $sourceCommit"
            "type commit"
            "tag 1.5.5"
            "tagger github-actions[bot] <41898282+github-actions[bot]@users.noreply.github.com> 1787650000 -0400"
            return
        }
        throw "Unexpected mocked git invocation: $invocation"
    }

    function global:gh {
        $invocation = [string]::Join(' ', $args)
        $global:LASTEXITCODE = 0
        [object[]]$releaseAssets = @()
        if ($global:recoveryMockConcurrentVisible) {
            $releaseAssets = @([ordered]@{ id = $concurrentAssetId; name = $concurrentAssetName })
        }
        $release = [ordered]@{
            id = 376286670
            tag_name = "1.5.5"
            draft = $true
            immutable = $false
            prerelease = $false
            name = "Custodian 1.5.5"
            body = "Approved notes`n"
            assets = $releaseAssets
        }

        if ($invocation -eq "api repos/C-Tech-Solutions/custodian/releases/376286670") {
            ConvertTo-Json -InputObject $release -Depth 10 -Compress
            return
        }
        if ($invocation -like "api repos/C-Tech-Solutions/custodian/releases?per_page=100 --paginate --slurp") {
            $releaseJson = ConvertTo-Json -InputObject $release -Depth 10 -Compress
            "[[$releaseJson]]"
            return
        }
        if ($invocation -like "api --method POST *https://uploads.github.com/*") {
            $global:recoveryMockUploadCount++
            if ($global:recoveryMockUploadCount -eq 1) {
                ConvertTo-Json -InputObject ([ordered]@{ id = $ownedAssetId; name = $expectedRelease[0] }) -Compress
                return
            }
            $global:recoveryMockConcurrentVisible = $true
            $global:LASTEXITCODE = 1
            return
        }
        if ($invocation -like "api --method DELETE repos/C-Tech-Solutions/custodian/releases/assets/*") {
            $assetId = [Int64]($invocation -replace '^.*releases/assets/', '')
            $global:recoveryMockDeletedAssetIds.Add($assetId)
            return
        }
        throw "Unexpected mocked gh invocation: $invocation"
    }

    $env:GH_TOKEN = "test-only-token"
    $recoveryFailure = $null
    try {
        & (Join-Path $PSScriptRoot "resume-empty-release-draft.ps1") `
            -Version "1.5.5" `
            -ExpectedCommit $sourceCommit `
            -DraftId 376286670 `
            -SourceRepositoryRoot $recoveryTestRoot
    }
    catch {
        $recoveryFailure = $_.Exception.Message
    }
    if ($recoveryFailure -notlike "*Upload outcome for '$concurrentAssetName' was not confirmed*") {
        throw "Mocked concurrent upload did not fail through the expected path: '$recoveryFailure'."
    }
    if (!$global:recoveryMockConcurrentVisible -or
        $global:recoveryMockDeletedAssetIds.Count -ne 1 -or
        $global:recoveryMockDeletedAssetIds[0] -ne $ownedAssetId -or
        $global:recoveryMockDeletedAssetIds.Contains($concurrentAssetId)) {
        throw "Recovery cleanup did not preserve the concurrent same-named asset while deleting only its owned asset ID."
    }
}
finally {
    $env:GH_TOKEN = $previousGhToken
    Remove-Item Function:\global:gh -ErrorAction SilentlyContinue
    Remove-Item Function:\global:git -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $recoveryTestRoot) {
        $resolvedRecoveryTestRoot = (Resolve-Path -LiteralPath $recoveryTestRoot).Path
        $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if (!$resolvedRecoveryTestRoot.StartsWith($resolvedSystemTemp + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe recovery test cleanup path: '$resolvedRecoveryTestRoot'."
        }
        Remove-Item -LiteralPath $resolvedRecoveryTestRoot -Recurse -Force
    }
}

$releaseWorkflow = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\.github\workflows\release.yml")
$releaseWorkflowLines = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\.github\workflows\release.yml"))
$releaseWorkflowEnvironmentLines = @(Get-WorkflowRootEnvironmentLines -WorkflowLines $releaseWorkflowLines)
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
    "validate-recovery-source:",
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
    @{ Lines = $releaseWorkflowLines; Job = "validate-recovery-source"; Expected = "false" },
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
$permissionEntries = @(Get-WorkflowPermissionEntries -JobLines $validateJobLines)
if ($permissionEntries.Count -ne 1 -or $permissionEntries[0] -cne "contents: read") {
    throw "The pre-environment validation job must not have release-write permission."
}
$recoveryValidationJobLines = @(Get-WorkflowJobLines -WorkflowLines $releaseWorkflowLines -JobName "validate-recovery")
Assert-ExactWorkflowLines `
    -ActualLines $recoveryValidationJobLines `
    -ExpectedLines @(
        "  validate-recovery:",
        "    name: Validate protected empty-draft recovery",
        "    needs: validate-dispatch",
        "    if: inputs.recovery_draft_id != '' && inputs.recovery_source_sha != ''",
        "    runs-on: windows-latest",
        "    permissions:",
        "      # GitHub only exposes draft releases to tokens with push-level contents access.",
        "      contents: write",
        "    env:",
        '      RELEASE_VERSION: ${{ inputs.version }}',
        '      WORKFLOW_SHA: ${{ inputs.commit_sha }}',
        '      RELEASE_SHA: ${{ inputs.recovery_source_sha }}',
        '      RECOVERY_DRAFT_ID: ${{ inputs.recovery_draft_id }}',
        "    steps:",
        "      - name: Checkout exact workflow SHA",
        "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
        "        with:",
        '          ref: ${{ github.sha }}',
        "          fetch-depth: 0",
        "          persist-credentials: false",
        "",
        "      - name: Test recovery workflow contracts",
        "        shell: pwsh",
        "        run: ./scripts/test-release-scripts.ps1",
        "",
        "      - name: Verify exact empty draft and annotated source tag",
        "        shell: pwsh",
        "        env:",
        '          GH_TOKEN: ${{ github.token }}',
        '        run: ./scripts/assert-release-draft-recovery.ps1 -Version $env:RELEASE_VERSION -WorkflowCommitSha $env:WORKFLOW_SHA -SourceCommitSha $env:RELEASE_SHA -DraftId $env:RECOVERY_DRAFT_ID'
    ) `
    -FailureMessage "The write-capable recovery validation job must match its exact trusted contract."
Assert-SupportedRecoveryWorkflowSyntax `
    -WorkflowLines $releaseWorkflowLines `
    -RootEnvironmentLines $releaseWorkflowEnvironmentLines `
    -RecoveryJobLines $recoveryValidationJobLines
$recoveryCheckoutLines = @(Get-WorkflowStepLines `
    -JobLines $recoveryValidationJobLines `
    -StepName "Checkout exact workflow SHA")
if (!($recoveryCheckoutLines -contains "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1") -or
    !($recoveryCheckoutLines -contains '          ref: ${{ github.sha }}') -or
    !($recoveryCheckoutLines -contains "          persist-credentials: false") -or
    ($recoveryCheckoutLines -contains '          ref: ${{ inputs.commit_sha }}')) {
    throw "Recovery validation must load write-token inspection code from the trusted workflow revision."
}
Assert-SingleTrustedWorkflowCheckout `
    -JobLines $recoveryValidationJobLines `
    -TrustedStepLines $recoveryCheckoutLines
$recoveryPermissions = @(Get-WorkflowPermissionEntries -JobLines $recoveryValidationJobLines)
if ($recoveryPermissions.Count -ne 1 -or $recoveryPermissions[0] -cne "contents: write") {
    throw "Recovery validation requires contents write permission to retrieve an existing draft release."
}
$draftInspectionLines = @(Get-WorkflowStepLines `
    -JobLines $recoveryValidationJobLines `
    -StepName "Verify exact empty draft and annotated source tag")
Assert-RecoveryTokenScope `
    -EffectiveJobLines @($releaseWorkflowEnvironmentLines + $recoveryValidationJobLines) `
    -DraftInspectionLines $draftInspectionLines
$recoverySourceValidationJobLines = @(Get-WorkflowJobLines -WorkflowLines $releaseWorkflowLines -JobName "validate-recovery-source")
Assert-ExactWorkflowLines `
    -ActualLines $recoverySourceValidationJobLines `
    -ExpectedLines @(
        "  validate-recovery-source:",
        "    name: Validate exact recovery source",
        "    needs: validate-recovery",
        "    if: needs.validate-recovery.result == 'success'",
        "    runs-on: windows-latest",
        "    permissions:",
        "      contents: read",
        "    steps:",
        "      - name: Checkout exact release source",
        "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
        "        with:",
        '          ref: ${{ inputs.recovery_source_sha }}',
        "          path: release-source",
        "          fetch-depth: 0",
        "          persist-credentials: false",
        "",
        "      - name: Setup .NET",
        "        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68",
        "        with:",
        "          global-json-file: release-source/global.json",
        "",
        "      - name: Restore locked release-source dependencies",
        "        working-directory: release-source",
        "        run: dotnet restore Custodian.slnx --locked-mode",
        "",
        "      - name: Build exact release source",
        "        working-directory: release-source",
        "        run: dotnet build Custodian.slnx --configuration Release --no-restore",
        "",
        "      - name: Test exact release source",
        "        working-directory: release-source",
        "        run: dotnet test Custodian.slnx --configuration Release --no-build",
        "",
        "      - name: Scan exact release-source dependencies",
        "        working-directory: release-source",
        "        shell: pwsh",
        "        run: |",
        '          $scan = & dotnet list Custodian.slnx package --vulnerable --include-transitive --no-restore 2>&1',
        '          $exitCode = $LASTEXITCODE',
        '          $scan | Write-Host',
        '          if ($exitCode -ne 0) {',
        '            throw "NuGet vulnerability scan failed with exit code $exitCode."',
        "          }",
        '          if ($scan -match "has the following vulnerable packages") {',
        '            throw "Vulnerable NuGet packages were detected."',
        "          }",
        '          if ($scan -match "\bNU1900\b|\bNU1905\b") {',
        '            throw "NuGet audit data was incomplete or unavailable."',
        "          }"
    ) `
    -FailureMessage "The read-only recovery source validation job must match its exact trusted contract."
$recoverySourcePermissions = @(Get-WorkflowPermissionEntries -JobLines $recoverySourceValidationJobLines)
Assert-NoGitHubOrSecretsContextReferences `
    -WorkflowLines @($releaseWorkflowEnvironmentLines + $recoverySourceValidationJobLines)
if ($recoverySourcePermissions.Count -ne 1 -or
    $recoverySourcePermissions[0] -cne "contents: read" -or
    !($recoverySourceValidationJobLines -contains "      - name: Checkout exact release source") -or
    @(Get-GitHubCliTokenAssignments -WorkflowLines @($releaseWorkflowEnvironmentLines + $recoverySourceValidationJobLines)).Count -ne 0 -or
    @(Get-ExplicitGitHubTokenReferences -WorkflowLines @($releaseWorkflowEnvironmentLines + $recoverySourceValidationJobLines)).Count -ne 0) {
    throw "Historical release-source validation must run in a separate read-only job without GH_TOKEN exposure."
}
$recoverDraftJobLines = @(Get-WorkflowJobLines -WorkflowLines $releaseWorkflowLines -JobName "recover-draft")
$validatedSourceCheckoutLines = @(Get-WorkflowStepLines `
    -JobLines $recoverySourceValidationJobLines `
    -StepName "Checkout exact release source")
$signingWorkflowCheckoutLines = @(Get-WorkflowStepLines `
    -JobLines $recoverDraftJobLines `
    -StepName "Checkout exact recovery workflow SHA")
$signingSourceCheckoutLines = @(Get-WorkflowStepLines `
    -JobLines $recoverDraftJobLines `
    -StepName "Checkout exact release source")
$publishRecoveredJobLines = @(Get-WorkflowJobLines -WorkflowLines $releaseWorkflowLines -JobName "publish-recovered")
$publishingWorkflowCheckoutLines = @(Get-WorkflowStepLines `
    -JobLines $publishRecoveredJobLines `
    -StepName "Checkout exact recovery workflow SHA")
$publishingSourceCheckoutLines = @(Get-WorkflowStepLines `
    -JobLines $publishRecoveredJobLines `
    -StepName "Checkout exact release source")
Assert-ExpectedCheckoutCount -JobLines $recoverySourceValidationJobLines -ExpectedCount 1
Assert-ExpectedCheckoutCount -JobLines $recoverDraftJobLines -ExpectedCount 2
Assert-ExpectedCheckoutCount -JobLines $publishRecoveredJobLines -ExpectedCount 2
$checkoutAction = "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1"
$setupDotnetAction = "        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68"
Assert-ExactActionAllowlist `
    -JobLines $recoverySourceValidationJobLines `
    -ExpectedActionLines @($checkoutAction, $setupDotnetAction)
$sourceValidationStepContracts = @(
    @{
        Name = "Restore locked release-source dependencies"
        Lines = @(
            "      - name: Restore locked release-source dependencies",
            "        working-directory: release-source",
            "        run: dotnet restore Custodian.slnx --locked-mode"
        )
    },
    @{
        Name = "Build exact release source"
        Lines = @(
            "      - name: Build exact release source",
            "        working-directory: release-source",
            "        run: dotnet build Custodian.slnx --configuration Release --no-restore"
        )
    },
    @{
        Name = "Test exact release source"
        Lines = @(
            "      - name: Test exact release source",
            "        working-directory: release-source",
            "        run: dotnet test Custodian.slnx --configuration Release --no-build"
        )
    },
    @{
        Name = "Scan exact release-source dependencies"
        Lines = @(
            "      - name: Scan exact release-source dependencies",
            "        working-directory: release-source",
            "        shell: pwsh",
            "        run: |",
            '          $scan = & dotnet list Custodian.slnx package --vulnerable --include-transitive --no-restore 2>&1',
            '          $exitCode = $LASTEXITCODE',
            '          $scan | Write-Host',
            '          if ($exitCode -ne 0) {',
            '            throw "NuGet vulnerability scan failed with exit code $exitCode."',
            "          }",
            '          if ($scan -match "has the following vulnerable packages") {',
            '            throw "Vulnerable NuGet packages were detected."',
            "          }",
            '          if ($scan -match "\bNU1900\b|\bNU1905\b") {',
            '            throw "NuGet audit data was incomplete or unavailable."',
            "          }"
        )
    }
)
foreach ($sourceValidationStepContract in $sourceValidationStepContracts) {
    $sourceValidationStepLines = @(Get-WorkflowStepLines `
        -JobLines $recoverySourceValidationJobLines `
        -StepName $sourceValidationStepContract.Name)
    Assert-ExactWorkflowStepLines `
        -StepLines $sourceValidationStepLines `
        -ExpectedLines $sourceValidationStepContract.Lines
}
Assert-ExactActionAllowlist `
    -JobLines $recoverDraftJobLines `
    -ExpectedActionLines @(
        $checkoutAction,
        $checkoutAction,
        $setupDotnetAction,
        "        uses: azure/login@f5d393ae46f8fde4be8b75f32e3fc50e654ad0ca",
        "        uses: Azure/artifact-signing-action@c7ab2a863ab5f9a846ddb8265964877ef296ee82",
        "        uses: actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6"
    )
Assert-ExactActionAllowlist `
    -JobLines $publishRecoveredJobLines `
    -ExpectedActionLines @($checkoutAction, $checkoutAction, $setupDotnetAction)
$expectedWorkflowCheckoutLines = @(
    "      - name: Checkout exact recovery workflow SHA",
    $checkoutAction,
    "        with:",
    '          ref: ${{ inputs.commit_sha }}',
    "          fetch-depth: 0",
    "          persist-credentials: false"
)
foreach ($workflowCheckoutLines in @(
    $signingWorkflowCheckoutLines,
    $publishingWorkflowCheckoutLines
)) {
    Assert-ExactWorkflowStepLines `
        -StepLines $workflowCheckoutLines `
        -ExpectedLines $expectedWorkflowCheckoutLines
    Assert-NoCheckoutInputKeys `
        -StepLines $workflowCheckoutLines `
        -ForbiddenKeys @("path", "repository")
}
$expectedSourceCheckoutLines = @(
    "      - name: Checkout exact release source",
    $checkoutAction,
    "        with:",
    '          ref: ${{ inputs.recovery_source_sha }}',
    "          path: release-source",
    "          fetch-depth: 0",
    "          persist-credentials: false"
)
foreach ($sourceCheckoutLines in @(
    $validatedSourceCheckoutLines,
    $signingSourceCheckoutLines,
    $publishingSourceCheckoutLines
)) {
    Assert-ExactWorkflowStepLines `
        -StepLines $sourceCheckoutLines `
        -ExpectedLines $expectedSourceCheckoutLines
    Assert-NoCheckoutInputKeys `
        -StepLines $sourceCheckoutLines `
        -ForbiddenKeys @("repository")
}
if (!($recoverySourceValidationJobLines -contains "    needs: validate-recovery") -or
    !($recoverySourceValidationJobLines -contains "    if: needs.validate-recovery.result == 'success'") -or
    !($recoverDraftJobLines -contains "    needs: validate-recovery-source") -or
    !($recoverDraftJobLines -contains "    if: needs.validate-recovery-source.result == 'success'") -or
    !($publishRecoveredJobLines -contains "    needs: [validate-recovery-source, recover-draft]") -or
    !($publishRecoveredJobLines -contains "    if: needs.validate-recovery-source.result == 'success' && needs.recover-draft.result == 'success'")) {
    throw "Recovery signing and publication must depend on successful read-only source validation."
}

foreach ($auditContract in @("--no-restore", "NU1900", "NU1905")) {
    if (!$releaseWorkflow.Contains($auditContract, [StringComparison]::Ordinal) -or
        !$ciWorkflow.Contains($auditContract, [StringComparison]::Ordinal)) {
        throw "CI or release vulnerability audit is missing fail-closed contract '$auditContract'."
    }
}

Write-Host "Release script contract tests passed."
