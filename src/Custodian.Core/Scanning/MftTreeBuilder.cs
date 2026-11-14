using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Custodian.Core.Analysis;
using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

internal static class MftTreeBuilder
{
    internal const string ParentNotEnumeratedReason = "Parent folder not found in the MFT enumeration";
    internal const string ReparsePointReason = "Skipped reparse point";

    private const ulong FileReferenceSegmentMask = 0x0000ffffffffffff;

    // MFT record 5 is the root directory on every NTFS volume. FSCTL_ENUM_USN_DATA can leave it out
    // of the enumeration, so a parent reference to it is recognised by its segment number.
    private const ulong RootDirectorySegment = 5;

    public static MftTreeBuildResult Build(
        IReadOnlyCollection<NtfsFileRecord> records,
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
        var requestedComparisonPath = ScanPathUtility.TrimForComparison(requestedRootPath);
        var rootComparisonPath = ScanPathUtility.TrimForComparison(normalizedVolumeRoot);
        var isVolumeRootRequest = string.Equals(requestedComparisonPath, rootComparisonPath, StringComparison.OrdinalIgnoreCase);
        var rootEntry = CreateRootEntry(normalizedVolumeRoot);
        var directoryNodes = new Dictionary<ulong, FileSystemEntry>();
        var skippedReparseDirectories = new Dictionary<ulong, NtfsFileRecord>();
        var statistics = new MftTreeBuildStatistics();

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!record.IsDirectory)
            {
                statistics.FileRecords++;
                continue;
            }

            statistics.FolderRecords++;
            if (record.IsRootRecord)
            {
                statistics.RootRecords++;
                directoryNodes[record.FileReferenceNumber] = rootEntry;
                rootEntry.LastWriteTime = record.LastWriteTime;
                continue;
            }

            if (!options.FollowReparsePoints && record.IsReparsePoint)
            {
                statistics.ReparseFoldersSkipped++;
                skippedReparseDirectories[record.FileReferenceNumber] = record;
                continue;
            }

            directoryNodes[record.FileReferenceNumber] = new FileSystemEntry
            {
                Name = record.FileName,
                IsDirectory = true,
                Attributes = record.FileAttributes.ToString(),
                LastWriteTime = record.LastWriteTime
            };
        }

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!record.IsDirectory || record.IsRootRecord || !directoryNodes.TryGetValue(record.FileReferenceNumber, out var directory))
            {
                continue;
            }

            if (TryResolveParent(record.ParentFileReferenceNumber, directoryNodes, rootEntry, out var parent))
            {
                statistics.FoldersAttached++;
                parent.Children.Add(directory);
            }
            else if (skippedReparseDirectories.ContainsKey(record.ParentFileReferenceNumber))
            {
                statistics.FoldersInSkippedReparseFolder++;
            }
            else
            {
                statistics.FoldersWithoutParent++;
                if (isVolumeRootRequest)
                {
                    // The folder is left out together with everything below it.
                    skipped.Add(new SkippedEntry(record.FileName, ParentNotEnumeratedReason));
                }
            }
        }

        AssignPaths(rootEntry, normalizedVolumeRoot);
        treeWatch.Stop();

        var scanRoot = isVolumeRootRequest
            ? rootEntry
            : FindDirectory(rootEntry, Path.GetRelativePath(normalizedVolumeRoot, requestedComparisonPath));

        if (scanRoot is null)
        {
            throw new DirectoryNotFoundException($"MFT records did not include the requested root: {requestedRootPath}");
        }

        // Folders that cannot be reached from the scan root are outside the request, or below a folder
        // that was left out: a skipped reparse point, or a folder whose parent was not enumerated.
        var selectedDirectories = CollectDirectoryReferences(scanRoot);

        // The volume root is not an attached folder; a requested subfolder is, and it is in the tree.
        statistics.FoldersInTree = selectedDirectories.Count - (isVolumeRootRequest ? 1 : 0);
        foreach (var record in skippedReparseDirectories.Values)
        {
            if (TryResolveParent(record.ParentFileReferenceNumber, directoryNodes, rootEntry, out var parent) &&
                selectedDirectories.Contains(parent))
            {
                statistics.ReparseFoldersListed++;
                skipped.Add(new SkippedEntry(Path.Combine(parent.FullPath, record.FileName), ReparsePointReason));
            }
        }

        var sizeWatch = Stopwatch.StartNew();
        var filesSeen = 0L;
        var bytesSeen = 0L;
        var indexBuilder = new ScanGlobalIndexBuilder();

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record.IsDirectory)
            {
                continue;
            }

            if (!TryResolveParent(record.ParentFileReferenceNumber, directoryNodes, rootEntry, out var parent))
            {
                if (skippedReparseDirectories.ContainsKey(record.ParentFileReferenceNumber))
                {
                    statistics.FilesInSkippedReparseFolder++;
                }
                else
                {
                    statistics.CountFileWithoutParent(record.ParentFileReferenceNumber & FileReferenceSegmentMask);
                    if (isVolumeRootRequest)
                    {
                        skipped.Add(new SkippedEntry(record.FileName, ParentNotEnumeratedReason));
                    }
                }

                continue;
            }

            if (!selectedDirectories.Contains(parent))
            {
                statistics.FilesNotReached++;
                continue;
            }

            var fullPath = Path.Combine(parent.FullPath, record.FileName);
            if (!options.FollowReparsePoints && record.IsReparsePoint)
            {
                statistics.ReparseFilesSkipped++;
                skipped.Add(new SkippedEntry(fullPath, ReparsePointReason));
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
                    Attributes = record.FileAttributes.ToString(),
                    LastWriteTime = record.LastWriteTime
                };
                parent.Children.Add(fileEntry);
                indexBuilder.Observe(fileEntry);
                bytesSeen += logicalSize;
                statistics.FilesCounted++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                statistics.FilesWithoutSize++;
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
            aggregateWatch.Elapsed,
            statistics);
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

    private static bool TryResolveParent(
        ulong parentReference,
        IReadOnlyDictionary<ulong, FileSystemEntry> directoryNodes,
        FileSystemEntry rootEntry,
        [NotNullWhen(true)] out FileSystemEntry? parent)
    {
        if (directoryNodes.TryGetValue(parentReference, out parent))
        {
            return true;
        }

        if ((parentReference & FileReferenceSegmentMask) == RootDirectorySegment)
        {
            parent = rootEntry;
            return true;
        }

        return false;
    }

    private static void AssignPaths(FileSystemEntry directory, string fullPath)
    {
        directory.FullPath = fullPath;

        foreach (var child in directory.Children)
        {
            if (child.IsDirectory)
            {
                AssignPaths(child, Path.Combine(directory.FullPath, child.Name));
            }
        }
    }

    private static FileSystemEntry? FindDirectory(FileSystemEntry root, string relativePath)
    {
        var directory = root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            directory = directory.Children.FirstOrDefault(child =>
                child.IsDirectory && string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (directory is null)
            {
                return null;
            }
        }

        return directory;
    }

    private static HashSet<FileSystemEntry> CollectDirectoryReferences(FileSystemEntry root)
    {
        var directories = new HashSet<FileSystemEntry>(ReferenceEqualityComparer.Instance) { root };
        var pending = new Stack<FileSystemEntry>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in directory.Children)
            {
                if (child.IsDirectory && directories.Add(child))
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
    TimeSpan AggregationDuration,
    MftTreeBuildStatistics Statistics);

