using System.Windows;
using Custodian.App.Services;
using Custodian.Core.Model;
using Custodian.Core.Presentation;
using Custodian.Platform.Windows.Services;

namespace Custodian.Tests;

public sealed class DetailSelectionDeleteCommandServiceTests
{
    [Fact]
    public void BusyStateBlocksDeleteCommands()
    {
        var command = DetailSelectionDeleteCommandService.Build(
            DetailSelectionDeleteMode.PermanentDelete,
            [Row(@"C:\Temp\file.txt")],
            isPortableScan: false,
            isLoadedFromScanFile: false,
            isBusy: true);

        Assert.False(command.CanExecute);
        Assert.Equal(DetailSelectionDeleteBlockReason.Busy, command.BlockReason);
        Assert.Contains("current operation", command.ToastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PortableScansBlockDeleteCommands()
    {
        var command = DetailSelectionDeleteCommandService.Build(
            DetailSelectionDeleteMode.Recycle,
            [Row("Pixel/Internal shared storage/DCIM/photo.jpg")],
            isPortableScan: true,
            isLoadedFromScanFile: false,
            isBusy: false);

        Assert.False(command.CanExecute);
        Assert.Equal(DetailSelectionDeleteBlockReason.PortableScan, command.BlockReason);
    }

    [Fact]
    public void RecycleCommandUsesRecycleOperationAndNoForcedDefaultButton()
    {
        var command = DetailSelectionDeleteCommandService.Build(
            DetailSelectionDeleteMode.Recycle,
            [Row(@"C:\Temp\file.txt")],
            isPortableScan: false,
            isLoadedFromScanFile: false,
            isBusy: false);

        Assert.True(command.CanExecute);
        Assert.Equal(FileSystemOperationKind.Recycle, command.OperationKind);
        Assert.Null(command.DefaultResult);
        Assert.Contains("Recycle Bin", command.ConfirmationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PermanentDeleteCommandUsesPermanentDeleteOperationAndDefaultsNo()
    {
        var command = DetailSelectionDeleteCommandService.Build(
            DetailSelectionDeleteMode.PermanentDelete,
            [Row(@"C:\Temp\file.txt")],
            isPortableScan: false,
            isLoadedFromScanFile: false,
            isBusy: false);

        Assert.True(command.CanExecute);
        Assert.Equal(FileSystemOperationKind.PermanentDelete, command.OperationKind);
        Assert.Equal(MessageBoxResult.No, command.DefaultResult);
        Assert.Contains("will not be moved to the Recycle Bin", command.ConfirmationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyntheticRowsBlockDeleteCommands()
    {
        var command = DetailSelectionDeleteCommandService.Build(
            DetailSelectionDeleteMode.PermanentDelete,
            [Row(".zip")],
            isPortableScan: false,
            isLoadedFromScanFile: false,
            isBusy: false);

        Assert.False(command.CanExecute);
        Assert.Equal(DetailSelectionDeleteBlockReason.InvalidSelection, command.BlockReason);
        Assert.Contains("file or folder rows", command.ToastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportedScansBlockDeleteCommandsBeforePathUse()
    {
        var command = DetailSelectionDeleteCommandService.Build(
            DetailSelectionDeleteMode.PermanentDelete,
            [Row(@"C:\Temp\file.txt")],
            isPortableScan: false,
            isLoadedFromScanFile: true,
            isBusy: false);

        Assert.False(command.CanExecute);
        Assert.Empty(command.Paths);
        Assert.Equal(DetailSelectionDeleteBlockReason.LoadedScan, command.BlockReason);
        Assert.Equal(DetailSelectionActionService.ImportedScanFileOperationsMessage, command.ToastMessage);
    }

    [Fact]
    public void ImportedPortableScansUseLoadedScanBlockReason()
    {
        var command = DetailSelectionDeleteCommandService.Build(
            DetailSelectionDeleteMode.Recycle,
            [Row("Pixel/Internal shared storage/DCIM/photo.jpg")],
            isPortableScan: true,
            isLoadedFromScanFile: true,
            isBusy: false);

        Assert.False(command.CanExecute);
        Assert.Equal(DetailSelectionDeleteBlockReason.LoadedScan, command.BlockReason);
        Assert.Equal(DetailSelectionActionService.ImportedScanFileOperationsMessage, command.ToastMessage);
    }

    private static DetailRow Row(string path)
    {
        var entry = new FileSystemEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            LogicalSizeBytes = 10,
            AllocatedSizeBytes = 10
        };

        return DetailRow.From(entry, parentBytes: 100);
    }
}
