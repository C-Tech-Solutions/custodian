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

Export-ModuleMember -Function @(
    "Assert-CustodianPublishPhase",
    "Test-CustodianDatedChangelogEntry",
    "Get-CustodianVelopackAssetNames",
    "Get-CustodianReleaseAssetNames",
    "Assert-CustodianReleaseAbsent",
    "New-CustodianDraftReleaseArguments",
    "Get-CustodianGitHubReleasesByTag"
)
