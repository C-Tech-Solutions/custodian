param(
    [string]$PublishRoot = "artifacts\velopack-publish\Custodian",
    [string]$CatalogPath = "artifacts\velopack-publish\unsigned-pe-files.txt"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$publish = [IO.Path]::GetFullPath((Join-Path $repo $PublishRoot))
$catalog = [IO.Path]::GetFullPath((Join-Path $repo $CatalogPath))

if (!(Test-Path -LiteralPath $publish -PathType Container)) {
    throw "Prepared publish tree was not found at '$publish'."
}

$catalogDirectory = Split-Path -Parent $catalog
New-Item -ItemType Directory -Force -Path $catalogDirectory | Out-Null

$peFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File |
    Where-Object { $_.Extension -in @(".dll", ".exe") } |
    Sort-Object -Property FullName)
if ($peFiles.Count -eq 0) {
    throw "No PE files were found under '$publish'."
}

$unsignedPaths = [Collections.Generic.List[string]]::new()
$preservedSignedCount = 0
foreach ($file in $peFiles) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -eq [Management.Automation.SignatureStatus]::Valid) {
        $preservedSignedCount++
        continue
    }

    if ($signature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
        throw "Refusing to replace the invalid Authenticode signature on '$($file.FullName)': $($signature.StatusMessage)"
    }

    $relativePath = [IO.Path]::GetRelativePath($catalogDirectory, $file.FullName)
    $unsignedPaths.Add($relativePath)
}

[IO.File]::WriteAllLines($catalog, $unsignedPaths, [Text.UTF8Encoding]::new($false))

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "catalog_path=$catalog" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "unsigned_count=$($unsignedPaths.Count)" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "preserved_signed_count=$preservedSignedCount" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}

Write-Host "Signing catalog: $catalog"
Write-Host "Unsigned PEs selected for C-Tech signing: $($unsignedPaths.Count)"
Write-Host "Valid existing signatures preserved: $preservedSignedCount"
