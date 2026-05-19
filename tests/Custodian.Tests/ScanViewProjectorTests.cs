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
