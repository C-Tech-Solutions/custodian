namespace Custodian.Core.Model;

public sealed record RecycleBinEntry(
    string Name,
    string OriginalLocation,
    DateTimeOffset? DateDeleted,
    long SizeBytes,
    string ItemType,
    string RecyclePath,
    bool IsFolder,
    string StableKey);
