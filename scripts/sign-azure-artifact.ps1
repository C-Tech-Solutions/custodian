param(
    [Parameter(Mandatory = $true, ValueFromPipeline = $true, ValueFromPipelineByPropertyName = $true)]
    [Alias("FullName")]
    [string[]]$Path,
    [string]$MetadataPath,
    [string]$Endpoint,
    [string]$CodeSigningAccountName,
    [string]$CertificateProfileName,
    [string]$CorrelationId,
    [string[]]$ExcludeCredentials,
    [string]$SignToolPath,
    [string]$DlibPath,
    [string]$TimestampUrl,
    [switch]$PreserveValidSignature,
    [switch]$SkipVerify,
    [switch]$DebugSigning
)

$ErrorActionPreference = "Stop"

function Get-FirstConfiguredValue {
    param(
        [string]$ExplicitValue,
        [string[]]$EnvironmentNames
    )

    if (![string]::IsNullOrWhiteSpace($ExplicitValue)) {
        return $ExplicitValue
    }

    foreach ($name in $EnvironmentNames) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (![string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $null
}

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CandidatePath,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
        throw "$Description path was not provided."
    }

    if (!(Test-Path -LiteralPath $CandidatePath -PathType Leaf)) {
        throw "$Description was not found at '$CandidatePath'."
    }

    return (Resolve-Path -LiteralPath $CandidatePath).Path
}

function Find-SignTool {
    param([string]$ExplicitPath)

    $configuredPath = Get-FirstConfiguredValue `
        -ExplicitValue $ExplicitPath `
        -EnvironmentNames @("CUSTODIAN_SIGNTOOL_PATH", "SIGNTOOL_PATH")
    if (![string]::IsNullOrWhiteSpace($configuredPath)) {
        return Resolve-RequiredFile -CandidatePath $configuredPath -Description "SignTool"
    }

    $artifactToolsPath = Join-Path ${env:ProgramFiles(x86)} "Microsoft\ArtifactSigningClientTools\bin\signtool.exe"
    if (Test-Path -LiteralPath $artifactToolsPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $artifactToolsPath).Path
    }

    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $windowsKitBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $windowsKitBin -PathType Container) {
        $sdkTool = Get-ChildItem -Path $windowsKitBin -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object -Property @{
                Expression = {
                    try {
                        [version]$_.Directory.Parent.Name
                    }
                    catch {
                        [version]"0.0"
                    }
                }
                Descending = $true
            } |
            Select-Object -First 1

        if ($null -ne $sdkTool) {
            return $sdkTool.FullName
        }
    }

    $artifactSigningCache = Join-Path $env:LOCALAPPDATA "ArtifactSigning\Microsoft.Windows.SDK.BuildTools"
    if (Test-Path -LiteralPath $artifactSigningCache -PathType Container) {
        $cachedTool = Get-ChildItem -LiteralPath $artifactSigningCache -Recurse -Filter "signtool.exe" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($null -ne $cachedTool) {
            return $cachedTool.FullName
        }
    }

    throw "SignTool was not found. Install Microsoft.Azure.ArtifactSigningClientTools or Windows SDK Build Tools, or set CUSTODIAN_SIGNTOOL_PATH."
}

