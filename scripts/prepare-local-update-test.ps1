param(
    [string]$BaseVersion = "1.0.0",
    [string]$UpdateVersion = "1.0.1",
    [string]$OutputRoot = "artifacts\velopack-local",
    [string]$BaselineRoot = "artifacts\velopack-local-baseline"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$baseline = Join-Path $repo $BaselineRoot

if (Test-Path $baseline) {
    Remove-Item $baseline -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $baseline | Out-Null

& (Join-Path $PSScriptRoot "publish-velopack.ps1") -Version $BaseVersion -OutputRoot $OutputRoot

$releaseOutput = Join-Path $repo $OutputRoot
$setup = Get-ChildItem -Path $releaseOutput -Filter "*-Setup.exe" | Select-Object -First 1
if ($null -eq $setup) {
    throw "Velopack setup executable was not found in $releaseOutput."
}

$baselineSetup = Join-Path $baseline "Custodian-$BaseVersion-Setup.exe"
Copy-Item -LiteralPath $setup.FullName -Destination $baselineSetup -Force

& (Join-Path $PSScriptRoot "publish-velopack.ps1") -Version $UpdateVersion -OutputRoot $OutputRoot -PreserveExistingReleases

Write-Host "Baseline installer: $baselineSetup"
Write-Host "Local update feed:  $releaseOutput"
Write-Host "Set CUSTODIAN_UPDATE_SOURCE=$releaseOutput before launching the installed baseline app."
