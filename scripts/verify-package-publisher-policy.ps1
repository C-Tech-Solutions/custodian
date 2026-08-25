[CmdletBinding(DefaultParameterSetName = "Package")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Package")]
    [string]$PackagePath,
    [Parameter(Mandatory = $true, ParameterSetName = "PublishTree")]
    [string]$PublishRoot,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$temporaryPackage = $null
$resolvedPackage = $null

try {
    if ($PSCmdlet.ParameterSetName -eq "Package") {
        $resolvedPackage = (Resolve-Path -LiteralPath (Join-Path $repo $PackagePath)).Path
    }
    else {
        $resolvedPublishRoot = (Resolve-Path -LiteralPath (Join-Path $repo $PublishRoot)).Path
        $temporaryPackage = Join-Path ([IO.Path]::GetTempPath()) ("custodian-publisher-policy-{0}.nupkg" -f [Guid]::NewGuid().ToString("N"))
        $archive = [IO.Compression.ZipFile]::Open($temporaryPackage, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $resolvedPublishRoot -Recurse -File | Sort-Object -Property FullName) {
                $relativePath = [IO.Path]::GetRelativePath($resolvedPublishRoot, $file.FullName).Replace('\', '/')
                $entryName = "lib/app/$relativePath"
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive,
                    $file.FullName,
                    $entryName,
                    [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }

        $resolvedPackage = $temporaryPackage
    }

    $priorPackage = $env:CUSTODIAN_TEST_SIGNED_RELEASE_PACKAGE
    try {
        $env:CUSTODIAN_TEST_SIGNED_RELEASE_PACKAGE = $resolvedPackage
        dotnet test (Join-Path $repo "tests\Custodian.Tests\Custodian.Tests.csproj") `
            --configuration $Configuration `
            --no-restore `
            --filter "FullyQualifiedName=Custodian.Tests.UpdateSecurityTests.ConfiguredSignedReleasePackagePassesFullPublisherPolicy"
        if ($LASTEXITCODE -ne 0) {
            throw "Custodian's package publisher policy rejected '$resolvedPackage'."
        }
    }
    finally {
        $env:CUSTODIAN_TEST_SIGNED_RELEASE_PACKAGE = $priorPackage
    }
}
finally {
    if ($null -ne $temporaryPackage -and (Test-Path -LiteralPath $temporaryPackage -PathType Leaf)) {
        Remove-Item -LiteralPath $temporaryPackage -Force
    }
}
