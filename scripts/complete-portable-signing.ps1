param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputRoot = "artifacts\velopack",
    [string]$WorkingRoot = "artifacts\velopack-portable-signing"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$portablePath = [IO.Path]::GetFullPath((Join-Path $repo (Join-Path $OutputRoot "Custodian.DiskAnalyzer-win-Portable.zip")))
$content = [IO.Path]::GetFullPath((Join-Path $repo (Join-Path $WorkingRoot "content")))

if (!(Test-Path -LiteralPath $portablePath -PathType Leaf)) {
    throw "Portable release was not found at '$portablePath'."
}
if (!(Test-Path -LiteralPath $content -PathType Container)) {
    throw "Extracted portable signing tree was not found at '$content'."
}

$peFiles = @(Get-ChildItem -LiteralPath $content -Recurse -File |
    Where-Object { $_.Extension -in @(".dll", ".exe") })
if ($peFiles.Count -eq 0) {
    throw "Extracted portable signing tree contains no PE files."
}
foreach ($file in $peFiles) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Portable PE '$($file.FullName)' failed Authenticode verification before repacking: $($signature.StatusMessage)"
    }
}

$rootExecutables = @($peFiles | Where-Object {
    $_.Extension -eq ".exe" -and
    -not [IO.Path]::GetDirectoryName([IO.Path]::GetRelativePath($content, $_.FullName))
})
if ($rootExecutables.Count -eq 0) {
    throw "Portable release contains no Velopack root executables."
}
foreach ($file in $rootExecutables) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=C-Tech Solutions LLC(?:,|$)') {
        throw "Portable root executable '$($file.Name)' is not signed by C-Tech Solutions LLC."
    }
}

$temporaryPath = "$portablePath.repacked"
try {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in @(Get-ChildItem -LiteralPath $content -Recurse -File | Sort-Object -Property FullName)) {
                $relativePath = [IO.Path]::GetRelativePath($content, $file.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry($relativePath, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new($file.LastWriteTimeUtc)
                $input = $file.OpenRead()
                $output = $entry.Open()
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    Move-Item -LiteralPath $temporaryPath -Destination $portablePath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Host "Repacked portable release with $($peFiles.Count) valid PE signatures: $portablePath"
