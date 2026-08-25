param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputRoot = "artifacts\velopack",
    [string]$WorkingRoot = "artifacts\velopack-portable-signing",
    [string]$CatalogPath = "artifacts\velopack-portable-signing\unsigned-pe-files.txt"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$portablePath = [IO.Path]::GetFullPath((Join-Path $repo (Join-Path $OutputRoot "Custodian.DiskAnalyzer-win-Portable.zip")))
$working = [IO.Path]::GetFullPath((Join-Path $repo $WorkingRoot))
$content = Join-Path $working "content"
$catalog = [IO.Path]::GetFullPath((Join-Path $repo $CatalogPath))

if (!(Test-Path -LiteralPath $portablePath -PathType Leaf)) {
    throw "Portable release was not found at '$portablePath'."
}
if (Test-Path -LiteralPath $working) {
    Remove-Item -LiteralPath $working -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $content | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($portablePath, $content)

$peFiles = @(Get-ChildItem -LiteralPath $content -Recurse -File |
    Where-Object { $_.Extension -in @(".dll", ".exe") } |
    Sort-Object -Property FullName)
if ($peFiles.Count -eq 0) {
    throw "Portable release contains no PE files."
}

$catalogDirectory = Split-Path -Parent $catalog
$unsignedPaths = [Collections.Generic.List[string]]::new()
$preservedSignedCount = 0
foreach ($file in $peFiles) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -eq [Management.Automation.SignatureStatus]::Valid) {
        $preservedSignedCount++
        continue
    }
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
        throw "Refusing to replace the invalid Authenticode signature on portable PE '$($file.FullName)': $($signature.StatusMessage)"
    }

    $portableRelativePath = [IO.Path]::GetRelativePath($content, $file.FullName)
    if ([IO.Path]::GetDirectoryName($portableRelativePath) -or $file.Extension -ne ".exe") {
        throw "Only Velopack-generated root executables may be unsigned after packing; found '$portableRelativePath'."
    }
    $unsignedPaths.Add([IO.Path]::GetRelativePath($catalogDirectory, $file.FullName))
}

[IO.File]::WriteAllLines($catalog, $unsignedPaths, [Text.UTF8Encoding]::new($false))
if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "catalog_path=$catalog" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "unsigned_count=$($unsignedPaths.Count)" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "preserved_signed_count=$preservedSignedCount" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}

Write-Host "Portable signing catalog: $catalog"
Write-Host "Unsigned Velopack root executables selected: $($unsignedPaths.Count)"
Write-Host "Valid portable PE signatures preserved: $preservedSignedCount"
