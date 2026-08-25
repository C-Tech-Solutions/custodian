param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\velopack",
    [string]$PackId = "Custodian.DiskAnalyzer",
    [string]$Channel = "win",
    [switch]$PreserveExistingReleases,
    [switch]$PrepareOnly,
    [switch]$PackOnly,
    [switch]$Sign,
    [string]$AzureSigningMetadataPath,
    [string]$SignToolPath,
    [string]$AzureSigningDlibPath,
    [string]$TimestampUrl,
    [switch]$SkipSigningVerification,
    [switch]$DebugSigning
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repo "artifacts\velopack-publish"
$appOut = Join-Path $publishRoot "Custodian"
$output = Join-Path $repo $OutputRoot
$signScript = Join-Path $PSScriptRoot "sign-azure-artifact.ps1"
$packageIcon = Join-Path $repo "src\Custodian.App\Assets\Custodian.ico"
$releaseTools = Join-Path $PSScriptRoot "ReleaseTools.psm1"

Import-Module $releaseTools -Force

$hasSigningOptions =
    ![string]::IsNullOrWhiteSpace($AzureSigningMetadataPath) -or
    ![string]::IsNullOrWhiteSpace($SignToolPath) -or
    ![string]::IsNullOrWhiteSpace($AzureSigningDlibPath) -or
    ![string]::IsNullOrWhiteSpace($TimestampUrl) -or
    $SkipSigningVerification -or
    $DebugSigning
Assert-CustodianPublishPhase `
    -PrepareOnly $PrepareOnly `
    -PackOnly $PackOnly `
    -Sign $Sign `
    -HasSigningOptions $hasSigningOptions

$shouldPrepare = !$PackOnly
$shouldPack = !$PrepareOnly

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

function Add-TemplateArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Template,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Template
    }

    return "$Template -$Name `"$Value`""
}

function New-AzureSigningTemplate {
    $template = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$signScript`" -Path `"{{file}}`""
    $template = Add-TemplateArgument -Template $template -Name "MetadataPath" -Value $AzureSigningMetadataPath
    $template = Add-TemplateArgument -Template $template -Name "SignToolPath" -Value $SignToolPath
    $template = Add-TemplateArgument -Template $template -Name "DlibPath" -Value $AzureSigningDlibPath
    $template = Add-TemplateArgument -Template $template -Name "TimestampUrl" -Value $TimestampUrl

    if ($SkipSigningVerification) {
        $template = "$template -SkipVerify"
    }

    if ($DebugSigning) {
        $template = "$template -DebugSigning"
    }

    return $template
}

$numericVersion = Get-NumericVersion -InputVersion $Version

if ($shouldPrepare) {
    if (Test-Path $publishRoot) {
        Remove-Item $publishRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $appOut | Out-Null

    dotnet restore (Join-Path $repo "Custodian.slnx") --locked-mode --runtime $Runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed before preparing the Velopack publish tree."
    }

    dotnet publish (Join-Path $repo "src\Custodian.App\Custodian.App.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        --no-restore `
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
        --no-restore `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -p:AssemblyVersion=$numericVersion `
        -p:FileVersion=$numericVersion `
        -p:InformationalVersion=$Version `
        -o (Join-Path $appOut "cli")

    dotnet publish (Join-Path $repo "src\Custodian.Tui\Custodian.Tui.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        --no-restore `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -p:AssemblyVersion=$numericVersion `
        -p:FileVersion=$numericVersion `
        -p:InformationalVersion=$Version `
        -o (Join-Path $appOut "tui")

    Write-Host "Velopack publish input prepared: $appOut"
}

if (!$shouldPack) {
    return
}

if (!(Test-Path -LiteralPath $appOut -PathType Container)) {
    throw "Prepared Velopack publish input was not found at '$appOut'. Run with -PrepareOnly first."
}

foreach ($requiredFile in @("Custodian.App.exe", "cli\Custodian.Cli.exe", "tui\Custodian.Tui.exe")) {
    $requiredPath = Join-Path $appOut $requiredFile
    if (!(Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Prepared Velopack publish input is incomplete. Missing '$requiredPath'."
    }
}

if (!$PreserveExistingReleases -and (Test-Path $output)) {
    Remove-Item $output -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $output | Out-Null

if (!(Test-Path -LiteralPath $packageIcon -PathType Leaf)) {
    throw "Velopack package icon was not found at '$packageIcon'."
}

dotnet tool restore --tool-manifest (Join-Path $repo "dotnet-tools.json")
if ($LASTEXITCODE -ne 0) {
    throw "The pinned Velopack tool restore failed."
}

$vpkArgs = @(
    "vpk",
    "pack",
    "--packId", $PackId,
    "--packVersion", $Version,
    "--packDir", $appOut,
    "--mainExe", "Custodian.App.exe",
    "--packTitle", "Custodian Disk Analyzer",
    "--packAuthors", "Custodian",
    "--runtime", $Runtime,
    "--channel", $Channel,
    "--icon", $packageIcon,
    "--shortcuts", "Desktop,StartMenuRoot",
    "--outputDir", $output
)

if ($Sign) {
    $vpkArgs += @("--signTemplate", (New-AzureSigningTemplate), "--signParallel", "1")
}

dotnet @vpkArgs
if ($LASTEXITCODE -ne 0) {
    throw "Velopack packaging failed with exit code $LASTEXITCODE."
}

$internalAssetIndex = Join-Path $output "assets.$Channel.json"
if (Test-Path -LiteralPath $internalAssetIndex -PathType Leaf) {
    Remove-Item -LiteralPath $internalAssetIndex -Force
}

Write-Host "Velopack publish input: $appOut"
Write-Host "Velopack release output: $output"
