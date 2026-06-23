param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\portable",
    [switch]$Sign,
    [string]$AzureSigningMetadataPath,
    [string]$SignToolPath,
    [string]$AzureSigningDlibPath,
    [string]$TimestampUrl,
    [switch]$SkipSigningVerification,
    [switch]$DebugSigning
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$output = Join-Path $repo $OutputRoot
$appOut = Join-Path $output "Custodian"
$signScript = Join-Path $PSScriptRoot "sign-azure-artifact.ps1"

if (Test-Path $appOut) {
    Remove-Item $appOut -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $appOut | Out-Null

dotnet publish (Join-Path $repo "src\Custodian.App\Custodian.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $appOut

dotnet publish (Join-Path $repo "src\Custodian.Cli\Custodian.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o (Join-Path $appOut "cli")

if ($Sign) {
    $signArgs = @{}
    if (![string]::IsNullOrWhiteSpace($AzureSigningMetadataPath)) { $signArgs.MetadataPath = $AzureSigningMetadataPath }
    if (![string]::IsNullOrWhiteSpace($SignToolPath)) { $signArgs.SignToolPath = $SignToolPath }
    if (![string]::IsNullOrWhiteSpace($AzureSigningDlibPath)) { $signArgs.DlibPath = $AzureSigningDlibPath }
    if (![string]::IsNullOrWhiteSpace($TimestampUrl)) { $signArgs.TimestampUrl = $TimestampUrl }
    if ($SkipSigningVerification) { $signArgs.SkipVerify = $true }
    if ($DebugSigning) { $signArgs.DebugSigning = $true }

    $signTargets = @(Get-ChildItem -Path $appOut -Recurse -File | Where-Object { $_.Extension -in @(".exe", ".dll") })
    if ($signTargets.Count -eq 0) {
        throw "No portable executables or DLLs were found to sign under $appOut."
    }

    & $signScript -Path $signTargets.FullName @signArgs
}

Compress-Archive -Path (Join-Path $appOut "*") -DestinationPath (Join-Path $output "Custodian-win-x64-portable.zip") -Force

Write-Host "Portable build: $appOut"
Write-Host "Portable zip:   $(Join-Path $output "Custodian-win-x64-portable.zip")"
