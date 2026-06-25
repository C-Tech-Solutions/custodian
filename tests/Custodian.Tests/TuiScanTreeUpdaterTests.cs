using Custodian.Core.Analysis;
using Custodian.Core.Model;
using Custodian.Tui;

public sealed class TuiScanTreeUpdaterTests
{
    [Fact]
    public void RemoveFileUpdatesAggregatesAndRebuildsGlobalIndex()
    {
        var root = BuildScanRoot(out var alpha, out var deep, out var nested, out var keep, out _);
        var result = Result(root);
        FileSystemEntry? selected = alpha;

        var removed = TuiScanTreeUpdater.RemoveEntry(result, nested, ref selected);

        Assert.True(removed);
        Assert.DoesNotContain(nested, deep.Children);
        Assert.Same(alpha, selected);
        Assert.Equal(150, root.LogicalSizeBytes);
        Assert.Equal(150, root.AllocatedSizeBytes);
        Assert.Equal(2, root.FileCount);
        Assert.Equal(3, root.DirectoryCount);
        Assert.Equal(100, alpha.LogicalSizeBytes);
        Assert.Equal(1, alpha.FileCount);
        Assert.Equal(1, alpha.DirectoryCount);
        Assert.Equal(0, deep.LogicalSizeBytes);
        Assert.Equal(0, deep.FileCount);
        Assert.DoesNotContain(result.GlobalIndex!.LargestFiles, entry => ReferenceEquals(entry, nested));
        Assert.Contains(result.GlobalIndex.LargestFiles, entry => ReferenceEquals(entry, keep));
        Assert.DoesNotContain(result.GlobalIndex.ExtensionSummaries, summary => summary.Extension == ".bin");
    }

    [Fact]
    public void RemoveDirectoryUpdatesNestedCountsAndMovesSelectedDescendantToParent()
    {
        var root = BuildScanRoot(out var alpha, out _, out var nested, out _, out var beta);
        var result = Result(root);
        FileSystemEntry? selected = nested;

        var removed = TuiScanTreeUpdater.RemoveEntry(result, alpha, ref selected);

        Assert.True(removed);
        Assert.DoesNotContain(alpha, root.Children);
        Assert.Same(root, selected);
        Assert.Equal(50, root.LogicalSizeBytes);
        Assert.Equal(50, root.AllocatedSizeBytes);
        Assert.Equal(1, root.FileCount);
        Assert.Equal(1, root.DirectoryCount);
        Assert.DoesNotContain(result.GlobalIndex!.LargestFolders, entry => ReferenceEquals(entry, alpha));
        Assert.DoesNotContain(result.GlobalIndex.LargestFiles, entry => ReferenceEquals(entry, nested));
        Assert.Single(result.GlobalIndex.LargestFolders, entry => ReferenceEquals(entry, beta));
    }

    private static ScanResult Result(FileSystemEntry root)
        => new()
        {
            RootPath = root.FullPath,
            Root = root,
            Engine = "Test Engine",
            StartedAt = DateTimeOffset.Parse("2026-05-19T12:00:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-05-19T12:00:03Z"),
            GlobalIndex = ScanGlobalIndexBuilder.Build(root)
        };

    private static FileSystemEntry BuildScanRoot(
        out FileSystemEntry alpha,
        out FileSystemEntry deep,
        out FileSystemEntry nested,
        out FileSystemEntry keep,
        out FileSystemEntry beta)
    {
        var root = Directory(@"C:\", 350, 3, 3);
        alpha = Directory(@"C:\Alpha", 300, 2, 1);
        deep = Directory(@"C:\Alpha\Deep", 200, 1, 0);
        nested = File(@"C:\Alpha\Deep\nested.bin", 200, ".bin");
        keep = File(@"C:\Alpha\keep.log", 100, ".log");
        beta = Directory(@"C:\Beta", 50, 1, 0);
        var betaFile = File(@"C:\Beta\b.txt", 50, ".txt");

        deep.Children.Add(nested);
        alpha.Children.Add(deep);
        alpha.Children.Add(keep);
        beta.Children.Add(betaFile);
        root.Children.Add(alpha);
        root.Children.Add(beta);

        return root;
    }

    private static FileSystemEntry Directory(string path, long size, long fileCount, long directoryCount)
        => new()
        {
            Name = Path.GetFileName(path.TrimEnd('\\')),
            FullPath = path,
            IsDirectory = true,
            LogicalSizeBytes = size,
            AllocatedSizeBytes = size,
            FileCount = fileCount,
            DirectoryCount = directoryCount
        };

    private static FileSystemEntry File(string path, long size, string extension)
        => new()
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
