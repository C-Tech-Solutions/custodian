namespace Custodian.Core.Model;

public sealed record ExtensionSummary(
    string Extension,
    long FileCount,
    long LogicalSizeBytes,
    long AllocatedSizeBytes);
