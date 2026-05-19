namespace Custodian.Core.Presentation;

public sealed record ChartDataset(
    string Title,
    long TotalBytes,
    string TotalSize,
    IReadOnlyList<ChartSlice> Slices,
    bool HasOther);
