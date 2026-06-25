using Custodian.Core.Analysis;
using Custodian.Core.Model;

namespace Custodian.Tui;

internal static class TuiScanTreeUpdater
{
    public static bool RemoveEntry(ScanResult scan, FileSystemEntry entry, ref FileSystemEntry? selectedEntry)
    {
        if (ReferenceEquals(scan.Root, entry))
        {
            return false;
        }

        if (!TryBuildAncestorPath(scan.Root, entry, out var ancestors) || ancestors.Count == 0)
        {
            return false;
        }

        var parent = ancestors[^1];
        if (!parent.Children.Remove(entry))
        {
            return false;
        }

        SubtractFromAncestors(ancestors, entry);
        scan.GlobalIndex = ScanGlobalIndexBuilder.Build(scan.Root);

        if (selectedEntry is not null && IsEntryOrDescendant(entry, selectedEntry))
        {
            selectedEntry = parent;
        }

        return true;
    }

    private static void SubtractFromAncestors(IEnumerable<FileSystemEntry> ancestors, FileSystemEntry removed)
    {
        var fileCount = removed.IsDirectory ? removed.FileCount : 1;
        var directoryCount = removed.IsDirectory ? removed.DirectoryCount + 1 : 0;

        foreach (var ancestor in ancestors)
        {
            ancestor.LogicalSizeBytes = Math.Max(0, ancestor.LogicalSizeBytes - removed.LogicalSizeBytes);
            ancestor.AllocatedSizeBytes = Math.Max(0, ancestor.AllocatedSizeBytes - removed.AllocatedSizeBytes);
            ancestor.FileCount = Math.Max(0, ancestor.FileCount - fileCount);
            ancestor.DirectoryCount = Math.Max(0, ancestor.DirectoryCount - directoryCount);
        }
    }

    private static bool TryBuildAncestorPath(
        FileSystemEntry current,
        FileSystemEntry entry,
        List<FileSystemEntry> ancestors)
    {
        foreach (var child in current.Children)
        {
            if (ReferenceEquals(child, entry))
            {
                ancestors.Add(current);
                return true;
            }

            if (!child.IsDirectory)
            {
                continue;
            }

            ancestors.Add(current);
            if (TryBuildAncestorPath(child, entry, ancestors))
            {
                return true;
            }

            ancestors.RemoveAt(ancestors.Count - 1);
        }

        return false;
    }

    private static bool TryBuildAncestorPath(
        FileSystemEntry root,
        FileSystemEntry entry,
        out List<FileSystemEntry> ancestors)
    {
        ancestors = [];
        if (root is null || entry is null)
        {
            return false;
        }

        return TryBuildAncestorPath(root, entry, ancestors);
    }

    private static bool IsEntryOrDescendant(FileSystemEntry root, FileSystemEntry entry)
    {
        if (ReferenceEquals(root, entry))
        {
            return true;
        }

        foreach (var child in root.Children)
        {
            if (IsEntryOrDescendant(child, entry))
            {
                return true;
            }
        }

        return false;
    }
}
