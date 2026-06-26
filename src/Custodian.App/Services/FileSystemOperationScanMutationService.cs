using System.IO;
using Custodian.Core.Model;
using Custodian.Core.Scanning;
using Custodian.Platform.Windows.Services;

namespace Custodian.App.Services;

internal static class FileSystemOperationScanMutationService
{
    public static IReadOnlyList<FileSystemEntry> RemovedEntriesFor(
        FileSystemOperationKind operationKind,
        FileSystemOperationBatchResult result,
        IReadOnlyCollection<FileSystemEntry> sourceEntries,
        ScanResult? currentScan,
        string? destinationFolder)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sourceEntries);

        if (currentScan?.Root is null ||
            string.IsNullOrWhiteSpace(currentScan.Root.FullPath) ||
            sourceEntries.Count == 0 ||
            !IsCleanCompletedBatch(result))
        {
            return [];
        }

        if (operationKind == FileSystemOperationKind.Copy ||
            (operationKind == FileSystemOperationKind.Move &&
             !IsDestinationOutsideScanRoot(destinationFolder, currentScan.Root.FullPath)))
        {
            return [];
        }

        if (operationKind is not (FileSystemOperationKind.Recycle or
            FileSystemOperationKind.PermanentDelete or
            FileSystemOperationKind.Move))
        {
            return [];
        }

        return sourceEntries
            .Where(entry => entry is not null)
            .Distinct()
            .ToArray();
    }

    private static bool IsCleanCompletedBatch(FileSystemOperationBatchResult result)
        => !result.HasIssues &&
            result.RequestedCount > 0 &&
            result.CompletedCount == result.RequestedCount;

    private static bool IsDestinationOutsideScanRoot(string? destinationFolder, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            var normalizedDestination = ScanPathUtility.NormalizeRoot(destinationFolder);
            var normalizedRoot = ScanPathUtility.NormalizeRoot(rootPath);
            return !IsPathWithinRoot(normalizedDestination, normalizedRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsPathWithinRoot(string normalizedPath, string normalizedRoot)
    {
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Length > normalizedRoot.Length &&
            normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            (normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
             normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar);
    }
}
