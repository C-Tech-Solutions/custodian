using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Custodian.Core.Analysis;
using Custodian.Core.Model;
using Microsoft.Win32.SafeHandles;
using IOFileSystemEntry = System.IO.Enumeration.FileSystemEntry;

namespace Custodian.Core.Scanning;

/// <summary>
/// Scans a folder tree by listing directories. Each directory is listed once, and
/// directories are spread over several worker threads; a single-threaded pass then
/// computes folder totals and the global index.
/// </summary>
public sealed class RecursiveScanProvider : IDiskScanProvider
{
    internal const int DefaultMaxDegreeOfParallelism = 8;
    internal const int MaxAllowedDegreeOfParallelism = ScanOptions.MaxDegreeOfParallelismLimit;

    private const FileAttributes FileAttributeRecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes FileAttributeRecallOnDataAccess = (FileAttributes)0x00400000;

    // Entries of one directory between progress updates, so a huge directory or a slow share
    // keeps reporting while it is listed. Reports are still throttled to one per 250 ms.
    private const int ListingProgressInterval = 256;

    // How often the totals pass checks for cancellation.
    private const int AggregationCancellationInterval = 65536;

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

    /// <summary>
    /// Number of directories listed at the same time. Cloud-provider scans use one thread
    /// so that they touch placeholders exactly as the single-threaded engine did.
    /// </summary>
    internal static int ResolveDegreeOfParallelism(ScanOptions options)
    {
        if (options.CloudProvider is not null)
        {
            return 1;
        }

        if (options.MaxDegreeOfParallelism is { } requested)
        {
            return Math.Clamp(requested, 1, MaxAllowedDegreeOfParallelism);
        }

        return Math.Clamp(Environment.ProcessorCount, 1, DefaultMaxDegreeOfParallelism);
    }