function Find-AzureSigningDlib {
    param([string]$ExplicitPath)

    $configuredPath = Get-FirstConfiguredValue `
        -ExplicitValue $ExplicitPath `
        -EnvironmentNames @("CUSTODIAN_AZURE_SIGNING_DLIB_PATH", "AZURE_CODE_SIGNING_DLIB_PATH")
    if (![string]::IsNullOrWhiteSpace($configuredPath)) {
        return Resolve-RequiredFile -CandidatePath $configuredPath -Description "Azure Artifact Signing dlib"
    }

    $candidatePaths = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll"),
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\ArtifactSigningClientTools\bin\x64\Azure.CodeSigning.Dlib.dll"),
        (Join-Path $env:ProgramFiles "Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll"),
        (Join-Path $env:ProgramFiles "Microsoft\ArtifactSigningClientTools\bin\x64\Azure.CodeSigning.Dlib.dll"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll"),
        (Join-Path $env:LOCALAPPDATA "ArtifactSigning\Microsoft.ArtifactSigning.Client")
    )

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidatePath).Path
        }
        if (Test-Path -LiteralPath $candidatePath -PathType Container) {
            $cachedDlib = Get-ChildItem -LiteralPath $candidatePath -Recurse -Filter "Azure.CodeSigning.Dlib.dll" -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\Azure\.CodeSigning\.Dlib\.dll$' } |
                Sort-Object -Property FullName -Descending |
                Select-Object -First 1
            if ($null -ne $cachedDlib) {
                return $cachedDlib.FullName
            }
        }
    }

    $searchRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\ArtifactSigningClientTools"),
        (Join-Path $env:ProgramFiles "Microsoft\ArtifactSigningClientTools"),
        (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.artifactsigning.client")
    )

    foreach ($searchRoot in $searchRoots) {
        if (!(Test-Path -LiteralPath $searchRoot -PathType Container)) {
            continue
        }

        $dlib = Get-ChildItem -Path $searchRoot -Recurse -Filter "Azure.CodeSigning.Dlib.dll" -ErrorAction SilentlyContinue |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($null -ne $dlib) {
            return $dlib.FullName
        }
    }

    throw "Azure.CodeSigning.Dlib.dll was not found. Install Microsoft.Azure.ArtifactSigningClientTools or set CUSTODIAN_AZURE_SIGNING_DLIB_PATH."
}

