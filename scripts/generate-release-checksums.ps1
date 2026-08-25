param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputRoot = "artifacts\velopack"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"
Import-Module $releaseTools -Force

$output = (Resolve-Path -LiteralPath (Join-Path $repo $OutputRoot)).Path
$assetNames = [Collections.Generic.List[string]]::new()
$assetNames.AddRange([string[]]@(Get-CustodianVelopackAssetNames -Version $Version))
$assetNames.Add("Custodian-$Version.spdx.json")
$lines = foreach ($assetName in $assetNames) {
    $assetPath = Join-Path $output $assetName
    if (!(Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Cannot checksum missing release asset '$assetPath'."
    }

    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $assetName"
}

$checksumPath = Join-Path $output "SHA256SUMS.txt"
[IO.File]::WriteAllLines($checksumPath, $lines, [Text.UTF8Encoding]::new($false))
Write-Host "Checksums: $checksumPath"
