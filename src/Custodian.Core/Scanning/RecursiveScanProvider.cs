using System.Diagnostics;
using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

public sealed class RecursiveScanProvider : IDiskScanProvider
{
    public string Name => "Recursive";

    public bool CanScan(ScanOptions options, out string reason)
    {
        reason = string.Empty;
        return Directory.Exists(options.RootPath);
    }

    public Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var scanWatch = Stopwatch.StartNew();
        var skipped = new List<SkippedEntry>();
        var counters = new ScanCounters();
        var progressThrottle = new ProgressThrottle(progress);
        var root = ScanDirectory(new DirectoryInfo(options.RootPath), options, skipped, counters, progressThrottle, cancellationToken);
        scanWatch.Stop();

        return Task.FromResult(new ScanResult
        {
            RootPath = root.FullPath,
            Engine = Name,
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow,
            Root = root,
            SkippedEntries = skipped,
            PhaseTimings =
            [
                new ScanPhaseTiming("Recursive enumeration", scanWatch.Elapsed)
            ]
        });
    }

    private static FileSystemEntry ScanDirectory(
        DirectoryInfo directory,
        ScanOptions options,
        List<SkippedEntry> skipped,
        ScanCounters counters,
        ProgressThrottle progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = new FileSystemEntry
        {
            Name = directory.Name.Length == 0 ? directory.FullName : directory.Name,
            FullPath = directory.FullName,
            IsDirectory = true,
            Attributes = directory.Attributes.ToString(),
            LastWriteTime = SafeLastWriteTime(directory)
        };

        counters.Directories++;
        progress.Report(directory.FullName, counters.Files, counters.Directories, counters.Bytes, "Scanning folder");

        try
        {
            foreach (var childDirectory in directory.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!options.FollowReparsePoints && childDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped.Add(new SkippedEntry(childDirectory.FullName, "Skipped reparse point"));
                    continue;
                }

                var childEntry = ScanDirectory(childDirectory, options, skipped, counters, progress, cancellationToken);
                entry.Children.Add(childEntry);
                entry.LogicalSizeBytes += childEntry.LogicalSizeBytes;
                entry.AllocatedSizeBytes += childEntry.AllocatedSizeBytes;
                entry.FileCount += childEntry.FileCount;
                entry.DirectoryCount += childEntry.DirectoryCount + 1;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            skipped.Add(new SkippedEntry(directory.FullName, ex.Message));
        }

        try
        {
            foreach (var file in directory.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileEntry = ScanFile(file, options, skipped);
                if (fileEntry is null)
                {
                    continue;
                }

                entry.Children.Add(fileEntry);
                entry.LogicalSizeBytes += fileEntry.LogicalSizeBytes;
                entry.AllocatedSizeBytes += fileEntry.AllocatedSizeBytes;
                entry.FileCount++;
                counters.Files++;
                counters.Bytes += fileEntry.LogicalSizeBytes;
                progress.Report(file.FullName, counters.Files, counters.Directories, counters.Bytes, "Scanning files");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            skipped.Add(new SkippedEntry(directory.FullName, ex.Message));
        }

        return entry;
    }

    private static FileSystemEntry? ScanFile(FileInfo file, ScanOptions options, List<SkippedEntry> skipped)
    {
        try
        {
            var length = file.Length;
            return new FileSystemEntry
            {
                Name = file.Name,
                FullPath = file.FullName,
                IsDirectory = false,
                LogicalSizeBytes = length,
                AllocatedSizeBytes = options.CollectAllocatedSize ? FileSizeUtilities.GetAllocatedSize(file.FullName, length) : length,
                FileCount = 1,
                Extension = file.Extension.ToLowerInvariant(),
                Attributes = file.Attributes.ToString(),
                LastWriteTime = file.LastWriteTimeUtc
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            skipped.Add(new SkippedEntry(file.FullName, ex.Message));
            return null;
        }
    }

    private static DateTimeOffset? SafeLastWriteTime(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class ScanCounters
    {
        public long Files { get; set; }
        public long Directories { get; set; }
        public long Bytes { get; set; }
    }
}
