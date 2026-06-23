using Custodian.Core.Model;

namespace Custodian.Core.Portable;

public sealed record PortableCopyResult(
    long FilesCopied,
    long FilesSkipped,
    long BytesCopied,
    IReadOnlyList<SkippedEntry> SkippedEntries);
