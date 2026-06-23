namespace Custodian.Core.Portable;

public sealed record PortableCopyProgress(
    string CurrentPath,
    long FilesCopied,
    long FilesSkipped,
    long BytesCopied,
    string Message);
