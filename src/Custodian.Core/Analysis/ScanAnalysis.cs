using Custodian.Core.Model;

namespace Custodian.Core.Analysis;

public static class ScanAnalysis
{
    public static IReadOnlyList<FileSystemEntry> LargestFiles(ScanResult result, int take = 200)
    {
        return TopEntries(result.Root, take, e => !e.IsDirectory);
    }

    public static IReadOnlyList<FileSystemEntry> LargestFolders(ScanResult result, int take = 200)
    {
        return TopEntries(result.Root, take, e => e.IsDirectory);
    }

    public static IReadOnlyList<ExtensionSummary> ExtensionSummary(ScanResult result)
    {
        var summaries = new Dictionary<string, ExtensionAccumulator>(StringComparer.OrdinalIgnoreCase);

        Traverse(result.Root, entry =>
        {
            if (entry.IsDirectory)
            {
                return;
            }

            var extension = string.IsNullOrWhiteSpace(entry.Extension) ? "[no extension]" : entry.Extension;
            summaries.TryGetValue(extension, out var accumulator);
            accumulator.FileCount++;
            accumulator.LogicalSizeBytes += entry.LogicalSizeBytes;
            accumulator.AllocatedSizeBytes += entry.AllocatedSizeBytes;
            summaries[extension] = accumulator;
        });

        return summaries
            .Select(pair => new ExtensionSummary(pair.Key, pair.Value.FileCount, pair.Value.LogicalSizeBytes, pair.Value.AllocatedSizeBytes))
            .OrderByDescending(e => e.LogicalSizeBytes)
            .ThenBy(e => e.Extension, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<FileSystemEntry> TopEntries(FileSystemEntry root, int take, Func<FileSystemEntry, bool> predicate)
    {
        if (take <= 0)
        {
            return [];
        }

        var top = new List<FileSystemEntry>(capacity: take);

        Traverse(root, entry =>
        {
            if (!predicate(entry))
            {
                return;
            }

            if (top.Count < take)
            {
                top.Add(entry);
                return;
            }

            var smallestIndex = 0;
            for (var i = 1; i < top.Count; i++)
            {
                if (CompareForTop(top[i], top[smallestIndex]) < 0)
                {
                    smallestIndex = i;
                }
            }

            if (CompareForTop(entry, top[smallestIndex]) > 0)
            {
                top[smallestIndex] = entry;
            }
        });

        top.Sort((left, right) => -CompareForTop(left, right));
        return top;
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

    private static void Traverse(FileSystemEntry entry, Action<FileSystemEntry> visit)
    {
        visit(entry);
        foreach (var child in entry.Children)
        {
            Traverse(child, visit);
        }
    }

    private struct ExtensionAccumulator
    {
        public long FileCount;
        public long LogicalSizeBytes;
        public long AllocatedSizeBytes;
    }
}
