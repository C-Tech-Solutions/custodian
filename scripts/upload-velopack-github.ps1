param(
    [string]$OutputRoot = "artifacts\velopack",
    [string]$RepositoryUrl = "https://github.com/ctech1313/custodian",
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Channel = "win",
    [switch]$Publish
)

# Security note: vpk requires the GitHub token as a command-line argument (--token),
# which is briefly visible to other local users via the process command line while the
# upload runs. This script is intended to run on a trusted single-user build/release host;
# do not run it on a shared/multi-user machine. Prefer a short-lived, release-scoped token
# and revoke it after publishing.
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$output = Join-Path $repo $OutputRoot

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "Set GITHUB_TOKEN or pass -Token before uploading Velopack release assets."
}

dotnet tool restore --tool-manifest (Join-Path $repo "dotnet-tools.json")

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
