using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

/// <summary>
/// File sizes for a scan built from a direct <c>$MFT</c> read: the size parsed from the file's record, or a
/// lookup by path when the read found no unnamed <c>$DATA</c> for it.
/// </summary>
internal sealed class MftRecordSizeSource
{
    private readonly ScanOptions _options;

    public MftRecordSizeSource(ScanOptions options)
    {
        _options = options;
    }

    public long FromRecords { get; private set; }

    public long PathLookups { get; private set; }

    public (long LogicalSize, long AllocatedSize) Resolve(string fullPath, NtfsFileRecord record)
    {
        if (record.Size is { } size)
        {
            FromRecords++;
            return (size.LogicalSize, _options.CollectAllocatedSize ? size.AllocatedSize : size.LogicalSize);
        }

        PathLookups++;
        return NtfsRecordSizeResolver.ResolveFromPath(fullPath, _options);
    }

    public string GetDiagnostic() => $"File sizes from $MFT records {FromRecords:n0}; path lookups {PathLookups:n0}";
}