/// <summary>
/// Where every record handed to <see cref="MftTreeBuilder"/> went. Each file and folder record lands in exactly one
/// bucket, so the two sources can be compared bucket by bucket.
/// </summary>
internal sealed class MftTreeBuildStatistics
{
    // NTFS's own files are records 0-15; the direct read leaves them out, except the root (5).
    private const ulong FirstUserSegment = 16;

    private readonly HashSet<ulong> _missingParents = [];

    public long FileRecords { get; set; }

    public long FolderRecords { get; set; }

    public long RootRecords { get; set; }

    public long ReparseFoldersSkipped { get; set; }

    /// <summary>Skipped reparse-point folders directly in a folder of the scan; these get a skipped entry.</summary>
    public long ReparseFoldersListed { get; set; }

    public long FoldersAttached { get; set; }

    /// <summary>Folders reachable from the scan root, counting the root itself only when it is a requested subfolder.</summary>
    public long FoldersInTree { get; set; }

    public long FoldersWithoutParent { get; set; }

    public long FoldersInSkippedReparseFolder { get; set; }

    public long FilesCounted { get; set; }

    public long FilesWithoutSize { get; set; }

    public long ReparseFilesSkipped { get; set; }

    public long FilesWithoutParent { get; private set; }

    /// <summary>Files without a parent whose parent reference points at one of NTFS's own records (0-15).</summary>
    public long FilesWithoutParentInNtfsRecords { get; private set; }

    public int MissingParentFolders => _missingParents.Count;

    public long FilesInSkippedReparseFolder { get; set; }

    /// <summary>Files in a folder the scan root does not reach: below a left-out folder, or outside a requested subfolder.</summary>
    public long FilesNotReached { get; set; }

    public void CountFileWithoutParent(ulong parentSegment)
    {
        FilesWithoutParent++;
        _missingParents.Add(parentSegment);
        if (parentSegment < FirstUserSegment)
        {
            FilesWithoutParentInNtfsRecords++;
        }
    }

    public IEnumerable<string> GetDiagnostics()
    {
        yield return
            $"MFT tree files: {FileRecords:n0} records; counted {FilesCounted:n0}; size lookup failed {FilesWithoutSize:n0}; " +
            $"reparse points skipped {ReparseFilesSkipped:n0}; parent not found {FilesWithoutParent:n0} " +
            $"({MissingParentFolders:n0} distinct parents, {FilesWithoutParentInNtfsRecords:n0} under NTFS records 0-15); " +
            $"left out silently: in a skipped reparse folder {FilesInSkippedReparseFolder:n0}, " +
            $"in a folder the scan root does not reach {FilesNotReached:n0}";
        yield return
            $"MFT tree folders: {FolderRecords:n0} records; root {RootRecords:n0}; in the tree {FoldersInTree:n0}; " +
            $"not reached from the scan root {FoldersAttached - FoldersInTree:n0}; " +
            $"reparse points skipped {ReparseFoldersSkipped:n0} ({ReparseFoldersListed:n0} listed); " +
            $"parent not found {FoldersWithoutParent:n0}; in a skipped reparse folder {FoldersInSkippedReparseFolder:n0}";
    }
}