    private ScanResult Scan(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var phaseWatch = Stopwatch.StartNew();
        var skipped = new List<SkippedEntry>();
        var root = CreateRootEntry(new DirectoryInfo(options.RootPath), skipped);
        var degreeOfParallelism = ResolveDegreeOfParallelism(options);

        var walk = new DirectoryWalk(this, options, new ProgressThrottle(progress), degreeOfParallelism);
        walk.Run(root, skipped, cancellationToken);
        var enumerationTime = phaseWatch.Elapsed;

        phaseWatch.Restart();
        var globalIndex = AggregateFolders(root, cancellationToken);
        var aggregationTime = phaseWatch.Elapsed;

        // Workers finish in any order; sort so a scan of an unchanged tree always reports the same list.
        skipped.Sort(CompareSkippedEntries);

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
                new ScanPhaseTiming("Recursive enumeration", enumerationTime),
                new ScanPhaseTiming("Folder aggregation", aggregationTime)
            ],
            Diagnostics =
            [
                $"Recursive scan threads: {degreeOfParallelism}"
            ]
        };
    }

    private static FileSystemEntry CreateRootEntry(DirectoryInfo directory, List<SkippedEntry> skipped)
    {
        FileAttributes attributes;
        try
        {
            attributes = directory.Attributes;
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            attributes = FileAttributes.Directory;
            skipped.Add(new SkippedEntry(directory.FullName, ex.Message));
        }

        DateTimeOffset? lastWriteTime;
        try
        {
            lastWriteTime = directory.LastWriteTimeUtc;
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            lastWriteTime = null;
        }

        return new FileSystemEntry
        {
            Name = directory.Name.Length == 0 ? directory.FullName : directory.Name,
            FullPath = directory.FullName,
            IsDirectory = true,
            Attributes = FileAttributesText.Get(attributes),
            LastWriteTime = lastWriteTime
        };
    }

    /// <summary>
    /// Lists one directory and adds its children: folders first, then files, each in listing order.
    /// Subdirectories to descend into are left in <paramref name="worker"/>.<see cref="WorkerState.Subdirectories"/>.
    /// </summary>
    private void ListDirectory(
        FileSystemEntry directory,
        ScanOptions options,
        WorkerState worker,
        DirectoryWalk walk,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var folders = worker.Subdirectories;
        var files = worker.Files;
        folders.Clear();
        files.Clear();
        var directoryPath = directory.FullPath;
        long fileBytes = 0;
        var reportedFiles = 0;
        long reportedBytes = 0;

        try
        {
            var seen = 0;
            foreach (var item in _fileSystem.EnumerateEntries(directoryPath))
            {
                // Checked per entry: an allocated-size probe on a slow share can take a while.
                cancellationToken.ThrowIfCancellationRequested();
                if (++seen % ListingProgressInterval == 0)
                {
                    walk.ReportListingProgress(directoryPath, files.Count - reportedFiles, fileBytes - reportedBytes);
                    reportedFiles = files.Count;
                    reportedBytes = fileBytes;
                }

                var fullPath = Path.Join(directoryPath, item.Name);
                if ((item.Attributes & FileAttributes.Directory) != 0)
                {
                    if (ShouldSkipDirectory(fullPath, options, item.Attributes, out var skipReason))
                    {
                        worker.Skipped.Add(new SkippedEntry(fullPath, skipReason));
                        continue;
                    }

                    folders.Add(new FileSystemEntry
                    {
                        Name = item.Name,
                        FullPath = fullPath,
                        IsDirectory = true,
                        Attributes = FileAttributesText.Get(item.Attributes),
                        LastWriteTime = item.LastWriteTimeUtc
                    });
                }
                else
                {
                    var file = CreateFileEntry(item, fullPath, options, worker.Skipped);
                    files.Add(file);
                    fileBytes += file.LogicalSizeBytes;
                }
            }
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            // Entries returned before the failure are kept; the directory is reported once.
            worker.Skipped.Add(new SkippedEntry(directoryPath, ex.Message));
        }

        var children = directory.Children;
        var childCount = folders.Count + files.Count;
        if (children.Count == 0 && childCount > 0)
        {
            children.Capacity = childCount;
        }

        children.AddRange(folders);
        children.AddRange(files);
        // Only what the progress updates during the listing have not already added.
        worker.FileCount = files.Count - reportedFiles;
        worker.FileBytes = fileBytes - reportedBytes;
        files.Clear();
    }

    private FileSystemEntry CreateFileEntry(ScanItem item, string fullPath, ScanOptions options, List<SkippedEntry> skipped)
    {
        var length = item.Length;
        var allocatedSize = length;
        if (options.CollectAllocatedSize && !ShouldAvoidAllocatedSizeProbe(item.Attributes))
        {
            try
            {
                allocatedSize = _fileSystem.GetAllocatedSize(fullPath, length);
            }
            catch (Exception ex) when (IsRecoverableMetadataException(ex))
            {
                skipped.Add(new SkippedEntry(fullPath, $"Allocated size unavailable; used logical size. {ex.Message}"));
            }
        }

        return new FileSystemEntry
        {
            Name = item.Name,
            FullPath = fullPath,
            IsDirectory = false,
            LogicalSizeBytes = length,
            AllocatedSizeBytes = allocatedSize,
            FileCount = 1,
            Extension = GetExtension(item.Name).ToLowerInvariant(),
            Attributes = FileAttributesText.Get(item.Attributes),
            LastWriteTime = item.LastWriteTimeUtc
        };
    }

    /// <summary>
    /// Same result as <see cref="FileSystemInfo.Extension"/>, which the engine used before:
    /// unlike <see cref="Path.GetExtension(string)"/>, a name ending in a dot keeps that dot.
    /// </summary>
    internal static string GetExtension(string name)
    {
        for (var i = name.Length - 1; i >= 0; i--)
        {
            var ch = name[i];
            if (ch == '.')
            {
                return name[i..];
            }

            if (ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar || ch == Path.VolumeSeparatorChar)
            {
                break;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Single-threaded post-order pass: adds file sizes and counts up the tree and feeds the
    /// global index, children before their folder, in the order the old recursive engine used.
    /// </summary>
    private static ScanGlobalIndex AggregateFolders(FileSystemEntry root, CancellationToken cancellationToken)
    {
        var indexBuilder = new ScanGlobalIndexBuilder();
        var directories = new Stack<FileSystemEntry>();
        var resumeAt = new Stack<int>();
        directories.Push(root);
        resumeAt.Push(0);
        var steps = 0;

        while (directories.Count > 0)
        {
            var directory = directories.Peek();
            var children = directory.Children;
            var index = resumeAt.Pop();
            FileSystemEntry? descendInto = null;
            while (index < children.Count)
            {
                // Counted per entry, so a few folders holding millions of files stay cancellable.
                if (++steps % AggregationCancellationInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var child = children[index++];
                if (child.IsDirectory)
                {
                    descendInto = child;
                    break;
                }

                directory.LogicalSizeBytes += child.LogicalSizeBytes;
                directory.AllocatedSizeBytes += child.AllocatedSizeBytes;
                directory.FileCount++;
                indexBuilder.Observe(child);
            }

            if (descendInto is not null)
            {
                resumeAt.Push(index);
                directories.Push(descendInto);
                resumeAt.Push(0);
                continue;
            }

            directories.Pop();
            indexBuilder.Observe(directory);
            if (directories.TryPeek(out var parent))
            {
                parent.LogicalSizeBytes += directory.LogicalSizeBytes;
                parent.AllocatedSizeBytes += directory.AllocatedSizeBytes;
                parent.FileCount += directory.FileCount;
                parent.DirectoryCount += directory.DirectoryCount + 1;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return indexBuilder.Build(root);
    }

    private bool ShouldSkipDirectory(string directoryPath, ScanOptions options, FileAttributes attributes, out string reason)
    {
        reason = string.Empty;
        if (options.FollowReparsePoints)
        {
            return false;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (options.CloudProvider is not null &&
                _fileSystem.IsCloudFilesReparsePoint(directoryPath))
            {
                return false;
            }

            reason = "Skipped reparse point";
            return true;
        }

        if (options.CloudProvider is not null && HasHydrationProneAttributes(attributes))
        {
            reason = "Skipped cloud placeholder directory";
            return true;
        }

        return false;
    }

    private static bool ShouldAvoidAllocatedSizeProbe(FileAttributes attributes)
        => HasHydrationProneAttributes(attributes);

    private static bool HasHydrationProneAttributes(FileAttributes attributes)
        => attributes.HasFlag(FileAttributes.Offline) ||
            attributes.HasFlag(FileAttributeRecallOnOpen) ||
            attributes.HasFlag(FileAttributeRecallOnDataAccess);

    private static bool IsRecoverableMetadataException(Exception ex)
        => ex is UnauthorizedAccessException or IOException or System.Security.SecurityException;

    private static int CompareSkippedEntries(SkippedEntry left, SkippedEntry right)
    {
        var byPath = string.CompareOrdinal(left.Path, right.Path);
        return byPath != 0 ? byPath : string.CompareOrdinal(left.Reason, right.Reason);
    }

    private sealed class WorkerState
    {
        public List<FileSystemEntry> Subdirectories { get; } = [];
        public List<FileSystemEntry> Files { get; } = [];
        public List<SkippedEntry> Skipped { get; } = [];
        public int FileCount { get; set; }
        public long FileBytes { get; set; }
    }

    /// <summary>
    /// Work-queue traversal. A worker takes a directory, lists it, and queues its
    /// subdirectories; only that worker touches the directory's <c>Children</c>.
    /// A pending-directory count ends the run when it reaches zero.
    /// </summary>
    private sealed class DirectoryWalk(
        RecursiveScanProvider provider,
        ScanOptions options,
        ProgressThrottle progress,
        int degreeOfParallelism)
    {
        private int _pending;
        private long _files;
        private long _directories;
        private long _bytes;
        private Exception? _failure;

        public void Run(FileSystemEntry root, List<SkippedEntry> skipped, CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // LIFO keeps the queue short and, on one thread, visits folders in the old depth-first order.
            using var queue = new BlockingCollection<FileSystemEntry>(new ConcurrentStack<FileSystemEntry>());
            _pending = 1;
            queue.Add(root);

            var workers = new WorkerState[degreeOfParallelism];
            var helpers = new Task[degreeOfParallelism - 1];
            for (var i = 0; i < workers.Length; i++)
            {
                workers[i] = new WorkerState();
            }

            for (var i = 1; i < workers.Length; i++)
            {
                var worker = workers[i];
                helpers[i - 1] = Task.Factory.StartNew(
                    () => RunWorker(queue, worker, stop),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            // The calling thread is worker 0.
            RunWorker(queue, workers[0], stop);
            Task.WaitAll(helpers);

            cancellationToken.ThrowIfCancellationRequested();
            var failure = Volatile.Read(ref _failure);
            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            foreach (var worker in workers)
            {
                skipped.AddRange(worker.Skipped);
            }
        }

        private void RunWorker(BlockingCollection<FileSystemEntry> queue, WorkerState worker, CancellationTokenSource stop)
        {
            try
            {
                foreach (var directory in queue.GetConsumingEnumerable(stop.Token))
                {
                    provider.ListDirectory(directory, options, worker, this, stop.Token);
                    ReportDirectoryListed(directory, worker);

                    var subdirectories = worker.Subdirectories;
                    if (subdirectories.Count > 0)
                    {
                        Interlocked.Add(ref _pending, subdirectories.Count);
                        // Pushed in reverse so the stack hands them out in listing order.
                        for (var i = subdirectories.Count - 1; i >= 0; i--)
                        {
                            queue.Add(subdirectories[i]);
                        }

                        subdirectories.Clear();
                    }

                    if (Interlocked.Decrement(ref _pending) == 0)
                    {
                        queue.CompleteAdding();
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Cancelled by the caller or by another worker's failure; Run reports which.
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, ex, null);
                stop.Cancel();
            }
        }

        /// <summary>Adds files found so far in a directory that is still being listed.</summary>
        public void ReportListingProgress(string directoryPath, int fileCount, long fileBytes)
        {
            var files = Interlocked.Add(ref _files, fileCount);
            var bytes = Interlocked.Add(ref _bytes, fileBytes);
            progress.Report(directoryPath, files, Interlocked.Read(ref _directories), bytes, "Scanning files");
        }

        private void ReportDirectoryListed(FileSystemEntry directory, WorkerState worker)
        {
            var files = Interlocked.Add(ref _files, worker.FileCount);
            var bytes = Interlocked.Add(ref _bytes, worker.FileBytes);
            var directories = Interlocked.Increment(ref _directories);
            progress.Report(directory.FullPath, files, directories, bytes, "Scanning folder");
        }
    }
}

/// <summary>One directory record: what a single directory listing returns per entry.</summary>
internal readonly record struct ScanItem(string Name, FileAttributes Attributes, long Length, DateTimeOffset LastWriteTimeUtc);

internal interface IRecursiveScanFileSystem
{
    bool DirectoryExists(string path);

    /// <summary>Lists files and folders of one directory, without recursing.</summary>
    IEnumerable<ScanItem> EnumerateEntries(string directoryPath);

    bool IsCloudFilesReparsePoint(string directoryPath);

    long GetAllocatedSize(string path, long fallbackLength);
}

internal sealed class RecursiveScanFileSystem : IRecursiveScanFileSystem
{
    private const uint FileReadData = 0x00000001;
    private const uint GenericFileShare = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint IoReparseTagCloudMask = 0xFFFF0FFF;
    private const uint IoReparseTagCloud = 0x9000001A;

    // The defaults skip hidden and system entries and swallow access-denied errors; both would change results.
    private static readonly EnumerationOptions ListingOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    public static RecursiveScanFileSystem Instance { get; } = new();

    private RecursiveScanFileSystem()
    {
    }

    public bool DirectoryExists(string path)
        => Directory.Exists(path);

    public IEnumerable<ScanItem> EnumerateEntries(string directoryPath)
        => new System.IO.Enumeration.FileSystemEnumerable<ScanItem>(
            ToListingPath(directoryPath),
            static (ref IOFileSystemEntry entry) => new ScanItem(
                entry.FileName.ToString(),
                entry.Attributes,
                entry.Length,
                entry.LastWriteTimeUtc),
            ListingOptions);

    public bool IsCloudFilesReparsePoint(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var handle = CreateFile(
                directoryPath,
                FileReadData,
                GenericFileShare,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                return DeviceIoControl(
                        handle,
                        FsctlGetReparsePoint,
                        IntPtr.Zero,
                        0,
                        buffer,
                        buffer.Length,
                        out var bytesReturned,
                        IntPtr.Zero) &&
                    bytesReturned >= sizeof(uint) &&
                    IsCloudFilesReparseTag(BitConverter.ToUInt32(buffer, 0));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public long GetAllocatedSize(string path, long fallbackLength)
        => FileSizeUtilities.GetAllocatedSize(path, fallbackLength);

    /// <summary>
    /// <see cref="System.IO.Enumeration.FileSystemEnumerable{TResult}"/> passes its path through
    /// <see cref="Path.GetFullPath(string)"/>, which drops trailing dots and spaces from a path
    /// segment. NTFS allows such names, and the old engine listed them correctly, so those
    /// paths are listed in the <c>\\?\</c> form, which Windows takes unchanged.
    /// </summary>
    internal static string ToListingPath(string path)
    {
        if (!HasSegmentEndingInDotOrSpace(path) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    private static bool HasSegmentEndingInDotOrSpace(string path)
    {
        for (var i = 1; i < path.Length; i++)
        {
            var ch = path[i - 1];
            if ((ch == '.' || ch == ' ') && (path[i] == '\\' || path[i] == '/'))
            {
                return true;
            }
        }

        return path.Length > 0 && path[^1] is '.' or ' ';
    }

    private static bool IsCloudFilesReparseTag(uint reparseTag)
        => (reparseTag & IoReparseTagCloudMask) == IoReparseTagCloud;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
