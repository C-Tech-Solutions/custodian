using System.IO;
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
    bool CanPermanentDelete,
    bool CanCopyPath,
    bool CanCopyRows,
    bool CanExport,
    string CopyText,
    string CopyToolTip,
    string MoveToolTip,
    string DeleteToolTip,
    string PermanentDeleteToolTip);

internal static class DetailSelectionActionService
{
    internal const string ImportedScanFileOperationsMessage =
        "File operations are unavailable for imported scans. Run a new live scan to enable them.";

    internal static DetailSelectionActionState Build(
        IReadOnlyCollection<DetailRow> selectedRows,
        bool isPortableScan,
        bool isLoadedFromScanFile,
        bool allSelectedRowsUseFileSystemPaths,
        bool isBusy)
    {
        var selectedCount = selectedRows.Count;
        var hasSelection = selectedCount > 0;
        var singleSelection = selectedCount == 1;
        var canUseSelection = hasSelection &&
            (isPortableScan
                ? AllRowsUsePortableObjectIdentity(selectedRows)
                : allSelectedRowsUseFileSystemPaths);
        var canModifyLocal = canUseSelection && !isBusy && !isPortableScan && !isLoadedFromScanFile;
        var canCopy = canUseSelection && !isBusy && !isLoadedFromScanFile;

        return new DetailSelectionActionState(
            selectedCount,
            SelectionText(selectedCount, isPortableScan, allSelectedRowsUseFileSystemPaths),
            CanOpen: singleSelection && canUseSelection && !isBusy,
            CanReveal: singleSelection && canUseSelection && !isBusy,
            CanCopy: canCopy,
            CanMove: canModifyLocal,
            CanDelete: canModifyLocal,
            CanPermanentDelete: canModifyLocal,
            CanCopyPath: hasSelection,
            CanCopyRows: hasSelection,
            CanExport: hasSelection,
            CopyText: isPortableScan ? "Copy to PC" : "Copy",
            CopyToolTip: isLoadedFromScanFile
                ? ImportedScanFileOperationsMessage
                : CopyToolTip(isPortableScan, allSelectedRowsUseFileSystemPaths),
            MoveToolTip: isLoadedFromScanFile
                ? ImportedScanFileOperationsMessage
                : isPortableScan
                ? "Move is not available for phone scans"
                : allSelectedRowsUseFileSystemPaths
                    ? "Move selected files or folders to another folder"
                    : "Move requires real file or folder rows",
            DeleteToolTip: isLoadedFromScanFile
                ? ImportedScanFileOperationsMessage
                : isPortableScan
                ? "Delete is not available for phone scans"
                : allSelectedRowsUseFileSystemPaths
                    ? "Move selected files or folders to the Recycle Bin"
                    : "Delete requires real file or folder rows",
            PermanentDeleteToolTip: isLoadedFromScanFile
                ? ImportedScanFileOperationsMessage
                : isPortableScan
                ? "Permanent delete is not available for phone scans"
                : allSelectedRowsUseFileSystemPaths
                    ? "Permanently delete selected files or folders without using the Recycle Bin"
                    : "Delete requires real file or folder rows");
    }

    internal static bool AllRowsUseFileSystemPathSyntax(IReadOnlyCollection<DetailRow> rows)
        => rows.Count > 0 && rows.All(row => IsFileSystemPathSyntax(row.FullPath));

    internal static IReadOnlyList<string> FileSystemPaths(IEnumerable<DetailRow> rows)
        => rows
            .Select(row => row.FullPath)
            .Where(IsFileSystemPathSyntax)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static bool AllRowsUsePortableObjectIdentity(IReadOnlyCollection<DetailRow> rows)
        => rows.Count > 0 && rows.All(UsesPortableObjectIdentity);

    internal static string SelectionPreview(IReadOnlyList<string> paths)
    {
        var preview = string.Join(Environment.NewLine, paths.Take(6));
        return paths.Count > 6
            ? preview + $"{Environment.NewLine}...and {paths.Count - 6:n0} more."
            : preview;
    }

    private static bool IsFileSystemPathSyntax(string path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool UsesPortableObjectIdentity(DetailRow row)
        => !string.IsNullOrWhiteSpace(row.Entry.PortableObjectId) ||
           !string.IsNullOrWhiteSpace(row.Entry.PortablePersistentId);

    private static string SelectionText(int count, bool isPortableScan, bool allSelectedRowsUseFileSystemPathSyntax)
    {
        if (count > 0 && !isPortableScan && !allSelectedRowsUseFileSystemPathSyntax)
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

    private static string CopyToolTip(bool isPortableScan, bool allSelectedRowsUseFileSystemPathSyntax)
    {
        if (isPortableScan)
        {
            return "Copy selected phone items to a PC folder";
        }

        return allSelectedRowsUseFileSystemPathSyntax
            ? "Copy selected files or folders to another folder"
            : "Copy requires real file or folder rows";
    }
}
