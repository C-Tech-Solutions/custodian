using Custodian.Core.Analysis;
using Custodian.Core.Model;

public sealed class ScanTreeUpdaterTests
{
    [Fact]
    public void RemoveFileUpdatesAggregatesAndRebuildsGlobalIndex()
    {
        var root = BuildScanRoot(out var alpha, out var deep, out var nested, out var keep, out _);
        var result = Result(root);
        FileSystemEntry? selected = alpha;

        var removed = ScanTreeUpdater.RemoveEntry(result, nested, ref selected);

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

        var removed = ScanTreeUpdater.RemoveEntry(result, alpha, ref selected);

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

    [Fact]
    public void RemoveEntriesDeduplicatesNestedSelection()
    {
        var root = BuildScanRoot(out var alpha, out var deep, out var nested, out _, out var beta);
        var result = Result(root);

        var update = ScanTreeUpdater.RemoveEntries(result, [alpha, deep, nested], selectedEntry: nested);

        Assert.True(update.Changed);
        Assert.Equal([alpha], update.RemovedEntries);
        Assert.Same(root, update.SelectedEntry);
        Assert.DoesNotContain(alpha, root.Children);
        Assert.Single(root.Children, child => ReferenceEquals(child, beta));
        Assert.Equal(50, root.LogicalSizeBytes);
        Assert.Equal(1, root.FileCount);
        Assert.Equal(1, root.DirectoryCount);
    }

    [Fact]
    public void RemoveEntriesRemovesMultipleSiblings()
    {
        var root = BuildScanRoot(out var alpha, out _, out _, out _, out var beta);
        var result = Result(root);

        var update = ScanTreeUpdater.RemoveEntries(result, [alpha, beta], selectedEntry: beta);

        Assert.True(update.Changed);
        Assert.Equal(2, update.RemovedEntries.Count);
        Assert.Empty(root.Children);
        Assert.Same(root, update.SelectedEntry);
        Assert.Equal(0, root.LogicalSizeBytes);
        Assert.Equal(0, root.AllocatedSizeBytes);
        Assert.Equal(0, root.FileCount);
        Assert.Equal(0, root.DirectoryCount);
        Assert.Empty(result.GlobalIndex!.LargestFiles);
        Assert.Empty(result.GlobalIndex.LargestFolders);
    }

    [Fact]
    public void RemoveDirectoryPrunesSkippedEntriesUnderRemovedPath()
    {
        var root = BuildScanRoot(out var alpha, out _, out _, out _, out var beta);
        var result = Result(root);
        result.SkippedEntries.Add(new SkippedEntry(@"C:\Alpha\Locked", "Access denied"));
        result.SkippedEntries.Add(new SkippedEntry(@"C:\Alpha\Deep\Denied", "Access denied"));
        result.SkippedEntries.Add(new SkippedEntry(@"C:\Beta\Locked", "Access denied"));
        result.SkippedEntries.Add(new SkippedEntry(@"C:\Alphabet\Locked", "Access denied"));

        var update = ScanTreeUpdater.RemoveEntries(result, [alpha], selectedEntry: beta);

        Assert.True(update.Changed);
        Assert.Equal(
            [@"C:\Beta\Locked", @"C:\Alphabet\Locked"],
            result.SkippedEntries.Select(entry => entry.Path));
    }

    [Fact]
    public void RemoveEntriesIgnoresRootMissingAndDuplicateEntries()
    {
        var root = BuildScanRoot(out var alpha, out _, out _, out _, out _);
        var missing = Directory(@"C:\Missing", 25, 0, 0);
        var result = Result(root);

        var update = ScanTreeUpdater.RemoveEntries(result, [root, missing, alpha, alpha], selectedEntry: root);

        Assert.True(update.Changed);
        Assert.Equal([alpha], update.RemovedEntries);
        Assert.Same(root, update.SelectedEntry);
        Assert.DoesNotContain(alpha, root.Children);
        Assert.Equal(50, root.LogicalSizeBytes);
    }

    [Fact]
    public void RemoveEntriesReturnsNoOpForEmptyOrUnreachableSelection()
    {
        var root = BuildScanRoot(out _, out _, out _, out _, out _);
        var missing = File(@"C:\Missing\a.bin", 10, ".bin");
        var result = Result(root);

        var empty = ScanTreeUpdater.RemoveEntries(result, [], selectedEntry: root);
        var unreachable = ScanTreeUpdater.RemoveEntries(result, [missing], selectedEntry: root);

        Assert.False(empty.Changed);
        Assert.Empty(empty.RemovedEntries);
        Assert.Same(root, empty.SelectedEntry);
        Assert.False(unreachable.Changed);
        Assert.Empty(unreachable.RemovedEntries);
        Assert.Same(root, unreachable.SelectedEntry);
        Assert.Equal(350, root.LogicalSizeBytes);
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
