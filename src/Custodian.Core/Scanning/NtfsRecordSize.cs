namespace Custodian.Core.Scanning;

internal readonly record struct NtfsRecordSize(long LogicalSize, long AllocatedSize);
