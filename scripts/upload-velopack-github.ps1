param(
    [string]$OutputRoot = "artifacts\velopack",
    [string]$RepositoryUrl = "https://github.com/ctech1313/custodian",
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Channel = "win",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$output = Join-Path $repo $OutputRoot

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "Set GITHUB_TOKEN or pass -Token before uploading Velopack release assets."
}

$vpkArgs = @(
    "vpk", "upload", "github",
    "--outputDir", $output,
    "--channel", $Channel,
    "--repoUrl", $RepositoryUrl,
    "--token", $Token
)

if ($Publish) {
    $vpkArgs += "--publish"
}

dotnet @vpkArgs
