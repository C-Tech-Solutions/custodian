namespace Custodian.Core.Storage;

/// <summary>
/// Resource budgets for reading a saved scan. Exceeding a budget rejects the entire
/// file; entries are never truncated. Callers may explicitly opt into larger budgets.
/// </summary>
public sealed record ScanLoadLimits
{
    public long MaxFileBytes { get; init; } = 1L << 30;
    public int MaxEntries { get; init; } = 2_000_000;
    public int MaxSkippedEntries { get; init; } = 100_000;
    public int MaxMetadataEntries { get; init; } = 128;
    public int MaxStringBytes { get; init; } = 131_072;
    public long MaxTotalStringBytes { get; init; } = 256L << 20;
    /// <summary>The root has depth one.</summary>
    public int MaxDepth { get; init; } = 1_024;

    internal void Validate()
    {
        if (MaxFileBytes <= 0 || MaxEntries <= 0 || MaxSkippedEntries <= 0 ||
            MaxMetadataEntries <= 0 || MaxStringBytes <= 0 || MaxTotalStringBytes <= 0 || MaxDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ScanLoadLimits), "Scan load limits must be positive.");
        }
    }
}
