using Custodian.Core.Presentation;

namespace Custodian.App.Services;

internal sealed record DetailSelectionActionState(
    int SelectedCount,
    string SelectionText,
    bool CanOpen,
    bool CanReveal,
    bool CanCopy,
    bool CanMove,
    bool CanDelete,
    bool CanCopyPath,
    bool CanCopyRows,
    bool CanExport,
    string CopyText,
    string CopyToolTip,
    string MoveToolTip,
    string DeleteToolTip);

internal static class DetailSelectionActionService
{
    internal static DetailSelectionActionState Build(
        IReadOnlyCollection<DetailRow> selectedRows,
        bool isPortableScan,
        bool allSelectedRowsUseFileSystemPaths,
        bool isBusy)
    {
        var selectedCount = selectedRows.Count;
        var hasSelection = selectedCount > 0;
        var singleSelection = selectedCount == 1;
        var canUseSelection = hasSelection && (isPortableScan || allSelectedRowsUseFileSystemPaths);
        var canModifyLocal = canUseSelection && !isBusy && !isPortableScan;
        var canCopy = canUseSelection && !isBusy;

        return new DetailSelectionActionState(
            selectedCount,
            SelectionText(selectedCount, isPortableScan, allSelectedRowsUseFileSystemPaths),
            CanOpen: singleSelection && canUseSelection && !isBusy,
            CanReveal: singleSelection && canUseSelection && !isBusy,
            CanCopy: canCopy,
            CanMove: canModifyLocal,
            CanDelete: canModifyLocal,
            CanCopyPath: hasSelection,
            CanCopyRows: hasSelection,
            CanExport: hasSelection,
            CopyText: isPortableScan ? "Copy to PC" : "Copy",
            CopyToolTip: CopyToolTip(isPortableScan, allSelectedRowsUseFileSystemPaths),
            MoveToolTip: isPortableScan
                ? "Move is not available for phone scans"
                : allSelectedRowsUseFileSystemPaths
                    ? "Move selected files or folders to another folder"
                    : "Move requires real file or folder rows",
            DeleteToolTip: isPortableScan
                ? "Delete is not available for phone scans"
                : allSelectedRowsUseFileSystemPaths
                    ? "Move selected files or folders to the Recycle Bin"
                    : "Delete requires real file or folder rows");
    }

    private static string SelectionText(int count, bool isPortableScan, bool allSelectedRowsUseFileSystemPaths)
    {
        if (count > 0 && !isPortableScan && !allSelectedRowsUseFileSystemPaths)
        {
            return count == 1
                ? "1 non-file row selected"
                : $"{count:n0} items selected, some unavailable";
        }

        return count switch
        {
            0 => "No selection",
            1 => "1 item selected",
            _ => $"{count:n0} items selected"
        };
    }

    private static string CopyToolTip(bool isPortableScan, bool allSelectedRowsUseFileSystemPaths)
    {
        if (isPortableScan)
        {
            return "Copy selected phone items to a PC folder";
        }

        return allSelectedRowsUseFileSystemPaths
            ? "Copy selected files or folders to another folder"
            : "Copy requires real file or folder rows";
    }
}
