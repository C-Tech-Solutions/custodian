using System.Diagnostics;
using Custodian.Core.Analysis;
using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

internal static class MftTreeBuilder
{
    public static MftTreeBuildResult Build(
        IReadOnlyDictionary<ulong, NtfsFileRecord> records,
        string requestedRootPath,
        ScanOptions options,
        ProgressThrottle progress,
        List<SkippedEntry> skipped,
        CancellationToken cancellationToken,
        Func<string, NtfsFileRecord, (long LogicalSize, long AllocatedSize)>? sizeResolver = null)
    {
        var treeWatch = Stopwatch.StartNew();
        var volumeRoot = Path.GetPathRoot(requestedRootPath) ?? requestedRootPath;
        var normalizedVolumeRoot = ScanPathUtility.NormalizeRoot(volumeRoot);
        var rootEntry = CreateRootEntry(normalizedVolumeRoot);
        var directoryNodes = new Dictionary<ulong, FileSystemEntry>();
        var rootRecordIds = new HashSet<ulong>();
        var missingParentMeansRoot = true;

        foreach (var record in records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!record.IsDirectory)
            {
                continue;
            }

            if (record.IsRootRecord)
            {
                directoryNodes[record.FileReferenceNumber] = rootEntry;
                rootRecordIds.Add(record.FileReferenceNumber);
                missingParentMeansRoot = false;
                continue;
            }

            if (!options.FollowReparsePoints && record.IsReparsePoint)
            {
                continue;
            }

            directoryNodes[record.FileReferenceNumber] = new FileSystemEntry
            {
                Name = record.FileName,
                IsDirectory = true,
                Attributes = record.FileAttributes.ToString()
            };
        }

        foreach (var record in records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!record.IsDirectory || record.IsRootRecord || !directoryNodes.TryGetValue(record.FileReferenceNumber, out var directory))
            {
                continue;
            }

            var parent = ResolveParentDirectory(record.ParentFileReferenceNumber, directoryNodes, rootEntry, missingParentMeansRoot);
            if (parent is null)
            {
                continue;
            }

