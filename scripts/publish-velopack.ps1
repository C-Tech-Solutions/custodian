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

function Get-NumericVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InputVersion
    )

    $coreVersion = ($InputVersion -split '[+-]', 2)[0]
    $parts = @($coreVersion -split '\.')
    if ($parts.Count -lt 1 -or $parts.Count -gt 4) {
        throw "Version '$InputVersion' must use one to four numeric components before prerelease metadata."
    }

    foreach ($part in $parts) {
        if ($part -notmatch '^\d+$') {
            throw "Version '$InputVersion' contains non-numeric assembly version component '$part'."
        }
    }

    while ($parts.Count -lt 3) {
        $parts += "0"
    }

    return ($parts -join ".")
}

$numericVersion = Get-NumericVersion -InputVersion $Version

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
    -p:AssemblyVersion=$numericVersion `
    -p:FileVersion=$numericVersion `
    -p:InformationalVersion=$Version `
    -o $appOut

dotnet publish (Join-Path $repo "src\Custodian.Cli\Custodian.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$numericVersion `
    -p:FileVersion=$numericVersion `
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
