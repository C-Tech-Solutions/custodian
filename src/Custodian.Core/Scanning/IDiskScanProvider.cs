using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

public interface IDiskScanProvider
{
    string Name { get; }

    bool CanScan(ScanOptions options, out string reason);

    Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}