            parent.Children.Add(directory);
        }

        AssignPaths(rootEntry, normalizedVolumeRoot);
        treeWatch.Stop();

        var requestedComparisonPath = ScanPathUtility.TrimForComparison(requestedRootPath);
        var rootComparisonPath = ScanPathUtility.TrimForComparison(normalizedVolumeRoot);
        var isVolumeRootRequest = string.Equals(requestedComparisonPath, rootComparisonPath, StringComparison.OrdinalIgnoreCase);
        var scanRoot = isVolumeRootRequest
            ? rootEntry
            : FindDirectory(rootEntry, requestedComparisonPath);

        if (scanRoot is null)
        {
            throw new DirectoryNotFoundException($"MFT records did not include the requested root: {requestedRootPath}");
        }

        var selectedDirectories = isVolumeRootRequest ? null : CollectDirectoryReferences(scanRoot);
        var sizeWatch = Stopwatch.StartNew();
        var filesSeen = 0L;
        var bytesSeen = 0L;
        var indexBuilder = new ScanGlobalIndexBuilder();

        foreach (var record in records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record.IsDirectory)
            {
                continue;
            }

            var parent = ResolveParentDirectory(record.ParentFileReferenceNumber, directoryNodes, rootEntry, missingParentMeansRoot);
            if (parent is null)
            {
                continue;
            }

            if (selectedDirectories is not null && !selectedDirectories.Contains(parent))
            {
                continue;
            }

            var fullPath = Path.Combine(parent.FullPath, record.FileName);
            if (!options.FollowReparsePoints && record.IsReparsePoint)
            {
                skipped.Add(new SkippedEntry(fullPath, "Skipped reparse point"));
                continue;
            }

            filesSeen++;
            progress.Report(fullPath, filesSeen, directoryNodes.Count, bytesSeen, "Resolving file sizes");

            try
            {
                var (logicalSize, allocatedSize) = sizeResolver is null
                    ? ResolveSize(fullPath, record, options)
                    : sizeResolver(fullPath, record);

                var fileEntry = new FileSystemEntry
                {
                    Name = record.FileName,
                    FullPath = fullPath,
                    IsDirectory = false,
                    LogicalSizeBytes = logicalSize,
                    AllocatedSizeBytes = allocatedSize,
                    FileCount = 1,
                    Extension = Path.GetExtension(record.FileName).ToLowerInvariant(),
                    Attributes = record.FileAttributes.ToString()
                };
                parent.Children.Add(fileEntry);
                indexBuilder.Observe(fileEntry);
                bytesSeen += logicalSize;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                skipped.Add(new SkippedEntry(fullPath, ex.Message));
            }
        }

        sizeWatch.Stop();
        progress.Report(scanRoot.FullPath, filesSeen, directoryNodes.Count, bytesSeen, "Building folder totals", force: true);

        var aggregateWatch = Stopwatch.StartNew();
        Aggregate(scanRoot, indexBuilder);
        aggregateWatch.Stop();

        return new MftTreeBuildResult(
            scanRoot,
            indexBuilder.Build(scanRoot),
            filesSeen,
            bytesSeen,
            treeWatch.Elapsed,
            sizeWatch.Elapsed,
            aggregateWatch.Elapsed);
    }

    private static FileSystemEntry CreateRootEntry(string rootPath)
    {
        return new FileSystemEntry
        {
            Name = rootPath,
            FullPath = rootPath,
            IsDirectory = true,
            Attributes = FileAttributes.Directory.ToString()
        };
    }

    private static FileSystemEntry? ResolveParentDirectory(
        ulong parentReference,
        IReadOnlyDictionary<ulong, FileSystemEntry> directoryNodes,
        FileSystemEntry rootEntry,
        bool missingParentMeansRoot)
    {
        if (directoryNodes.TryGetValue(parentReference, out var parent))
        {
            return parent;
        }

        return missingParentMeansRoot ? rootEntry : null;
    }

    private static void AssignPaths(FileSystemEntry directory, string fullPath)
    {
        directory.FullPath = fullPath;

        foreach (var child in directory.Children.Where(c => c.IsDirectory))
        {
            AssignPaths(child, Path.Combine(directory.FullPath, child.Name));
        }
    }

    private static FileSystemEntry? FindDirectory(FileSystemEntry directory, string comparisonPath)
    {
        if (string.Equals(ScanPathUtility.TrimForComparison(directory.FullPath), comparisonPath, StringComparison.OrdinalIgnoreCase))
        {
            return directory;
        }

        foreach (var child in directory.Children.Where(c => c.IsDirectory))
        {
            var found = FindDirectory(child, comparisonPath);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static HashSet<FileSystemEntry> CollectDirectoryReferences(FileSystemEntry root)
    {
        var directories = new HashSet<FileSystemEntry> { root };
        var pending = new Stack<FileSystemEntry>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in directory.Children.Where(c => c.IsDirectory))
            {
                if (directories.Add(child))
                {
                    pending.Push(child);
                }
            }
        }

        return directories;
    }

    private static (long LogicalSize, long AllocatedSize) ResolveSize(string fullPath, NtfsFileRecord record, ScanOptions options)
    {
        var info = new FileInfo(fullPath);
        var length = info.Length;
        var allocated = options.CollectAllocatedSize ? FileSizeUtilities.GetAllocatedSize(fullPath, length) : length;
        return (length, allocated);
    }

    private static void Aggregate(FileSystemEntry entry, ScanGlobalIndexBuilder indexBuilder)
    {
        if (!entry.IsDirectory)
        {
            return;
        }

        entry.LogicalSizeBytes = 0;
        entry.AllocatedSizeBytes = 0;
        entry.FileCount = 0;
        entry.DirectoryCount = 0;

        foreach (var child in entry.Children)
        {
            Aggregate(child, indexBuilder);
            entry.LogicalSizeBytes += child.LogicalSizeBytes;
            entry.AllocatedSizeBytes += child.AllocatedSizeBytes;
            entry.FileCount += child.IsDirectory ? child.FileCount : 1;
            entry.DirectoryCount += child.IsDirectory ? child.DirectoryCount + 1 : 0;
        }

        indexBuilder.Observe(entry);
    }
}

internal sealed record MftTreeBuildResult(
    FileSystemEntry Root,
    ScanGlobalIndex GlobalIndex,
    long FilesSeen,
    long BytesSeen,
    TimeSpan TreeConstructionDuration,
    TimeSpan SizeResolutionDuration,
    TimeSpan AggregationDuration);
