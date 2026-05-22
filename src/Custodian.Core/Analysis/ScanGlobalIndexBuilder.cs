using Custodian.Core.Model;

namespace Custodian.Core.Analysis;

public sealed class ScanGlobalIndexBuilder
{
    private readonly int _take;
    private readonly int _folderTake;
    private readonly PriorityQueue<FileSystemEntry, FileSystemEntry> _largestFiles;
    private readonly PriorityQueue<FileSystemEntry, FileSystemEntry> _largestFolders;
    private readonly Dictionary<string, ExtensionAccumulator> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private long _totalFileLogicalSizeBytes;
    private long _fileEntryCount;
    private long _totalFolderLogicalSizeBytes;
    private long _folderEntryCount;

    public ScanGlobalIndexBuilder(int take = ScanGlobalIndex.DefaultTopEntryCount)
    {
        _take = Math.Max(0, take);
        _folderTake = _take == 0 ? 0 : _take + 1;
        var comparer = Comparer<FileSystemEntry>.Create(CompareForTop);
        _largestFiles = new PriorityQueue<FileSystemEntry, FileSystemEntry>(comparer);
        _largestFolders = new PriorityQueue<FileSystemEntry, FileSystemEntry>(comparer);
    }

    public static ScanGlobalIndex Build(FileSystemEntry root, int take = ScanGlobalIndex.DefaultTopEntryCount)
    {
        var builder = new ScanGlobalIndexBuilder(take);
        Traverse(root, builder.Observe);
        return builder.Build(root);
    }

    public void Observe(FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            ObserveDirectory(entry);
            return;
        }

        ObserveFile(entry);
    }

    public ScanGlobalIndex Build(FileSystemEntry root)
    {
        var largestFiles = DrainLargest(_largestFiles)
            .Take(_take)
            .ToList();
        var largestFolders = DrainLargest(_largestFolders)
            .Where(entry => !ReferenceEquals(entry, root))
            .Take(_take)
            .ToList();

        var extensionSummaries = _extensions
            .Select(pair => new ExtensionSummary(
                pair.Key,
                pair.Value.FileCount,
                pair.Value.LogicalSizeBytes,
                pair.Value.AllocatedSizeBytes))
            .OrderByDescending(summary => summary.LogicalSizeBytes)
            .ThenBy(summary => summary.Extension, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalFolderBytes = _totalFolderLogicalSizeBytes;
        var folderEntryCount = _folderEntryCount;
        if (root.LogicalSizeBytes > 0)
        {
            totalFolderBytes = Math.Max(0, totalFolderBytes - root.LogicalSizeBytes);
            folderEntryCount = Math.Max(0, folderEntryCount - 1);
        }

        return new ScanGlobalIndex(
            _take,
            largestFiles,
            largestFolders,
            extensionSummaries,
            _totalFileLogicalSizeBytes,
            _fileEntryCount,
            totalFolderBytes,
            folderEntryCount);
    }

    private void ObserveFile(FileSystemEntry entry)
    {
        AddTop(_largestFiles, entry, _take);

        var extension = string.IsNullOrWhiteSpace(entry.Extension) ? "[no extension]" : entry.Extension;
        _extensions.TryGetValue(extension, out var accumulator);
        accumulator.FileCount++;
        accumulator.LogicalSizeBytes += entry.LogicalSizeBytes;
        accumulator.AllocatedSizeBytes += entry.AllocatedSizeBytes;
        _extensions[extension] = accumulator;

        if (entry.LogicalSizeBytes > 0)
        {
            _totalFileLogicalSizeBytes += entry.LogicalSizeBytes;
            _fileEntryCount++;
        }
    }

    private void ObserveDirectory(FileSystemEntry entry)
    {
        AddTop(_largestFolders, entry, _folderTake);

        if (entry.LogicalSizeBytes > 0)
        {
            _totalFolderLogicalSizeBytes += entry.LogicalSizeBytes;
            _folderEntryCount++;
        }
    }

    private static void AddTop(
        PriorityQueue<FileSystemEntry, FileSystemEntry> top,
        FileSystemEntry entry,
        int capacity)
    {
        if (capacity <= 0)
        {
            return;
        }

        if (top.Count < capacity)
        {
            top.Enqueue(entry, entry);
            return;
        }

        if (CompareForTop(entry, top.Peek()) > 0)
        {
            top.Dequeue();
            top.Enqueue(entry, entry);
        }
    }

    private static IReadOnlyList<FileSystemEntry> DrainLargest(PriorityQueue<FileSystemEntry, FileSystemEntry> top)
    {
        var count = top.Count;
        var results = new FileSystemEntry[count];
        for (var i = count - 1; i >= 0; i--)
        {
            results[i] = top.Dequeue();
        }

        return results;
    }

    private static int CompareForTop(FileSystemEntry left, FileSystemEntry right)
    {
        var size = left.LogicalSizeBytes.CompareTo(right.LogicalSizeBytes);
        if (size != 0)
        {
            return size;
        }

        return -StringComparer.OrdinalIgnoreCase.Compare(left.FullPath, right.FullPath);
    }

    private static void Traverse(FileSystemEntry root, Action<FileSystemEntry> visit)
    {
        var stack = new Stack<FileSystemEntry>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            visit(entry);

            for (var i = entry.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(entry.Children[i]);
            }
        }
    }

    private struct ExtensionAccumulator
    {
        public long FileCount;
        public long LogicalSizeBytes;
        public long AllocatedSizeBytes;
    }
}