function New-SigningMetadataFile {
    param(
        [string]$MetadataPath,
        [string]$Endpoint,
        [string]$CodeSigningAccountName,
        [string]$CertificateProfileName,
        [string]$CorrelationId,
        [string[]]$ExcludeCredentials
    )

    $configuredMetadataPath = Get-FirstConfiguredValue `
        -ExplicitValue $MetadataPath `
        -EnvironmentNames @("CUSTODIAN_AZURE_SIGNING_METADATA", "AZURE_TRUSTED_SIGNING_METADATA", "AZURE_ARTIFACT_SIGNING_METADATA")
    if (![string]::IsNullOrWhiteSpace($configuredMetadataPath)) {
        return [pscustomobject]@{
            Path = Resolve-RequiredFile -CandidatePath $configuredMetadataPath -Description "Azure Artifact Signing metadata"
            Temporary = $false
        }
    }

    $resolvedEndpoint = Get-FirstConfiguredValue `
        -ExplicitValue $Endpoint `
        -EnvironmentNames @("CUSTODIAN_AZURE_SIGNING_ENDPOINT", "AZURE_TRUSTED_SIGNING_ENDPOINT")
    $resolvedAccountName = Get-FirstConfiguredValue `
        -ExplicitValue $CodeSigningAccountName `
        -EnvironmentNames @("CUSTODIAN_AZURE_SIGNING_ACCOUNT", "AZURE_CODE_SIGNING_NAME", "AZURE_TRUSTED_SIGNING_ACCOUNT_NAME")
    $resolvedProfileName = Get-FirstConfiguredValue `
        -ExplicitValue $CertificateProfileName `
        -EnvironmentNames @("CUSTODIAN_AZURE_SIGNING_PROFILE", "AZURE_CERT_PROFILE_NAME", "AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME")
    $resolvedCorrelationId = Get-FirstConfiguredValue `
        -ExplicitValue $CorrelationId `
        -EnvironmentNames @("CUSTODIAN_AZURE_SIGNING_CORRELATION_ID")
    $resolvedExcludeCredentials = @()
    if ($null -ne $ExcludeCredentials -and $ExcludeCredentials.Count -gt 0) {
        $resolvedExcludeCredentials = $ExcludeCredentials
    }
    else {
        $configuredExcludeCredentials = [Environment]::GetEnvironmentVariable("CUSTODIAN_AZURE_SIGNING_EXCLUDE_CREDENTIALS")
        if (![string]::IsNullOrWhiteSpace($configuredExcludeCredentials)) {
            $resolvedExcludeCredentials = @($configuredExcludeCredentials -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
    }

    $missing = @()
    if ([string]::IsNullOrWhiteSpace($resolvedEndpoint)) { $missing += "CUSTODIAN_AZURE_SIGNING_ENDPOINT" }
    if ([string]::IsNullOrWhiteSpace($resolvedAccountName)) { $missing += "CUSTODIAN_AZURE_SIGNING_ACCOUNT" }
    if ([string]::IsNullOrWhiteSpace($resolvedProfileName)) { $missing += "CUSTODIAN_AZURE_SIGNING_PROFILE" }
    if ($missing.Count -gt 0) {
        throw "Azure Artifact Signing metadata is incomplete. Set CUSTODIAN_AZURE_SIGNING_METADATA or set: $($missing -join ', ')."
    }

    $metadata = [ordered]@{
        Endpoint = $resolvedEndpoint
        CodeSigningAccountName = $resolvedAccountName
        CertificateProfileName = $resolvedProfileName
    }

    if (![string]::IsNullOrWhiteSpace($resolvedCorrelationId)) {
        $metadata.CorrelationId = $resolvedCorrelationId
    }

    if ($resolvedExcludeCredentials.Count -gt 0) {
        $metadata.ExcludeCredentials = @($resolvedExcludeCredentials)
    }

    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("custodian-azure-signing-{0}.json" -f [Guid]::NewGuid())
    $metadataJson = $metadata | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText($tempPath, $metadataJson, [System.Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        Path = $tempPath
        Temporary = $true
    }
}

$resolvedPaths = @()
foreach ($inputPath in $Path) {
    $resolvedPath = (Resolve-Path -LiteralPath $inputPath).Path
    if ($PreserveValidSignature) {
        $existingSignature = Get-AuthenticodeSignature -LiteralPath $resolvedPath
        if ($existingSignature.Status -eq [Management.Automation.SignatureStatus]::Valid) {
            Write-Host "Preserving valid Authenticode signature on $resolvedPath"
            continue
        }
        if ($existingSignature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
            throw "Refusing to replace the invalid Authenticode signature on '$resolvedPath': $($existingSignature.StatusMessage)"
        }
    }
    $resolvedPaths += $resolvedPath
}

if ($resolvedPaths.Count -eq 0) {
    return
}

$resolvedTimestampUrl = Get-FirstConfiguredValue `
    -ExplicitValue $TimestampUrl `
    -EnvironmentNames @("CUSTODIAN_SIGNING_TIMESTAMP_URL")
if ([string]::IsNullOrWhiteSpace($resolvedTimestampUrl)) {
    $resolvedTimestampUrl = "http://timestamp.acs.microsoft.com/"
}

$resolvedSignToolPath = Find-SignTool -ExplicitPath $SignToolPath
$resolvedDlibPath = Find-AzureSigningDlib -ExplicitPath $DlibPath
$metadataFile = New-SigningMetadataFile `
    -MetadataPath $MetadataPath `
    -Endpoint $Endpoint `
    -CodeSigningAccountName $CodeSigningAccountName `
    -CertificateProfileName $CertificateProfileName `
    -CorrelationId $CorrelationId `
    -ExcludeCredentials $ExcludeCredentials

try {

    foreach ($resolvedPath in $resolvedPaths) {
        Write-Host "Signing $resolvedPath"

        $signArgs = @(
            "sign",
            "/v",
            "/fd", "SHA256",
            "/tr", $resolvedTimestampUrl,
            "/td", "SHA256",
            "/dlib", $resolvedDlibPath,
            "/dmdf", $metadataFile.Path,
            $resolvedPath
        )

        if ($DebugSigning) {
            $signArgs = @("sign", "/debug") + $signArgs[1..($signArgs.Count - 1)]
        }

        & $resolvedSignToolPath @signArgs
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool failed with exit code $LASTEXITCODE while signing '$resolvedPath'."
        }

        if (!$SkipVerify) {
            & $resolvedSignToolPath verify /pa /v $resolvedPath
            if ($LASTEXITCODE -ne 0) {
                throw "SignTool verification failed with exit code $LASTEXITCODE for '$resolvedPath'."
            }
        }
    }
}
finally {
    if ($metadataFile.Temporary -and (Test-Path -LiteralPath $metadataFile.Path -PathType Leaf)) {
        Remove-Item -LiteralPath $metadataFile.Path -Force
    }
}
