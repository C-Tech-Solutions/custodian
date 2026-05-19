using Custodian.Core.Analysis;
using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public static class ScanViewProjector
{
    private static readonly string[] ChartPalette =
    [
        "#2563eb",
        "#16a34a",
        "#dc2626",
        "#9333ea",
        "#f59e0b",
        "#0891b2",
        "#db2777",
        "#65a30d",
        "#7c3aed",
        "#0d9488",
        "#ea580c",
        "#475569"
    ];

    public static IReadOnlyList<DetailRow> ChildRows(FileSystemEntry parent)
    {
        return parent.Children
            .OrderByDescending(child => child.IsDirectory)
            .ThenByDescending(child => child.LogicalSizeBytes)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(child => DetailRow.From(child, Math.Max(1, parent.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<DetailRow> LargestFileRows(ScanResult result, int take = 200)
    {
        return ScanAnalysis.LargestFiles(result, take)
            .Select(entry => DetailRow.From(entry, Math.Max(1, result.Root.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<DetailRow> LargestFolderRows(ScanResult result, int take = 200)
    {
        return ScanAnalysis.LargestFolders(result, take + 1)
            .Where(entry => !ReferenceEquals(entry, result.Root))
            .Take(take)
            .Select(entry => DetailRow.From(entry, Math.Max(1, result.Root.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<DetailRow> ExtensionRows(ScanResult result)
    {
        return ScanAnalysis.ExtensionSummary(result)
            .Select(summary => ExtensionDetailRow.From(summary, Math.Max(1, result.Root.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<ChartRow> TopChildChartRows(FileSystemEntry parent, int take = 12)
    {
        return parent.Children
            .OrderByDescending(child => child.LogicalSizeBytes)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(child => ChartRow.From(child, Math.Max(1, parent.LogicalSizeBytes)))
            .ToList();
    }

    public static ChartDataset SelectedFolderChart(FileSystemEntry parent, int take = 12)
    {
        var title = string.IsNullOrWhiteSpace(parent.Name) ? parent.FullPath : parent.Name;
        return EntryChart($"{title} distribution", parent.Children, take);
    }

    public static ChartDataset LargestFoldersChart(ScanResult result, int take = 12)
    {
        var entries = result.Root.Flatten().Where(entry => entry.IsDirectory && !ReferenceEquals(entry, result.Root));
        return EntryChart("Largest folders", entries, take);
    }

    public static ChartDataset LargestFilesChart(ScanResult result, int take = 12)
    {
        var entries = result.Root.Flatten().Where(entry => !entry.IsDirectory);
        return EntryChart("Largest files", entries, take);
    }

    public static ChartDataset ExtensionsChart(ScanResult result, int take = 12)
    {
        var summaries = ScanAnalysis.ExtensionSummary(result);
        var top = new List<ExtensionSummary>(capacity: Math.Max(0, take));
        long totalBytes = 0;

        foreach (var summary in summaries)
        {
            if (summary.LogicalSizeBytes <= 0)
            {
                continue;
            }

            totalBytes += summary.LogicalSizeBytes;
            if (top.Count < take)
            {
                top.Add(summary);
            }
        }

        var topBytes = top.Sum(summary => summary.LogicalSizeBytes);
        var otherBytes = Math.Max(0, totalBytes - topBytes);
        var slices = top
            .Select((summary, index) => ExtensionSlice(summary, totalBytes, index))
            .ToList();

        if (otherBytes > 0)
        {
            slices.Add(OtherSlice(otherBytes, totalBytes, "Other extensions"));
        }

        return new ChartDataset("Extensions", totalBytes, SizeFormatter.Format(totalBytes), slices, otherBytes > 0);
    }

    public static IReadOnlyList<SummaryMetric> SummaryMetrics(ScanResult result)
    {
        return
        [
            new("Logical", SizeFormatter.Format(result.Root.LogicalSizeBytes), "total file size"),
            new("Allocated", SizeFormatter.Format(result.Root.AllocatedSizeBytes), "disk usage estimate"),
            new("Files", $"{result.Root.FileCount:n0}", "files scanned"),
            new("Folders", $"{result.Root.DirectoryCount:n0}", "folders scanned"),
            new("Skipped", $"{result.SkippedEntries.Count:n0}", "access or link skips"),
            new("Elapsed", result.Duration.ToString(@"m\:ss\.fff"), result.Engine)
        ];
    }

    public static IReadOnlyList<SummaryMetric> SelectedSummaryMetrics(ScanResult result, FileSystemEntry selected)
    {
        var rootBytes = Math.Max(1, result.Root.LogicalSizeBytes);
        var percent = Percent(selected.LogicalSizeBytes, rootBytes);
        return
        [
            new("Logical", SizeFormatter.Format(selected.LogicalSizeBytes), "selected folder size"),
            new("Allocated", SizeFormatter.Format(selected.AllocatedSizeBytes), "selected disk usage"),
            new("Files", $"{selected.FileCount:n0}", "inside selected folder"),
            new("Folders", $"{selected.DirectoryCount:n0}", "inside selected folder"),
            new("Skipped", $"{result.SkippedEntries.Count:n0}", "scan-level skips"),
            new("Root Share", percent.ToString("0.0") + "%", "of scanned root")
        ];
    }

    public static IReadOnlyList<BreadcrumbItem> Breadcrumb(FileSystemEntry root, FileSystemEntry selected)
    {
        var path = new List<FileSystemEntry>();
        if (!FindPath(root, selected, path))
        {
            return [];
        }

        return path.Select(entry => new BreadcrumbItem(DisplayName(entry), entry.FullPath, entry)).ToList();
    }

    public static IReadOnlyList<FolderJumpRow> FolderJumpRows(FileSystemEntry root, string query, int take = 50)
    {
        var trimmed = query.Trim();
        return root
            .Flatten()
            .Where(entry => entry.IsDirectory)
            .Where(entry => string.IsNullOrWhiteSpace(trimmed)
                || entry.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || entry.FullPath.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(FolderJumpRow.From)
            .ToList();
    }

    public static double Percent(long value, long total)
    {
        if (value <= 0 || total <= 0)
        {
            return 0;
        }

        return Math.Min(100, (double)value / total * 100);
    }

    public static bool ShouldShowCallout(ChartSliceKind kind, double percent, double minimumPercent = 6)
    {
        return kind != ChartSliceKind.Other && percent >= minimumPercent;
    }

    public static string ShortLabel(string label, int maxLength = 16)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length <= maxLength)
        {
            return label;
        }

        if (maxLength <= 3)
        {
            return label[..maxLength];
        }

        return label[..(maxLength - 3)] + "...";
    }

    private static ChartDataset EntryChart(string title, IEnumerable<FileSystemEntry> entries, int take)
    {
        var top = new List<FileSystemEntry>(capacity: Math.Max(0, take));
        long totalBytes = 0;
        var entryCount = 0;

        foreach (var entry in entries)
        {
            if (entry.LogicalSizeBytes <= 0)
            {
                continue;
            }

            entryCount++;
            totalBytes += entry.LogicalSizeBytes;

            if (take <= 0)
            {
                continue;
            }

            if (top.Count < take)
            {
                top.Add(entry);
                continue;
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
        }

        top.Sort((left, right) => -CompareForTop(left, right));

        var topBytes = top.Sum(entry => entry.LogicalSizeBytes);
        var otherBytes = Math.Max(0, totalBytes - topBytes);
        var slices = top
            .Select((entry, index) => EntrySlice(entry, totalBytes, index))
            .ToList();

        if (entryCount > take && otherBytes > 0)
        {
            slices.Add(OtherSlice(otherBytes, totalBytes, "Other items"));
        }

        return new ChartDataset(title, totalBytes, SizeFormatter.Format(totalBytes), slices, otherBytes > 0);
    }

    private static ChartSlice EntrySlice(FileSystemEntry entry, long totalBytes, int index)
    {
        var percent = Percent(entry.LogicalSizeBytes, totalBytes);
        var label = string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
        var detail = entry.IsDirectory
            ? $"{entry.FileCount:n0} files, {entry.DirectoryCount:n0} folders"
            : string.IsNullOrWhiteSpace(entry.Extension) ? "No extension" : entry.Extension;

        return new ChartSlice(
            label,
            detail,
            SizeFormatter.Format(entry.LogicalSizeBytes),
            entry.LogicalSizeBytes,
            percent,
            percent.ToString("0.0") + "%",
            ColorAt(index),
            ChartSliceKind.Entry,
            entry.FullPath,
            entry,
            ShortLabel(label),
            ShouldShowCallout(ChartSliceKind.Entry, percent));
    }

    private static ChartSlice ExtensionSlice(ExtensionSummary summary, long totalBytes, int index)
    {
        var percent = Percent(summary.LogicalSizeBytes, totalBytes);
        return new ChartSlice(
            summary.Extension,
            $"{summary.FileCount:n0} files",
            SizeFormatter.Format(summary.LogicalSizeBytes),
            summary.LogicalSizeBytes,
            percent,
            percent.ToString("0.0") + "%",
            ColorAt(index),
            ChartSliceKind.Extension,
            summary.Extension,
            null,
            ShortLabel(summary.Extension),
            ShouldShowCallout(ChartSliceKind.Extension, percent));
    }

    private static ChartSlice OtherSlice(long bytes, long totalBytes, string label)
    {
        var percent = Percent(bytes, totalBytes);
        return new ChartSlice(
            label,
            "Combined smaller items",
            SizeFormatter.Format(bytes),
            bytes,
            percent,
            percent.ToString("0.0") + "%",
            "#94a3b8",
            ChartSliceKind.Other,
            "other",
            null,
            ShortLabel(label),
            false);
    }

    private static string ColorAt(int index)
    {
        return ChartPalette[index % ChartPalette.Length];
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

    private static bool FindPath(FileSystemEntry current, FileSystemEntry selected, List<FileSystemEntry> path)
    {
        path.Add(current);
        if (ReferenceEquals(current, selected) || string.Equals(current.FullPath, selected.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var child in current.Children.Where(child => child.IsDirectory))
        {
            if (FindPath(child, selected, path))
            {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static string DisplayName(FileSystemEntry entry)
    {
        var root = Path.GetPathRoot(entry.FullPath);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(root.TrimEnd('\\'), entry.FullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return entry.FullPath;
        }

        return string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
    }
}
