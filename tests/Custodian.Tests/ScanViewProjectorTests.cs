using Custodian.Core.Model;
using Custodian.Core.Presentation;

namespace Custodian.Tests;

public sealed class ScanViewProjectorTests
{
    [Fact]
    public void ChildRowsSortDirectoriesBeforeFilesThenBySize()
    {
        var root = Directory(@"C:\", 240, 2, 2);
        root.Children.Add(File(@"C:\tiny.log", 10, ".log"));
        root.Children.Add(Directory(@"C:\Small", 30, 1, 0));
        root.Children.Add(File(@"C:\large.bin", 80, ".bin"));
        root.Children.Add(Directory(@"C:\Big", 120, 1, 0));

        var rows = ScanViewProjector.ChildRows(root).Select(row => row.Name).ToList();

        Assert.Equal(["Big", "Small", "large.bin", "tiny.log"], rows);
    }

    [Fact]
    public void PercentHandlesZeroAndClampsAtOneHundred()
    {
        Assert.Equal(0, ScanViewProjector.Percent(10, 0));
        Assert.Equal(0, ScanViewProjector.Percent(0, 100));
        Assert.Equal(100, ScanViewProjector.Percent(250, 100));
        Assert.Equal(25, ScanViewProjector.Percent(25, 100));
    }

    [Fact]
    public void ExtensionRowsProjectSummariesIntoDetailRows()
    {
        var result = SampleResult();

        var rows = ScanViewProjector.ExtensionRows(result).ToList();

        Assert.Equal([".bin", ".log"], rows.Select(row => row.Name).ToList());
        Assert.Equal("Extension", rows[0].Kind);
        Assert.Equal(2, rows[0].FileCount);
        Assert.Equal("60.0%", rows[0].PercentText);
    }

    [Fact]
    public void SummaryMetricsIncludeScanTotalsAndEngine()
    {
        var result = SampleResult();
        result.SkippedEntries.Add(new SkippedEntry(@"C:\Denied", "Access denied"));

        var metrics = ScanViewProjector.SummaryMetrics(result).ToDictionary(metric => metric.Label);

        Assert.Equal("Test Engine", metrics["Elapsed"].Detail);
        Assert.Equal("1", metrics["Skipped"].Value);
        Assert.Equal("3", metrics["Files"].Value);
        Assert.Equal("1", metrics["Folders"].Value);
    }

    [Fact]
    public void SelectedFolderChartKeepsTopSlicesAndAggregatesOther()
    {
        var root = Directory(@"C:\", 150, 5, 0);
        root.Children.Add(File(@"C:\a.bin", 50, ".bin"));
        root.Children.Add(File(@"C:\b.bin", 40, ".bin"));
        root.Children.Add(File(@"C:\c.log", 30, ".log"));
        root.Children.Add(File(@"C:\d.tmp", 20, ".tmp"));
        root.Children.Add(File(@"C:\e.tmp", 10, ".tmp"));

        var dataset = ScanViewProjector.SelectedFolderChart(root, take: 3);

        Assert.Equal(150, dataset.TotalBytes);
        Assert.True(dataset.HasOther);
        Assert.Equal(["a.bin", "b.bin", "c.log", "Other items"], dataset.Slices.Select(slice => slice.Label).ToList());
        Assert.Equal(30, dataset.Slices[^1].RawBytes);
        Assert.Equal(ChartSliceKind.Other, dataset.Slices[^1].Kind);
    }

    [Fact]
    public void ChartProjectionHandlesZeroSizeData()
    {
        var root = Directory(@"C:\", 0, 1, 0);
        root.Children.Add(File(@"C:\empty.txt", 0, ".txt"));

        var dataset = ScanViewProjector.SelectedFolderChart(root);

        Assert.Equal(0, dataset.TotalBytes);
        Assert.Empty(dataset.Slices);
        Assert.False(dataset.HasOther);
    }

    [Fact]
    public void LargestFileChartMatchesLargestFileRowOrder()
    {
        var result = SampleResult();

        var chartLabels = ScanViewProjector.LargestFilesChart(result, take: 2)
            .Slices
            .Where(slice => slice.Kind != ChartSliceKind.Other)
            .Select(slice => slice.Label)
            .ToList();
        var rowLabels = ScanViewProjector.LargestFileRows(result, take: 2)
            .Select(row => row.Name)
            .ToList();

        Assert.Equal(rowLabels, chartLabels);
    }

    [Fact]
    public void ExtensionChartMatchesExtensionRowOrder()
    {
        var result = SampleResult();

        var chartLabels = ScanViewProjector.ExtensionsChart(result)
            .Slices
            .Where(slice => slice.Kind != ChartSliceKind.Other)
            .Select(slice => slice.Label)
            .ToList();
        var rowLabels = ScanViewProjector.ExtensionRows(result)
            .Select(row => row.Name)
            .ToList();

        Assert.Equal(rowLabels, chartLabels);
    }

    [Fact]
    public void ChartColorsAreDeterministic()
    {
        var result = SampleResult();

        var first = ScanViewProjector.ExtensionsChart(result).Slices.Select(slice => slice.Color).ToList();
        var second = ScanViewProjector.ExtensionsChart(result).Slices.Select(slice => slice.Color).ToList();

        Assert.Equal(first, second);
    }

    private static ScanResult SampleResult()
    {
        var root = Directory(@"C:\", 200, 3, 1);
        root.Children.Add(File(@"C:\a.bin", 100, ".bin"));
        root.Children.Add(File(@"C:\b.bin", 20, ".bin"));
        root.Children.Add(File(@"C:\c.log", 80, ".log"));

        return new ScanResult
        {
            RootPath = root.FullPath,
            Root = root,
            Engine = "Test Engine",
            StartedAt = DateTimeOffset.Parse("2026-05-19T12:00:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-05-19T12:00:03Z")
        };
    }

    private static FileSystemEntry Directory(string path, long size, long fileCount, long directoryCount)
    {
        return new FileSystemEntry
        {
            Name = Path.GetFileName(path.TrimEnd('\\')),
            FullPath = path,
            IsDirectory = true,
            LogicalSizeBytes = size,
            AllocatedSizeBytes = size,
            FileCount = fileCount,
            DirectoryCount = directoryCount
        };
    }

    private static FileSystemEntry File(string path, long size, string extension)
    {
        return new FileSystemEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = false,
            LogicalSizeBytes = size,
            AllocatedSizeBytes = size,
            FileCount = 1,
            Extension = extension
        };
    }
}
