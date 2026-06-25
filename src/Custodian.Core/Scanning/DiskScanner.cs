using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

public sealed class DiskScanner
{
    private readonly RecursiveScanProvider _recursive = new();
    private readonly MftScanProvider _mft = new();

    public async Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedOptions = options with { RootPath = ScanPathUtility.NormalizeRoot(options.RootPath) };
        ValidateRoot(normalizedOptions.RootPath);

        if (normalizedOptions.CloudProvider is not null)
        {
            return await _recursive.ScanAsync(normalizedOptions with { Mode = ScanMode.Recursive }, progress, cancellationToken).ConfigureAwait(false);
        }

        if (normalizedOptions.Mode is ScanMode.Mft)
        {
            return await _mft.ScanAsync(normalizedOptions, progress, cancellationToken).ConfigureAwait(false);
        }

        if (normalizedOptions.Mode is ScanMode.Auto &&
            ScanPathUtility.IsVolumeRoot(normalizedOptions.RootPath) &&
            _mft.CanScan(normalizedOptions, out _))
        {
            try
            {
                return await _mft.ScanAsync(normalizedOptions, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var fallback = await _recursive.ScanAsync(normalizedOptions with { Mode = ScanMode.Recursive }, progress, cancellationToken).ConfigureAwait(false);
                fallback.SkippedEntries.Insert(0, new SkippedEntry(normalizedOptions.RootPath, $"MFT scan unavailable; used recursive fallback. {ex.Message}"));
                return fallback;
            }
        }

        return await _recursive.ScanAsync(normalizedOptions with { Mode = ScanMode.Recursive }, progress, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A scan path is required.", nameof(rootPath));
        }

        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Scan path does not exist: {rootPath}");
        }
    }
}
