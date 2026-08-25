Set-StrictMode -Version Latest

function Assert-CustodianPublishPhase {
    [CmdletBinding()]
    param(
        [bool]$PrepareOnly,
        [bool]$PackOnly,
        [bool]$Sign,
        [bool]$HasSigningOptions
    )

    if ($PrepareOnly -and $PackOnly) {
        throw "-PrepareOnly and -PackOnly cannot be used together."
    }

    if ($PrepareOnly -and ($Sign -or $HasSigningOptions)) {
        throw "Prepare-only publishing cannot use Velopack signing options. Prepare the tree first, then sign generated Velopack PEs while packing."
    }

    if (!$Sign -and $HasSigningOptions) {
        throw "Signing configuration was supplied without -Sign."
    }
}

function Test-CustodianDatedChangelogEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ChangelogText,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    return $ChangelogText -match "(?m)^## $([regex]::Escape($Version)) - \d{4}-\d{2}-\d{2}\r?$"
}

function Get-CustodianVelopackAssetNames {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
        [string]$Version
    )

    return @(
        "Custodian.DiskAnalyzer-$Version-full.nupkg",
        "Custodian.DiskAnalyzer-win-Portable.zip",
        "Custodian.DiskAnalyzer-win-Setup.exe",
        "RELEASES",
        "releases.win.json"
    )
}

function Get-CustodianReleaseAssetNames {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $assetNames = [Collections.Generic.List[string]]::new()
    $assetNames.AddRange([string[]]@(Get-CustodianVelopackAssetNames -Version $Version))
    $assetNames.Add("Custodian-$Version.spdx.json")
    $assetNames.Add("SHA256SUMS.txt")
    return $assetNames.ToArray()
}

function Assert-CustodianReleaseAbsent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [bool]$ReleaseExists,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($ReleaseExists) {
        throw "GitHub release '$Version' already exists. Existing releases and assets are never overwritten."
    }
}

function New-CustodianDraftReleaseArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$NotesPath
    )

    return @(
        "release", "create", $Version,
        "--repo", $Repository,
        "--verify-tag",
        "--draft",
        "--title", "Custodian $Version",
        "--notes-file", $NotesPath
    )
}

function Get-CustodianGitHubReleasesByTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $rawPages = (& gh api "repos/$Repository/releases?per_page=100" --paginate --slurp | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate GitHub releases for '$Repository'."
    }

    $pages = ConvertFrom-Json -InputObject $rawPages -Depth 50
    $matches = [Collections.Generic.List[object]]::new()
    foreach ($page in $pages) {
        foreach ($release in $page) {
            if ($release.tag_name -ceq $Version) {
                $matches.Add($release)
            }
        }
    }

    return $matches.ToArray()
}

function Wait-CustodianGitHubReleaseByTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [ValidateRange(1, 30)]
        [int]$Attempts = 10,
        [ValidateRange(0, 30)]
        [int]$DelaySeconds = 2
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $matches = @(Get-CustodianGitHubReleasesByTag -Repository $Repository -Version $Version)
        if ($matches.Count -gt 1) {
            throw "Multiple GitHub releases exist for '$Version'."
        }
        if ($matches.Count -eq 1) {
            return $matches[0]
        }
        if ($attempt -lt $Attempts -and $DelaySeconds -gt 0) {
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    return $null
}

function Assert-CustodianEmptyDraftRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Release,
        [Parameter(Mandatory = $true)]
        [Int64]$DraftId,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ([Int64]$Release.id -ne $DraftId -or $Release.tag_name -cne $Version) {
        throw "Draft release '$DraftId' does not match tag '$Version'."
    }
    if (!$Release.draft -or $Release.immutable) {
        throw "Release '$DraftId' is not a mutable draft."
    }
    if (@($Release.assets).Count -ne 0) {
        throw "Draft release '$DraftId' is not empty; existing assets are never overwritten."
    }
}

Export-ModuleMember -Function @(
    "Assert-CustodianPublishPhase",
    "Test-CustodianDatedChangelogEntry",
    "Get-CustodianVelopackAssetNames",
    "Get-CustodianReleaseAssetNames",
    "Assert-CustodianReleaseAbsent",
    "New-CustodianDraftReleaseArguments",
    "Get-CustodianGitHubReleasesByTag",
    "Wait-CustodianGitHubReleaseByTag",
    "Assert-CustodianEmptyDraftRelease"
)
