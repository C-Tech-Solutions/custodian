using Custodian.Core.Model;

namespace Custodian.Core.Analysis;

public sealed record ScanTreeUpdateResult(
    bool Changed,
    IReadOnlyList<FileSystemEntry> RemovedEntries,
    FileSystemEntry? SelectedEntry);

public static class ScanTreeUpdater
{
    public static bool RemoveEntry(ScanResult scan, FileSystemEntry entry, ref FileSystemEntry? selectedEntry)
    {
        var result = RemoveEntries(scan, [entry], selectedEntry);
        selectedEntry = result.SelectedEntry;
        return result.Changed;
    }

    public static ScanTreeUpdateResult RemoveEntries(
        ScanResult scan,
        IEnumerable<FileSystemEntry> entries,
        FileSystemEntry? selectedEntry)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(entries);

        if (scan.Root is null)
        {
            return new ScanTreeUpdateResult(false, [], selectedEntry);
        }

        var candidates = entries
            .Where(entry => entry is not null && !ReferenceEquals(scan.Root, entry))
            .Distinct()
            .Select(entry => TryBuildAncestorPath(scan.Root, entry, out var ancestors)
                ? new RemovalCandidate(entry, ancestors)
                : null)
            .OfType<RemovalCandidate>()
            .Where(candidate => candidate.Ancestors.Count > 0)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new ScanTreeUpdateResult(false, [], selectedEntry);
        }

        var selectedCandidates = candidates
            .Where(candidate => !candidates.Any(other =>
                !ReferenceEquals(other.Entry, candidate.Entry) &&
                candidate.Ancestors.Contains(other.Entry)))
            .OrderByDescending(candidate => candidate.Ancestors.Count)
            .ToArray();

        var removedEntries = new List<FileSystemEntry>(selectedCandidates.Length);
        var nextSelectedEntry = selectedEntry;
        foreach (var candidate in selectedCandidates)
        {
            var parent = candidate.Ancestors[^1];
            if (!parent.Children.Remove(candidate.Entry))
            {
                continue;
            }

            SubtractFromAncestors(candidate.Ancestors, candidate.Entry);
            removedEntries.Add(candidate.Entry);

            if (nextSelectedEntry is not null && IsEntryOrDescendant(candidate.Entry, nextSelectedEntry))
            {
                nextSelectedEntry = parent;
            }
        }

        if (removedEntries.Count == 0)
        {
            return new ScanTreeUpdateResult(false, [], selectedEntry);
        }

        scan.GlobalIndex = ScanGlobalIndexBuilder.Build(scan.Root);
        return new ScanTreeUpdateResult(true, removedEntries, nextSelectedEntry);
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
        FileSystemEntry root,
        FileSystemEntry entry,
        out List<FileSystemEntry> ancestors)
    {
        ancestors = [];
        return TryBuildAncestorPathCore(root, entry, ancestors);
    }

    private static bool TryBuildAncestorPathCore(
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
            if (TryBuildAncestorPathCore(child, entry, ancestors))
            {
                return true;
            }

            ancestors.RemoveAt(ancestors.Count - 1);
        }

        return false;
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

    private sealed record RemovalCandidate(FileSystemEntry Entry, List<FileSystemEntry> Ancestors);
}
