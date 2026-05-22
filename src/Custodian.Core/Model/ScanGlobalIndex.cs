namespace Custodian.Core.Model;

public sealed record ScanGlobalIndex(
    int TopEntryCount,
    IReadOnlyList<FileSystemEntry> LargestFiles,
    IReadOnlyList<FileSystemEntry> LargestFolders,
    IReadOnlyList<ExtensionSummary> ExtensionSummaries,
    long TotalFileLogicalSizeBytes,
    long FileEntryCount,
    long TotalFolderLogicalSizeBytes,
    long FolderEntryCount)
{
    public const int DefaultTopEntryCount = 200;
}
