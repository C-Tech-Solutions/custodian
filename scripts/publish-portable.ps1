param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\portable"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$output = Join-Path $repo $OutputRoot
$appOut = Join-Path $output "Custodian"

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

Compress-Archive -Path (Join-Path $appOut "*") -DestinationPath (Join-Path $output "Custodian-win-x64-portable.zip") -Force

Write-Host "Portable build: $appOut"
Write-Host "Portable zip:   $(Join-Path $output "Custodian-win-x64-portable.zip")"
