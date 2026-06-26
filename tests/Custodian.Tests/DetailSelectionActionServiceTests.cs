using Custodian.App.Services;
using Custodian.Core.Model;
using Custodian.Core.Presentation;

namespace Custodian.Tests;

public sealed class DetailSelectionActionServiceTests
{
    [Fact]
    public void EmptySelectionDisablesActions()
    {
        var state = DetailSelectionActionService.Build(
            [],
            isPortableScan: false,
            allSelectedRowsUseFileSystemPaths: false,
            isBusy: false);

        Assert.Equal(0, state.SelectedCount);
        Assert.Equal("No selection", state.SelectionText);
        Assert.False(state.CanOpen);
        Assert.False(state.CanReveal);
        Assert.False(state.CanCopy);
        Assert.False(state.CanMove);
        Assert.False(state.CanDelete);
        Assert.False(state.CanPermanentDelete);
        Assert.False(state.CanCopyPath);
        Assert.False(state.CanExport);
    }

    [Fact]
    public void LocalSingleFileSelectionEnablesSingleAndBatchActions()
    {
        var state = DetailSelectionActionService.Build(
            [Row(@"C:\Temp\file.txt")],
            isPortableScan: false,
            allSelectedRowsUseFileSystemPaths: true,
            isBusy: false);

        Assert.Equal("1 item selected", state.SelectionText);
        Assert.True(state.CanOpen);
        Assert.True(state.CanReveal);
        Assert.True(state.CanCopy);
        Assert.True(state.CanMove);
        Assert.True(state.CanDelete);
        Assert.True(state.CanPermanentDelete);
        Assert.True(state.CanCopyPath);
        Assert.True(state.CanCopyRows);
        Assert.True(state.CanExport);
        Assert.Equal("Copy", state.CopyText);
        Assert.Contains("Recycle Bin", state.DeleteToolTip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without using the Recycle Bin", state.PermanentDeleteToolTip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalMultipleFileSelectionDisablesSingleItemActionsOnly()
    {
        var state = DetailSelectionActionService.Build(
            [Row(@"C:\Temp\a.txt"), Row(@"C:\Temp\b.txt")],
            isPortableScan: false,
            allSelectedRowsUseFileSystemPaths: true,
            isBusy: false);

        Assert.Equal("2 items selected", state.SelectionText);
        Assert.False(state.CanOpen);
        Assert.False(state.CanReveal);
        Assert.True(state.CanCopy);
        Assert.True(state.CanMove);
        Assert.True(state.CanDelete);
        Assert.True(state.CanPermanentDelete);
    }

    [Fact]
    public void PortableSelectionAllowsCopyToPcButBlocksMutation()
    {
        var state = DetailSelectionActionService.Build(
            [
                Row("Pixel/Internal shared storage/DCIM/photo.jpg", portableObjectId: "photo-id"),
                Row("Pixel/Internal shared storage/Download/file.pdf", portableObjectId: "file-id")
            ],
            isPortableScan: true,
            allSelectedRowsUseFileSystemPaths: false,
            isBusy: false);

        Assert.Equal("2 items selected", state.SelectionText);
        Assert.True(state.CanCopy);
        Assert.False(state.CanMove);
        Assert.False(state.CanDelete);
        Assert.False(state.CanPermanentDelete);
        Assert.Equal("Copy to PC", state.CopyText);
        Assert.Contains("phone", state.MoveToolTip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("phone", state.DeleteToolTip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("phone", state.PermanentDeleteToolTip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PortableSyntheticSelectionBlocksFileActions()
    {
        var state = DetailSelectionActionService.Build(
            [Row(".jpg")],
            isPortableScan: true,
            allSelectedRowsUseFileSystemPaths: false,
            isBusy: false);

        Assert.Equal("1 item selected", state.SelectionText);
        Assert.False(state.CanOpen);
        Assert.False(state.CanReveal);
        Assert.False(state.CanCopy);
        Assert.False(state.CanMove);
        Assert.False(state.CanDelete);
        Assert.True(state.CanCopyPath);
        Assert.True(state.CanCopyRows);
        Assert.True(state.CanExport);
    }

    [Fact]
    public void SyntheticLocalSelectionKeepsExportButBlocksFileOperations()
    {
        var state = DetailSelectionActionService.Build(
            [Row(".zip")],
            isPortableScan: false,
            allSelectedRowsUseFileSystemPaths: false,
            isBusy: false);

        Assert.Equal("1 non-file row selected", state.SelectionText);
        Assert.False(state.CanOpen);
        Assert.False(state.CanReveal);
        Assert.False(state.CanCopy);
        Assert.False(state.CanMove);
        Assert.False(state.CanDelete);
        Assert.False(state.CanPermanentDelete);
        Assert.True(state.CanCopyPath);
        Assert.True(state.CanCopyRows);
        Assert.True(state.CanExport);
    }

    [Fact]
    public void FileSystemPathSyntaxDoesNotRequireFilesToExist()
    {
        var rows = new[] { Row(@"C:\DefinitelyMissing\a.txt"), Row(@"C:\DefinitelyMissing\b.txt") };

        Assert.True(DetailSelectionActionService.AllRowsUseFileSystemPathSyntax(rows));
        Assert.Equal([@"C:\DefinitelyMissing\a.txt", @"C:\DefinitelyMissing\b.txt"], DetailSelectionActionService.FileSystemPaths(rows));
    }

    [Fact]
    public void FileSystemPathSyntaxRejectsSyntheticRows()
    {
        var rows = new[] { Row(@"C:\Temp\file.txt"), Row(".zip") };

        Assert.False(DetailSelectionActionService.AllRowsUseFileSystemPathSyntax(rows));
        Assert.Equal([@"C:\Temp\file.txt"], DetailSelectionActionService.FileSystemPaths(rows));
    }

    [Fact]
    public void BusyStateBlocksFileOperationsButKeepsSelectionExports()
    {
        var state = DetailSelectionActionService.Build(
            [Row(@"C:\Temp\file.txt")],
            isPortableScan: false,
            allSelectedRowsUseFileSystemPaths: true,
            isBusy: true);

        Assert.False(state.CanOpen);
        Assert.False(state.CanReveal);
        Assert.False(state.CanCopy);
        Assert.False(state.CanMove);
        Assert.False(state.CanDelete);
        Assert.False(state.CanPermanentDelete);
        Assert.True(state.CanCopyPath);
        Assert.True(state.CanCopyRows);
        Assert.True(state.CanExport);
    }

    private static DetailRow Row(
        string path,
        string portableObjectId = "",
        string portablePersistentId = "")
    {
        var entry = new FileSystemEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            LogicalSizeBytes = 10,
            AllocatedSizeBytes = 10,
            PortableObjectId = portableObjectId,
            PortablePersistentId = portablePersistentId
        };

        return DetailRow.From(entry, parentBytes: 100);
    }
}
