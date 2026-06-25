using System.Diagnostics;
using Custodian.Core.Analysis;
using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

public sealed class RecursiveScanProvider : IDiskScanProvider
{
    private const FileAttributes FileAttributeRecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes FileAttributeRecallOnDataAccess = (FileAttributes)0x00400000;

    private readonly IRecursiveScanFileSystem _fileSystem;

    public RecursiveScanProvider()
        : this(RecursiveScanFileSystem.Instance)
    {
    }

    internal RecursiveScanProvider(IRecursiveScanFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public string Name => "Recursive";

    public bool CanScan(ScanOptions options, out string reason)
    {
        if (_fileSystem.DirectoryExists(options.RootPath))
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Scan path does not exist: {options.RootPath}";
        return false;
    }

    public async Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanScan(options, out var reason))
        {
            throw new DirectoryNotFoundException(reason);
        }

        return await Task.Run(() => Scan(options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private ScanResult Scan(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var scanWatch = Stopwatch.StartNew();
        var skipped = new List<SkippedEntry>();
        var counters = new ScanCounters();
        var progressThrottle = new ProgressThrottle(progress);
        var indexBuilder = new ScanGlobalIndexBuilder();
        var root = ScanDirectory(new DirectoryInfo(options.RootPath), options, skipped, counters, progressThrottle, indexBuilder, cancellationToken);
        var globalIndex = indexBuilder.Build(root);
        scanWatch.Stop();

        return new ScanResult
        {
            RootPath = root.FullPath,
            SourceKind = ScanSourceKind.FileSystem,
            SourceId = root.FullPath,
            DisplayRootPath = root.FullPath,
            CloudProvider = options.CloudProvider,
            Engine = Name,
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow,
            Root = root,
            GlobalIndex = globalIndex,
            SkippedEntries = skipped,
            PhaseTimings =
            [
                new ScanPhaseTiming("Recursive enumeration", scanWatch.Elapsed)
            ]
        };
    }

    private FileSystemEntry ScanDirectory(
        DirectoryInfo directory,
        ScanOptions options,
        List<SkippedEntry> skipped,
        ScanCounters counters,
        ProgressThrottle progress,
        ScanGlobalIndexBuilder indexBuilder,
        CancellationToken cancellationToken,
        FileAttributes? knownAttributes = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directoryAttributes = knownAttributes ?? GetAttributesOrDefault(directory, skipped, FileAttributes.Directory);

        var entry = new FileSystemEntry
        {
            Name = directory.Name.Length == 0 ? directory.FullName : directory.Name,
            FullPath = directory.FullName,
            IsDirectory = true,
            Attributes = directoryAttributes.ToString(),
            LastWriteTime = SafeLastWriteTime(directory)
        };

        counters.Directories++;
        progress.Report(directory.FullName, counters.Files, counters.Directories, counters.Bytes, "Scanning folder");

        try
        {
            foreach (var childDirectory in _fileSystem.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetAttributes(childDirectory, skipped, out var childAttributes))
                {
                    continue;
                }

                if (!options.FollowReparsePoints && childAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped.Add(new SkippedEntry(childDirectory.FullName, "Skipped reparse point"));
                    continue;
                }

                var childEntry = ScanDirectory(childDirectory, options, skipped, counters, progress, indexBuilder, cancellationToken, childAttributes);
                entry.Children.Add(childEntry);
                entry.LogicalSizeBytes += childEntry.LogicalSizeBytes;
                entry.AllocatedSizeBytes += childEntry.AllocatedSizeBytes;
                entry.FileCount += childEntry.FileCount;
                entry.DirectoryCount += childEntry.DirectoryCount + 1;
            }
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            skipped.Add(new SkippedEntry(directory.FullName, ex.Message));
        }

        try
        {
            foreach (var file in _fileSystem.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileEntry = ScanFile(file, options, skipped);
                if (fileEntry is null)
                {
                    continue;
                }

                entry.Children.Add(fileEntry);
                indexBuilder.Observe(fileEntry);
                entry.LogicalSizeBytes += fileEntry.LogicalSizeBytes;
                entry.AllocatedSizeBytes += fileEntry.AllocatedSizeBytes;
                entry.FileCount++;
                counters.Files++;
                counters.Bytes += fileEntry.LogicalSizeBytes;
                progress.Report(file.FullName, counters.Files, counters.Directories, counters.Bytes, "Scanning files");
            }
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            skipped.Add(new SkippedEntry(directory.FullName, ex.Message));
        }

        indexBuilder.Observe(entry);
        return entry;
    }

    private FileSystemEntry? ScanFile(FileInfo file, ScanOptions options, List<SkippedEntry> skipped)
    {
        try
        {
            if (!TryGetAttributes(file, skipped, out var attributes))
            {
                return null;
            }

            var length = _fileSystem.GetLength(file);
            var allocatedSize = length;
            if (options.CollectAllocatedSize && !ShouldAvoidAllocatedSizeProbe(attributes))
            {
                try
                {
                    allocatedSize = _fileSystem.GetAllocatedSize(file.FullName, length);
                }
                catch (Exception ex) when (IsRecoverableMetadataException(ex))
                {
                    skipped.Add(new SkippedEntry(file.FullName, $"Allocated size unavailable; used logical size. {ex.Message}"));
                }
            }

            return new FileSystemEntry
            {
                Name = file.Name,
                FullPath = file.FullName,
                IsDirectory = false,
                LogicalSizeBytes = length,
                AllocatedSizeBytes = allocatedSize,
                FileCount = 1,
                Extension = file.Extension.ToLowerInvariant(),
                Attributes = attributes.ToString(),
                LastWriteTime = SafeLastWriteTime(file)
            };
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            skipped.Add(new SkippedEntry(file.FullName, ex.Message));
            return null;
        }
    }

    private bool TryGetAttributes(FileSystemInfo info, List<SkippedEntry> skipped, out FileAttributes attributes)
    {
        try
        {
            attributes = _fileSystem.GetAttributes(info);
            return true;
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            attributes = default;
            skipped.Add(new SkippedEntry(info.FullName, ex.Message));
            return false;
        }
    }

    private FileAttributes GetAttributesOrDefault(FileSystemInfo info, List<SkippedEntry> skipped, FileAttributes fallback)
    {
        return TryGetAttributes(info, skipped, out var attributes)
            ? attributes
            : fallback;
    }

    private DateTimeOffset? SafeLastWriteTime(FileSystemInfo info)
    {
        try
        {
            return _fileSystem.GetLastWriteTimeUtc(info);
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            return null;
        }
    }

    private static bool ShouldAvoidAllocatedSizeProbe(FileAttributes attributes)
    {
        return attributes.HasFlag(FileAttributes.Offline) ||
            attributes.HasFlag(FileAttributeRecallOnOpen) ||
            attributes.HasFlag(FileAttributeRecallOnDataAccess);
    }

    private static bool IsRecoverableMetadataException(Exception ex)
        => ex is UnauthorizedAccessException or IOException or System.Security.SecurityException;

    private sealed class ScanCounters
    {
        public long Files { get; set; }
        public long Directories { get; set; }
        public long Bytes { get; set; }
    }
}

internal interface IRecursiveScanFileSystem
{
    bool DirectoryExists(string path);

    IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory);

    IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory);

    FileAttributes GetAttributes(FileSystemInfo info);

    DateTimeOffset GetLastWriteTimeUtc(FileSystemInfo info);

    long GetLength(FileInfo file);

    long GetAllocatedSize(string path, long fallbackLength);
}

internal sealed class RecursiveScanFileSystem : IRecursiveScanFileSystem
{
    public static RecursiveScanFileSystem Instance { get; } = new();

    private RecursiveScanFileSystem()
    {
    }

    public bool DirectoryExists(string path)
        => Directory.Exists(path);

    public IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory)
        => directory.EnumerateDirectories();

    public IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory)
        => directory.EnumerateFiles();

    public FileAttributes GetAttributes(FileSystemInfo info)
        => info.Attributes;

    public DateTimeOffset GetLastWriteTimeUtc(FileSystemInfo info)
        => info.LastWriteTimeUtc;

    public long GetLength(FileInfo file)
        => file.Length;

    public long GetAllocatedSize(string path, long fallbackLength)
        => FileSizeUtilities.GetAllocatedSize(path, fallbackLength);
}
