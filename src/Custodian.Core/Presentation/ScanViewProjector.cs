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
        return LargestFilesForTake(result, take)
            .Select(entry => DetailRow.From(entry, Math.Max(1, result.Root.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<DetailRow> LargestFileRows(FileSystemEntry scope, int take = 200)
    {
        return ScanAnalysis.LargestFiles(scope, take)
            .Select(entry => DetailRow.From(entry, Math.Max(1, scope.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<DetailRow> LargestFolderRows(ScanResult result, int take = 200)
    {
        return LargestFoldersForTake(result, take)
            .Select(entry => DetailRow.From(entry, Math.Max(1, result.Root.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<DetailRow> LargestFolderRows(FileSystemEntry scope, int take = 200)
    {
        return LargestFoldersForScope(scope, take)
            .Select(entry => DetailRow.From(entry, Math.Max(1, scope.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<DetailRow> ExtensionRows(ScanResult result)
    {
        return ScanAnalysis.EnsureGlobalIndex(result)
            .ExtensionSummaries
            .Select(summary => ExtensionDetailRow.From(summary, Math.Max(1, result.Root.LogicalSizeBytes)))
            .ToList();
    }

    public static IReadOnlyList<DetailRow> ExtensionRows(FileSystemEntry scope)
    {
        return ScanAnalysis.ExtensionSummary(scope)
            .Select(summary => ExtensionDetailRow.From(summary, Math.Max(1, scope.LogicalSizeBytes)))
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
        var index = ScanAnalysis.EnsureGlobalIndex(result);
        return IndexedEntryChart("Largest folders", LargestFoldersForTake(result, take), index.TotalFolderLogicalSizeBytes, index.FolderEntryCount, take);
    }

    public static ChartDataset LargestFoldersChart(FileSystemEntry scope, int take = 12)
    {
        return EntryChart($"Largest folders in {DisplayName(scope)}", DescendantFolders(scope), take);
    }

    public static ChartDataset LargestFilesChart(ScanResult result, int take = 12)
    {
        var index = ScanAnalysis.EnsureGlobalIndex(result);
        return IndexedEntryChart("Largest files", LargestFilesForTake(result, take), index.TotalFileLogicalSizeBytes, index.FileEntryCount, take);
    }

    public static ChartDataset LargestFilesChart(FileSystemEntry scope, int take = 12)
    {
        return EntryChart($"Largest files in {DisplayName(scope)}", DescendantFiles(scope), take);
    }

    public static ChartDataset ExtensionsChart(ScanResult result, int take = 12)
    {
        var summaries = ScanAnalysis.EnsureGlobalIndex(result).ExtensionSummaries;
        return ExtensionsChart("Extensions", summaries, take);
    }

    public static ChartDataset ExtensionsChart(FileSystemEntry scope, int take = 12)
    {
        return ExtensionsChart($"Extensions in {DisplayName(scope)}", ScanAnalysis.ExtensionSummary(scope), take);
    }

    private static ChartDataset ExtensionsChart(string title, IReadOnlyList<ExtensionSummary> summaries, int take)
    {
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

        return new ChartDataset(title, totalBytes, SizeFormatter.Format(totalBytes), slices, otherBytes > 0);
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
        if (!TryBuildPathBySegments(root, selected.FullPath, out var path))
        {
            return [];
        }

        return path.Select(entry => new BreadcrumbItem(DisplayName(entry), entry.FullPath, entry)).ToList();
    }

    public static bool TryFindDirectoryByPath(FileSystemEntry root, string fullPath, out FileSystemEntry entry)
    {
        if (TryBuildPathBySegments(root, fullPath, out var path) && path.Count > 0)
        {
            entry = path[^1];
            return true;
        }

        entry = null!;
        return false;
    }

    public static IReadOnlyList<FolderJumpRow> FolderJumpIndex(FileSystemEntry root)
    {
        return root
            .Flatten()
            .Where(entry => entry.IsDirectory)
            .OrderBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(FolderJumpRow.From)
            .ToList();
    }

    public static IReadOnlyList<FolderJumpRow> FolderJumpRows(IReadOnlyList<FolderJumpRow> directoryIndex, string query, int take = 50)
    {
        var trimmed = query.Trim();
        return directoryIndex
            .Where(row => string.IsNullOrWhiteSpace(trimmed)
                || row.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || row.FullPath.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .Take(take)
            .ToList();
    }

    public static IReadOnlyList<FolderJumpRow> FolderJumpRows(FileSystemEntry root, string query, int take = 50)
    {
        return FolderJumpRows(FolderJumpIndex(root), query, take);
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

    private static ChartDataset IndexedEntryChart(
        string title,
        IEnumerable<FileSystemEntry> entries,
        long totalBytes,
        long entryCount,
        int take)
    {
        var top = entries
            .Where(entry => entry.LogicalSizeBytes > 0)
            .Take(Math.Max(0, take))
            .ToList();
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

    private static IReadOnlyList<FileSystemEntry> LargestFilesForTake(ScanResult result, int take)
    {
        if (take <= 0)
        {
            return [];
        }

        var index = ScanAnalysis.EnsureGlobalIndex(result);
        return take <= index.TopEntryCount
            ? index.LargestFiles.Take(take).ToList()
            : ScanAnalysis.LargestFiles(result, take);
    }

    private static IReadOnlyList<FileSystemEntry> LargestFoldersForTake(ScanResult result, int take)
    {
        if (take <= 0)
        {
            return [];
        }

        var index = ScanAnalysis.EnsureGlobalIndex(result);
        return take <= index.TopEntryCount
            ? index.LargestFolders.Take(take).ToList()
            : ScanAnalysis.LargestFolders(result, take + 1)
                .Where(entry => !ReferenceEquals(entry, result.Root))
                .Take(take)
                .ToList();
    }

    private static IReadOnlyList<FileSystemEntry> LargestFoldersForScope(FileSystemEntry scope, int take)
    {
        if (take <= 0)
        {
            return [];
        }

        return ScanAnalysis.LargestFolders(scope, take + 1)
            .Where(entry => !ReferenceEquals(entry, scope))
            .Take(take)
            .ToList();
    }

    private static IEnumerable<FileSystemEntry> DescendantFiles(FileSystemEntry scope)
        => scope.Flatten().Where(entry => !entry.IsDirectory);

    private static IEnumerable<FileSystemEntry> DescendantFolders(FileSystemEntry scope)
        => scope.Flatten().Where(entry => entry.IsDirectory && !ReferenceEquals(entry, scope));

    private static ChartSlice EntrySlice(FileSystemEntry entry, long totalBytes, int index)
    {
        var percent = Percent(entry.LogicalSizeBytes, totalBytes);
        var label = string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
        var detail = entry.IsDirectory
            ? $"{entry.FileCount:n0} files, {entry.DirectoryCount:n0} folders"
            : string.IsNullOrWhiteSpace(entry.Extension) ? "No extension" : entry.Extension;
        var category = FileCategoryClassifier.Classify(entry);

        return new ChartSlice(
            label,
            detail,
            SizeFormatter.Format(entry.LogicalSizeBytes),
            entry.LogicalSizeBytes,
            percent,
            percent.ToString("0.0") + "%",
            // Folders get rotating palette colors (no inherent category); files get category color.
            entry.IsDirectory ? ColorAt(index) : FileCategoryClassifier.DefaultColor(category),
            ChartSliceKind.Entry,
            entry.FullPath,
            entry,
            ShortLabel(label),
            ShouldShowCallout(ChartSliceKind.Entry, percent),
            category);
    }

    private static ChartSlice ExtensionSlice(ExtensionSummary summary, long totalBytes, int index)
    {
        var percent = Percent(summary.LogicalSizeBytes, totalBytes);
        var category = FileCategoryClassifier.ClassifyExtension(summary.Extension);
        return new ChartSlice(
            summary.Extension,
            $"{summary.FileCount:n0} files",
            SizeFormatter.Format(summary.LogicalSizeBytes),
            summary.LogicalSizeBytes,
            percent,
            percent.ToString("0.0") + "%",
            FileCategoryClassifier.DefaultColor(category),
            ChartSliceKind.Extension,
            summary.Extension,
            null,
            ShortLabel(summary.Extension),
            ShouldShowCallout(ChartSliceKind.Extension, percent),
            category);
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
            false,
            FileCategory.Other);
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

    private static bool TryBuildPathBySegments(FileSystemEntry root, string fullPath, out List<FileSystemEntry> path)
    {
        path = [];
        if (!root.IsDirectory)
        {
            return false;
        }

        path.Add(root);
        if (string.Equals(root.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(root.FullPath, fullPath);
        if (relativePath == "." || Path.IsPathRooted(relativePath) || IsParentRelativePath(relativePath))
        {
            path.Clear();
            return false;
        }

        var current = root;
        foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Children.FirstOrDefault(child =>
                child.IsDirectory &&
                string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                path.Clear();
                return false;
            }

            current = next;
            path.Add(current);
        }

        return string.Equals(current.FullPath, fullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsParentRelativePath(string relativePath)
    {
        return relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
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
