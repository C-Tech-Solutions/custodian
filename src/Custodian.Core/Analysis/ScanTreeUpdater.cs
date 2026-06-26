using System.Runtime.CompilerServices;
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

        var parentMap = BuildParentMap(scan.Root);
        var candidates = entries
            .Where(entry => entry is not null && !ReferenceEquals(scan.Root, entry))
            .Distinct(FileSystemEntryReferenceComparer.Instance)
            .Select(entry => TryBuildAncestorPath(parentMap, entry, out var ancestors)
                ? new RemovalCandidate(entry, ancestors)
                : null)
            .OfType<RemovalCandidate>()
            .Where(candidate => candidate.Ancestors.Count > 0)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new ScanTreeUpdateResult(false, [], selectedEntry);
        }

        var candidateEntries = new HashSet<FileSystemEntry>(
            candidates.Select(candidate => candidate.Entry),
            FileSystemEntryReferenceComparer.Instance);
        var selectedCandidates = candidates
            .Where(candidate => !candidate.Ancestors.Any(candidateEntries.Contains))
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

            if (nextSelectedEntry is not null &&
                IsEntryOrDescendant(parentMap, candidate.Entry, nextSelectedEntry))
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

    private static Dictionary<FileSystemEntry, FileSystemEntry> BuildParentMap(FileSystemEntry root)
    {
        var parents = new Dictionary<FileSystemEntry, FileSystemEntry>(ReferenceEqualityComparer.Instance);
        AddChildren(root, parents);
        return parents;
    }

    private static void AddChildren(
        FileSystemEntry parent,
        Dictionary<FileSystemEntry, FileSystemEntry> parents)
    {
        foreach (var child in parent.Children)
        {
            parents[child] = parent;
            if (child.IsDirectory)
            {
                AddChildren(child, parents);
            }
        }
    }

    private static bool TryBuildAncestorPath(
        IReadOnlyDictionary<FileSystemEntry, FileSystemEntry> parentMap,
        FileSystemEntry entry,
        out List<FileSystemEntry> ancestors)
    {
        ancestors = [];
        var current = entry;
        while (parentMap.TryGetValue(current, out var parent))
        {
            ancestors.Add(parent);
            current = parent;
        }

        ancestors.Reverse();
        return ancestors.Count > 0;
    }

    private static bool IsEntryOrDescendant(
        IReadOnlyDictionary<FileSystemEntry, FileSystemEntry> parentMap,
        FileSystemEntry root,
        FileSystemEntry entry)
    {
        var current = entry;
        while (current is not null)
        {
            if (ReferenceEquals(root, current))
            {
                return true;
            }

            if (!parentMap.TryGetValue(current, out var parent))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    private sealed record RemovalCandidate(FileSystemEntry Entry, List<FileSystemEntry> Ancestors);

    private sealed class FileSystemEntryReferenceComparer : IEqualityComparer<FileSystemEntry>
    {
        public static readonly FileSystemEntryReferenceComparer Instance = new();

        private FileSystemEntryReferenceComparer()
        {
        }

        public bool Equals(FileSystemEntry? x, FileSystemEntry? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(FileSystemEntry obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
