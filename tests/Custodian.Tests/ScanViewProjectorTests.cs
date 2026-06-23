using Custodian.Core.Analysis;
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
    public void SelectedSummaryMetricsUseSelectedFolderTotals()
    {
        var result = NestedResult();
        var selected = result.Root.Children.Single(child => child.Name == "Alpha");

        var metrics = ScanViewProjector.SelectedSummaryMetrics(result, selected).ToDictionary(metric => metric.Label);

        Assert.Equal("150 B", metrics["Logical"].Value);
        Assert.Equal("2", metrics["Files"].Value);
        Assert.Equal("1", metrics["Folders"].Value);
        Assert.Equal("75.0%", metrics["Root Share"].Value);
    }

    [Fact]
    public void RootSummaryReportsFullRootShare()
    {
        var result = NestedResult();

        var metrics = ScanViewProjector.SelectedSummaryMetrics(result, result.Root).ToDictionary(metric => metric.Label);

        Assert.Equal("100.0%", metrics["Root Share"].Value);
    }

    [Fact]
    public void BreadcrumbReturnsRootToSelectedOrder()
    {
        var result = NestedResult();
        var alpha = result.Root.Children.Single(child => child.Name == "Alpha");
        var deep = alpha.Children.Single(child => child.Name == "Deep");

        var breadcrumbs = ScanViewProjector.Breadcrumb(result.Root, deep);
        var file = deep.Children.Single(child => child.Name == "nested.txt");
        var foundFileParent = ScanViewProjector.TryFindParent(result.Root, file, out var fileParent);

        Assert.Equal([@"C:\", "Alpha", "Deep"], breadcrumbs.Select(item => item.Name).ToList());
        Assert.True(foundFileParent);
        Assert.Equal(@"C:\Alpha\Deep", fileParent.FullPath);
    }

    [Fact]
    public void BreadcrumbAndParentSupportNonFileSystemPaths()
    {
        var root = Directory("Pixel/Internal shared storage", 200, 1, 2);
        var dcim = Directory("Pixel/Internal shared storage/DCIM", 200, 1, 1);
        var camera = Directory("Pixel/Internal shared storage/DCIM/Camera", 200, 1, 0);
        camera.Children.Add(File("Pixel/Internal shared storage/DCIM/Camera/photo.jpg", 200, ".jpg"));
        dcim.Children.Add(camera);
        root.Children.Add(dcim);

        var breadcrumbs = ScanViewProjector.Breadcrumb(root, camera);
        var foundParent = ScanViewProjector.TryFindParent(root, camera, out var parent);
        var photo = camera.Children.Single(child => child.Name == "photo.jpg");
        var foundFileParent = ScanViewProjector.TryFindParent(root, photo, out var fileParent);

        Assert.Equal(["Internal shared storage", "DCIM", "Camera"], breadcrumbs.Select(item => item.Name).ToList());
        Assert.True(foundParent);
        Assert.Equal("Pixel/Internal shared storage/DCIM", parent.FullPath);
        Assert.True(foundFileParent);
        Assert.Equal("Pixel/Internal shared storage/DCIM/Camera", fileParent.FullPath);
    }

    [Fact]
    public void NonFileSystemLookupUsesPathSegments()
    {
        var root = Directory("Pixel/Internal shared storage", 200, 0, 2);
        var other = Directory("Pixel/Internal shared storage/Other", 200, 0, 1);
        other.Children.Add(Directory("Pixel/Internal shared storage/DCIM/Camera", 200, 0, 0));
        root.Children.Add(other);

        var found = ScanViewProjector.TryFindDirectoryByPath(
            root,
            "Pixel/Internal shared storage/DCIM/Camera",
            out _);

        Assert.False(found);
    }

    [Fact]
    public void TryFindParentReturnsFalseForNullArguments()
    {
        var root = Directory(@"C:\", 0, 0, 0);
        var child = Directory(@"C:\Child", 0, 0, 0);

        Assert.False(ScanViewProjector.TryFindParent(null!, child, out _));
        Assert.False(ScanViewProjector.TryFindParent(root, null!, out _));
    }

    [Fact]
    public void TryFindParentFindsRootForSingleSegmentPortablePath()
    {
        var root = Directory("/", 0, 0, 1);
        var child = Directory("/DCIM", 0, 0, 0);
        root.Children.Add(child);

        var found = ScanViewProjector.TryFindParent(root, child, out var parent);

        Assert.True(found);
        Assert.Equal(root, parent);
    }

    [Fact]
    public void FolderJumpRowsMatchNameAndFullPath()
    {
        var result = NestedResult();

        var byName = ScanViewProjector.FolderJumpRows(result.Root, "Deep").Single();
        var byPath = ScanViewProjector.FolderJumpRows(result.Root, @"Alpha\Deep").Single();

        Assert.Equal(@"C:\Alpha\Deep", byName.FullPath);
        Assert.Equal(@"C:\Alpha\Deep", byPath.FullPath);
    }

    [Fact]
    public void FolderJumpRowsFilterCachedIndexWithoutResorting()
    {
        var result = NestedResult();
        var index = ScanViewProjector.FolderJumpIndex(result.Root);

        var rows = ScanViewProjector.FolderJumpRows(index, "Alpha");

        Assert.Equal([@"C:\Alpha", @"C:\Alpha\Deep"], rows.Select(row => row.FullPath).ToList());
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
    public void GlobalRowsUsePreparedIndex()
    {
        var root = Directory(@"C:\", 100, 0, 0);
        var indexed = File(@"C:\indexed.bin", 100, ".bin");
        var result = new ScanResult
        {
            RootPath = root.FullPath,
            Root = root,
            Engine = "Test Engine",
            StartedAt = DateTimeOffset.Parse("2026-05-19T12:00:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-05-19T12:00:03Z"),
            GlobalIndex = new ScanGlobalIndex(
                ScanGlobalIndex.DefaultTopEntryCount,
                [indexed],
                [],
                [new ExtensionSummary(".bin", 1, 100, 100)],
                100,
                1,
                0,
                0)
        };

        var rows = ScanViewProjector.LargestFileRows(result);
        var chart = ScanViewProjector.LargestFilesChart(result);

        Assert.Equal("indexed.bin", rows.Single().Name);
        Assert.Equal("indexed.bin", chart.Slices.Single().Label);
    }

    [Fact]
    public void ScopedLargestRowsUseSelectedFolderOnly()
    {
        var result = NestedResult();
        var alpha = result.Root.Children.Single(child => child.Name == "Alpha");

        var fileRows = ScanViewProjector.LargestFileRows(alpha).Select(row => row.FullPath).ToList();
        var folderRows = ScanViewProjector.LargestFolderRows(alpha).Select(row => row.FullPath).ToList();

        Assert.Equal([@"C:\Alpha\a.bin", @"C:\Alpha\Deep\nested.txt"], fileRows);
        Assert.Equal([@"C:\Alpha\Deep"], folderRows);
        Assert.DoesNotContain(fileRows, path => path.StartsWith(@"C:\Beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScopedExtensionsUseSelectedFolderOnly()
    {
        var result = NestedResult();
        var alpha = result.Root.Children.Single(child => child.Name == "Alpha");

        var rows = ScanViewProjector.ExtensionRows(alpha).ToList();
        var chartLabels = ScanViewProjector.ExtensionsChart(alpha)
            .Slices
            .Where(slice => slice.Kind != ChartSliceKind.Other)
            .Select(slice => slice.Label)
            .ToList();

        Assert.Equal([".bin", ".txt"], rows.Select(row => row.Name).ToList());
        Assert.Equal(rows.Select(row => row.Name), chartLabels);
        Assert.Equal("66.7%", rows[0].PercentText);
    }

    [Fact]
    public void ScopedLargestChartsUseSelectedFolderOnly()
    {
        var result = NestedResult();
        var alpha = result.Root.Children.Single(child => child.Name == "Alpha");

        var fileLabels = ScanViewProjector.LargestFilesChart(alpha)
            .Slices
            .Where(slice => slice.Kind != ChartSliceKind.Other)
            .Select(slice => slice.SourceKey)
            .ToList();
        var folderLabels = ScanViewProjector.LargestFoldersChart(alpha)
            .Slices
            .Where(slice => slice.Kind != ChartSliceKind.Other)
            .Select(slice => slice.SourceKey)
            .ToList();

        Assert.Equal([@"C:\Alpha\a.bin", @"C:\Alpha\Deep\nested.txt"], fileLabels);
        Assert.Equal([@"C:\Alpha\Deep"], folderLabels);
    }

    [Fact]
    public void LargestFileProjectionHonorsTakeBeyondPreparedIndex()
    {
        var root = Directory(@"C:\", 0, 0, 0);
        long total = 0;
        for (var i = 1; i <= 205; i++)
        {
            total += i;
            root.Children.Add(File($@"C:\file{i:000}.bin", i, ".bin"));
        }
        root.LogicalSizeBytes = total;
        root.FileCount = root.Children.Count;
        var result = new ScanResult
        {
            RootPath = root.FullPath,
            Root = root,
            Engine = "Test Engine",
            StartedAt = DateTimeOffset.Parse("2026-05-19T12:00:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-05-19T12:00:03Z"),
            GlobalIndex = ScanGlobalIndexBuilder.Build(root, take: 2)
        };

        var rowLabels = ScanViewProjector.LargestFileRows(result, take: 5)
            .Select(row => row.Name)
            .ToList();
        var chartLabels = ScanViewProjector.LargestFilesChart(result, take: 5)
            .Slices
            .Where(slice => slice.Kind != ChartSliceKind.Other)
            .Select(slice => slice.Label)
            .ToList();

        Assert.Equal(["file205.bin", "file204.bin", "file203.bin", "file202.bin", "file201.bin"], rowLabels);
        Assert.Equal(rowLabels, chartLabels);
    }

    [Fact]
    public void IndexedLargestFolderProjectionExcludesRoot()
    {
        var root = Directory(@"C:\", 300, 0, 2);
        var alpha = Directory(@"C:\Alpha", 180, 0, 0);
        var beta = Directory(@"C:\Beta", 120, 0, 0);
        root.Children.Add(alpha);
        root.Children.Add(beta);
        var result = new ScanResult
        {
            RootPath = root.FullPath,
            Root = root,
            Engine = "Test Engine",
            StartedAt = DateTimeOffset.Parse("2026-05-19T12:00:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-05-19T12:00:03Z"),
            GlobalIndex = new ScanGlobalIndex(
                2,
                [],
                [root, alpha],
                [],
                0,
                0,
                alpha.LogicalSizeBytes + beta.LogicalSizeBytes,
                2)
        };

        var rowPaths = ScanViewProjector.LargestFolderRows(result, take: 2)
            .Select(row => row.FullPath)
            .ToList();
        var chartPaths = ScanViewProjector.LargestFoldersChart(result, take: 2)
            .Slices
            .Where(slice => slice.Kind != ChartSliceKind.Other)
            .Select(slice => slice.SourceKey)
            .ToList();

        Assert.Equal([alpha.FullPath, beta.FullPath], rowPaths);
        Assert.Equal(rowPaths, chartPaths);
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

    [Fact]
    public void PieCalloutEligibilityIncludesLargeNonOtherSlicesOnly()
    {
        var root = Directory(@"C:\", 100, 4, 0);
        root.Children.Add(File(@"C:\large.bin", 80, ".bin"));
        root.Children.Add(File(@"C:\small.log", 5, ".log"));
        root.Children.Add(File(@"C:\tiny.tmp", 5, ".tmp"));
        root.Children.Add(File(@"C:\other.tmp", 10, ".tmp"));

        var dataset = ScanViewProjector.SelectedFolderChart(root, take: 3);

        Assert.True(dataset.Slices.Single(slice => slice.Label == "large.bin").ShowCallout);
        Assert.False(dataset.Slices.Single(slice => slice.Label == "small.log").ShowCallout);
        Assert.False(dataset.Slices.Single(slice => slice.Kind == ChartSliceKind.Other).ShowCallout);
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

    private static ScanResult NestedResult()
    {
        var root = Directory(@"C:\", 200, 4, 3);
        var alpha = Directory(@"C:\Alpha", 150, 2, 1);
        var deep = Directory(@"C:\Alpha\Deep", 50, 1, 0);
        deep.Children.Add(File(@"C:\Alpha\Deep\nested.txt", 50, ".txt"));
        alpha.Children.Add(deep);
        alpha.Children.Add(File(@"C:\Alpha\a.bin", 100, ".bin"));
        var beta = Directory(@"C:\Beta", 50, 1, 0);
        beta.Children.Add(File(@"C:\Beta\b.log", 50, ".log"));
        root.Children.Add(alpha);
        root.Children.Add(beta);

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
