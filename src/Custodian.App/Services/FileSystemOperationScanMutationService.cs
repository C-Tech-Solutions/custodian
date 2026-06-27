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
        string? destinationFolder,
        Func<string, SourcePathProbeResult>? pathProbe = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sourceEntries);

        if (currentScan?.Root is null ||
            string.IsNullOrWhiteSpace(currentScan.Root.FullPath) ||
            sourceEntries.Count == 0)
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

        var removableEntries = sourceEntries
            .Where(entry => entry is not null)
            .Distinct()
            .ToArray();
        if (!ShouldRemoveSources(operationKind, result, removableEntries, pathProbe ?? ProbeSourcePath))
        {
            return [];
        }

        return removableEntries;
    }

    private static bool IsCleanCompletedBatch(FileSystemOperationBatchResult result)
        => !result.HasIssues &&
            result.RequestedCount > 0 &&
            result.CompletedCount == result.RequestedCount;

    private static bool ShouldRemoveSources(
        FileSystemOperationKind operationKind,
        FileSystemOperationBatchResult result,
        IReadOnlyCollection<FileSystemEntry> sourceEntries,
        Func<string, SourcePathProbeResult> pathProbe)
    {
        if (IsCleanCompletedBatch(result))
        {
            return true;
        }

        return operationKind == FileSystemOperationKind.Recycle &&
            IsRecycleBatchCompletedBySourceDisappearance(result, sourceEntries, pathProbe);
    }

    private static bool IsRecycleBatchCompletedBySourceDisappearance(
        FileSystemOperationBatchResult result,
        IReadOnlyCollection<FileSystemEntry> sourceEntries,
        Func<string, SourcePathProbeResult> pathProbe)
    {
        if (result.CancelledCount > 0 ||
            result.Failures.Count > 0 ||
            result.RequestedCount <= 0 ||
            result.IndeterminateCount <= 0 ||
            result.CompletedCount + result.IndeterminateCount != result.RequestedCount)
        {
            return false;
        }

        return sourceEntries.All(entry =>
            !string.IsNullOrWhiteSpace(entry.FullPath) &&
            pathProbe(entry.FullPath) == SourcePathProbeResult.Missing);
    }

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

    private static SourcePathProbeResult ProbeSourcePath(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return SourcePathProbeResult.Exists;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SourcePathProbeResult.Missing;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return SourcePathProbeResult.Unknown;
        }
    }
}

internal enum SourcePathProbeResult
{
    Exists,
    Missing,
    Unknown
}
