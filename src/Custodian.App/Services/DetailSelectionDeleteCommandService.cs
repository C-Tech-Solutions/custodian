using System.Windows;
using Custodian.Core.Presentation;
using Custodian.Platform.Windows.Services;

namespace Custodian.App.Services;

internal enum DetailSelectionDeleteMode
{
    Recycle,
    PermanentDelete
}

internal enum DetailSelectionDeleteBlockReason
{
    None,
    Busy,
    PortableScan,
    InvalidSelection
}

internal sealed record DetailSelectionDeleteCommand(
    DetailSelectionDeleteBlockReason BlockReason,
    string? ToastMessage,
    IReadOnlyList<string> Paths,
    FileSystemOperationKind OperationKind,
    string ConfirmationTitle,
    string ConfirmationMessage,
    MessageBoxResult? DefaultResult)
{
    public bool CanExecute => BlockReason == DetailSelectionDeleteBlockReason.None;
}

internal static class DetailSelectionDeleteCommandService
{
    internal static DetailSelectionDeleteCommand Build(
        DetailSelectionDeleteMode mode,
        IReadOnlyCollection<DetailRow> selectedRows,
        bool isPortableScan,
        bool isBusy)
    {
        if (isBusy)
        {
            return Blocked(DetailSelectionDeleteBlockReason.Busy, "Wait for the current operation to finish first.");
        }

        if (isPortableScan)
        {
            return Blocked(DetailSelectionDeleteBlockReason.PortableScan);
        }

        var paths = DetailSelectionActionService.FileSystemPaths(selectedRows);
        if (selectedRows.Count == 0 || paths.Count == 0)
        {
            return Blocked(DetailSelectionDeleteBlockReason.InvalidSelection, "Select one or more file or folder rows.");
        }

        if (paths.Count != selectedRows.Count)
        {
            var message = mode == DetailSelectionDeleteMode.PermanentDelete
                ? "Permanent delete requires real file or folder rows."
                : "Delete requires real file or folder rows.";
            return Blocked(DetailSelectionDeleteBlockReason.InvalidSelection, message);
        }

        return mode == DetailSelectionDeleteMode.PermanentDelete
            ? PermanentDeleteCommand(paths)
            : RecycleCommand(paths);
    }

    private static DetailSelectionDeleteCommand RecycleCommand(IReadOnlyList<string> paths)
        => new(
            DetailSelectionDeleteBlockReason.None,
            ToastMessage: null,
            paths,
            FileSystemOperationKind.Recycle,
            "Confirm Recycle Bin move",
            $"Move {paths.Count:n0} selected item(s) to the Recycle Bin?\n\n{DetailSelectionActionService.SelectionPreview(paths)}\n\nCustodian will ask Windows to recycle these items, not permanently delete them. If Windows warns that it cannot use the Recycle Bin, cancel the operation.",
            DefaultResult: null);

    private static DetailSelectionDeleteCommand PermanentDeleteCommand(IReadOnlyList<string> paths)
        => new(
            DetailSelectionDeleteBlockReason.None,
            ToastMessage: null,
            paths,
            FileSystemOperationKind.PermanentDelete,
            "Confirm permanent delete",
            $"Permanently delete {paths.Count:n0} selected item(s)?\n\n{DetailSelectionActionService.SelectionPreview(paths)}\n\nThese items will not be moved to the Recycle Bin. You will not be able to restore them from the Recycle Bin after this operation.",
            MessageBoxResult.No);

    private static DetailSelectionDeleteCommand Blocked(
        DetailSelectionDeleteBlockReason reason,
        string? message = null)
        => new(
            reason,
            message,
            [],
            FileSystemOperationKind.Recycle,
            string.Empty,
            string.Empty,
            DefaultResult: null);
}
