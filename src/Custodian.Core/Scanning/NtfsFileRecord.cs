namespace Custodian.Core.Scanning;

internal sealed record NtfsFileRecord(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    FileAttributes FileAttributes,
    string FileName)
{
    /// <summary>The unnamed <c>$DATA</c> size, when the record came from a direct <c>$MFT</c> read that found it.</summary>
    public NtfsRecordSize? Size { get; init; }

    /// <summary>The <c>$STANDARD_INFORMATION</c> last-write time, when the record came from a direct <c>$MFT</c> read.</summary>
    public DateTimeOffset? LastWriteTime { get; init; }

    public bool IsDirectory => FileAttributes.HasFlag(FileAttributes.Directory);
    public bool IsReparsePoint => FileAttributes.HasFlag(FileAttributes.ReparsePoint);
    public bool IsRootRecord => IsDirectory && (FileName is "." || ParentFileReferenceNumber == FileReferenceNumber);
}
