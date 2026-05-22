param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\velopack",
    [string]$PackId = "Custodian.DiskAnalyzer",
    [string]$Channel = "win",
    [switch]$PreserveExistingReleases
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRoot = Join-Path $repo "artifacts\velopack-publish"
$appOut = Join-Path $publishRoot "Custodian"
$output = Join-Path $repo $OutputRoot

if (Test-Path $publishRoot) {
    Remove-Item $publishRoot -Recurse -Force
}

if (!$PreserveExistingReleases -and (Test-Path $output)) {
    Remove-Item $output -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $appOut | Out-Null
New-Item -ItemType Directory -Force -Path $output | Out-Null

dotnet tool restore --tool-manifest (Join-Path $repo "dotnet-tools.json")

dotnet publish (Join-Path $repo "src\Custodian.App\Custodian.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -o $appOut

dotnet publish (Join-Path $repo "src\Custodian.Cli\Custodian.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -o (Join-Path $appOut "cli")

dotnet vpk pack `
    --packId $PackId `
    --packVersion $Version `
    --packDir $appOut `
    --mainExe "Custodian.App.exe" `
    --packTitle "Custodian Disk Analyzer" `
    --packAuthors "Custodian" `
    --runtime $Runtime `
    --channel $Channel `
    --shortcuts "Desktop,StartMenuRoot" `
    --outputDir $output

Write-Host "Velopack publish input: $appOut"
Write-Host "Velopack release output: $output"
