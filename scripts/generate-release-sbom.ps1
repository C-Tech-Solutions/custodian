param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$CommitSha,
    [string]$PublishRoot = "artifacts\velopack-publish\Custodian",
    [string]$OutputRoot = "artifacts\velopack",
    [string]$ToolVersion = "4.1.5"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$publish = (Resolve-Path -LiteralPath (Join-Path $repo $PublishRoot)).Path
$output = (Resolve-Path -LiteralPath (Join-Path $repo $OutputRoot)).Path
$toolDirectory = Join-Path ([IO.Path]::GetTempPath()) "custodian-sbom-tool-$ToolVersion"
$manifestDirectory = Join-Path $repo "artifacts\sbom"
$destination = Join-Path $output "Custodian-$Version.spdx.json"

New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null
$sbomTool = Join-Path $toolDirectory "sbom-tool.exe"
if (!(Test-Path -LiteralPath $sbomTool -PathType Leaf)) {
    dotnet tool install --tool-path $toolDirectory Microsoft.Sbom.DotNetTool --version $ToolVersion
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install Microsoft.Sbom.DotNetTool $ToolVersion."
    }
}

& $sbomTool generate `
    -b $publish `
    -bc $repo `
    -m $manifestDirectory `
    -pn "Custodian" `
    -pv $Version `
    -ps "Organization: C-Tech Solutions LLC" `
    -nsb "https://github.com/C-Tech-Solutions/custodian" `
    -nsu "Custodian-$Version-$($CommitSha.ToLowerInvariant())" `
    -D true `
    -F false
if ($LASTEXITCODE -ne 0) {
    throw "SBOM generation failed with exit code $LASTEXITCODE."
}

$generated = @(Get-ChildItem -LiteralPath $manifestDirectory -Recurse -File -Filter "*.spdx.json")
if ($generated.Count -ne 1) {
    throw "Expected exactly one generated SPDX JSON file, found $($generated.Count)."
}

Copy-Item -LiteralPath $generated[0].FullName -Destination $destination -Force
$sbom = Get-Content -Raw -LiteralPath $destination | ConvertFrom-Json -Depth 100
if ($sbom.spdxVersion -notmatch '^SPDX-' -or
    $sbom.name -notmatch 'Custodian' -or
    $sbom.documentNamespace -notmatch [regex]::Escape($Version)) {
    throw "Generated SBOM metadata did not identify Custodian $Version."
}

Write-Host "SBOM: $destination"
