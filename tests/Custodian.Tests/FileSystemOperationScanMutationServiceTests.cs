using Custodian.App.Services;
using Custodian.Core.Model;
using Custodian.Platform.Windows.Services;

namespace Custodian.Tests;

public sealed class FileSystemOperationScanMutationServiceTests
{
    [Fact]
    public void CleanRecycleDeleteRemovesSourceEntries()
        => AssertCleanDeleteRemovesSourceEntries(FileSystemOperationKind.Recycle);

    [Fact]
    public void CleanPermanentDeleteRemovesSourceEntries()
        => AssertCleanDeleteRemovesSourceEntries(FileSystemOperationKind.PermanentDelete);

    [Fact]
    public void CleanRecycleDeleteFromFolderRootDoesNotRequireFullScanRefresh()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();

        var requiresRefresh = FileSystemOperationScanMutationService.RequiresFullScanRefreshFor(
            FileSystemOperationKind.Recycle,
            CleanBatch(),
            [entry],
            scan);

        Assert.False(requiresRefresh);
    }

    [Fact]
    public void CleanRecycleDeleteFromVolumeRootRemovesSourceEntriesWithoutFullScanRefresh()
    {
        var scan = Scan(rootPath: @"C:\");
        var entry = scan.Root.Children.Single();

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Recycle,
            CleanBatch(),
            [entry],
            scan,
            destinationFolder: null);
        var requiresRefresh = FileSystemOperationScanMutationService.RequiresFullScanRefreshFor(
            FileSystemOperationKind.Recycle,
            CleanBatch(),
            [entry],
            scan);

        Assert.Equal([entry], removed);
        Assert.False(requiresRefresh);
    }

    [Fact]
    public void RecycleIndeterminateWithMissingSourceRemovesSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();
        var batch = new FileSystemOperationBatchResult(1, 0, 0, 1, []);

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Recycle,
            batch,
            [entry],
            scan,
            destinationFolder: null,
            pathProbe: _ => SourcePathProbeResult.Missing);

        Assert.Equal([entry], removed);
    }

    [Fact]
    public void RecycleIndeterminateWithMissingSourceFromVolumeRootRemovesSourceEntriesWithoutFullScanRefresh()
    {
        var scan = Scan(rootPath: @"C:\");
        var entry = scan.Root.Children.Single();
        var batch = new FileSystemOperationBatchResult(1, 0, 0, 1, []);

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Recycle,
            batch,
            [entry],
            scan,
            destinationFolder: null,
            pathProbe: _ => SourcePathProbeResult.Missing);
        var requiresRefresh = FileSystemOperationScanMutationService.RequiresFullScanRefreshFor(
            FileSystemOperationKind.Recycle,
            batch,
            [entry],
            scan,
            pathProbe: _ => SourcePathProbeResult.Missing);

        Assert.Equal([entry], removed);
        Assert.False(requiresRefresh);
    }

    [Fact]
    public void RecycleIndeterminateWithExistingSourceDoesNotRemoveSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();
        var batch = new FileSystemOperationBatchResult(1, 0, 0, 1, []);

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Recycle,
            batch,
            [entry],
            scan,
            destinationFolder: null,
            pathProbe: _ => SourcePathProbeResult.Exists);

        Assert.Empty(removed);
    }

    [Fact]
    public void RecycleIndeterminateWithUnknownSourceStateDoesNotRemoveSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();
        var batch = new FileSystemOperationBatchResult(1, 0, 0, 1, []);

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Recycle,
            batch,
            [entry],
            scan,
            destinationFolder: null,
            pathProbe: _ => SourcePathProbeResult.Unknown);

        Assert.Empty(removed);
    }

    [Fact]
    public void RecycleIndeterminateWithFailureDoesNotRemoveMissingSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();
        var batch = new FileSystemOperationBatchResult(
            1,
            0,
            0,
            1,
            [new FileSystemOperationFailure(entry.FullPath, "denied")]);

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Recycle,
            batch,
            [entry],
            scan,
            destinationFolder: null,
            pathProbe: _ => SourcePathProbeResult.Missing);

        Assert.Empty(removed);
    }

    [Fact]
    public void CleanPermanentDeleteFromVolumeRootRemovesSourceEntries()
    {
        var scan = Scan(rootPath: @"C:\");
        var entry = scan.Root.Children.Single();

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.PermanentDelete,
            CleanBatch(),
            [entry],
            scan,
            destinationFolder: null);
        var requiresRefresh = FileSystemOperationScanMutationService.RequiresFullScanRefreshFor(
            FileSystemOperationKind.PermanentDelete,
            CleanBatch(),
            [entry],
            scan);

        Assert.Equal([entry], removed);
        Assert.False(requiresRefresh);
    }

    private static void AssertCleanDeleteRemovesSourceEntries(FileSystemOperationKind operationKind)
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();
        var batch = CleanBatch();

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            operationKind,
            batch,
            [entry],
            scan,
            destinationFolder: null);

        Assert.Equal([entry], removed);
    }

    [Fact]
    public void CleanMoveOutsideRootRemovesSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Move,
            CleanBatch(),
            [entry],
            scan,
            destinationFolder: @"D:\Archive");

        Assert.Equal([entry], removed);
    }

    [Fact]
    public void CleanMoveInsideRootDoesNotRemoveSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Move,
            CleanBatch(),
            [entry],
            scan,
            destinationFolder: @"C:\Root\Archive");

        Assert.Empty(removed);
    }

    [Fact]
    public void CopyDoesNotRemoveSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.Copy,
            CleanBatch(),
            [entry],
            scan,
            destinationFolder: @"D:\Archive");

        Assert.Empty(removed);
    }

    [Fact]
    public void IncompleteOrIssueBatchDoesNotRemoveSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();

        foreach (var batch in IssueBatches())
        {
            var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
                FileSystemOperationKind.PermanentDelete,
                batch,
                [entry],
                scan,
                destinationFolder: null);

            Assert.Empty(removed);
        }
    }

    [Fact]
    public void MissingScanOrEntriesDoesNotRemoveSourceEntries()
    {
        var scan = Scan();
        var entry = scan.Root.Children.Single();

        var noScan = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.PermanentDelete,
            CleanBatch(),
            [entry],
            currentScan: null,
            destinationFolder: null);
        var noEntries = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.PermanentDelete,
            CleanBatch(),
            [],
            scan,
            destinationFolder: null);

        Assert.Empty(noScan);
        Assert.Empty(noEntries);
    }

    [Fact]
    public void MissingScanRootDoesNotRemoveSourceEntries()
    {
        var entry = new FileSystemEntry { FullPath = @"C:\Root\a.bin" };
        var scan = new ScanResult { RootPath = @"C:\Root" };

        var removed = FileSystemOperationScanMutationService.RemovedEntriesFor(
            FileSystemOperationKind.PermanentDelete,
            CleanBatch(),
            [entry],
            scan,
            destinationFolder: null);

        Assert.Empty(removed);
    }

    private static IEnumerable<FileSystemOperationBatchResult> IssueBatches()
    {
        yield return new FileSystemOperationBatchResult(1, 0, 0, 1, []);
        yield return new FileSystemOperationBatchResult(1, 0, 1, 0, []);
        yield return new FileSystemOperationBatchResult(1, 0, 0, 0, [new FileSystemOperationFailure(@"C:\Root\a.bin", "denied")]);
        yield return new FileSystemOperationBatchResult(2, 1, 0, 0, []);
        yield return new FileSystemOperationBatchResult(0, 0, 0, 0, []);
    }

    private static FileSystemOperationBatchResult CleanBatch()
        => new(1, 1, 0, 0, []);

    private static ScanResult Scan(string rootPath = @"C:\Root")
    {
        var root = new FileSystemEntry
        {
            Name = Path.GetFileName(rootPath.TrimEnd('\\')),
            FullPath = rootPath,
            IsDirectory = true,
            LogicalSizeBytes = 10,
            AllocatedSizeBytes = 10,
            FileCount = 1
        };
        root.Children.Add(new FileSystemEntry
        {
            Name = "a.bin",
            FullPath = Path.Combine(rootPath, "a.bin"),
            LogicalSizeBytes = 10,
            AllocatedSizeBytes = 10,
            FileCount = 1,
            Extension = ".bin"
        });

        return new ScanResult
        {
            RootPath = root.FullPath,
            Root = root
        };
    }
}
