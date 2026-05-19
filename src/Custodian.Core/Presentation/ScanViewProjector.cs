using Custodian.Core.Analysis;
using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public static class ScanViewProjector
{
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
        return ScanAnalysis.LargestFolders(result, take)
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

    public static double Percent(long value, long total)
    {
        if (value <= 0 || total <= 0)
        {
            return 0;
        }

        return Math.Min(100, (double)value / total * 100);
    }
}
