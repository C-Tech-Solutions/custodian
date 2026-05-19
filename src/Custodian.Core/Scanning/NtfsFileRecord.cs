namespace Custodian.Core.Scanning;

internal sealed record NtfsFileRecord(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    FileAttributes FileAttributes,
    string FileName)
{
    public bool IsDirectory => FileAttributes.HasFlag(FileAttributes.Directory);
    public bool IsReparsePoint => FileAttributes.HasFlag(FileAttributes.ReparsePoint);
    public bool IsRootRecord => IsDirectory && (FileName is "." || ParentFileReferenceNumber == FileReferenceNumber);
}
