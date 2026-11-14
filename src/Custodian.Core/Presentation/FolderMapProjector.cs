using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

/// <summary>
/// Builds the folder map: one root per scan held in the session, so every scanned drive or folder
/// shows side by side instead of only the scan on screen.
/// </summary>
public static class FolderMapProjector
{
    /// <summary>
    /// Roots in path order, so drives read C:, D:, E: and a folder scan sits right after its drive.
    /// </summary>
    public static IReadOnlyList<FolderNode> OrderRoots(IEnumerable<FolderNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return roots
            .OrderBy(root => root.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(root => root.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Makes <paramref name="target"/> hold exactly <paramref name="desired"/>, comparing by reference.
    /// Roots that are already in place are left alone, so a tree view keeps them expanded; only roots
    /// that were added, replaced by a rescan, or dropped from the session are inserted or removed.
    /// </summary>
    public static void SyncRoots(IList<FolderNode> target, IReadOnlyList<FolderNode> desired)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (IndexOfReference(desired, target[i], 0) < 0)
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
            {
                continue;
            }

            var existing = IndexOfReference(target, desired[i], i + 1);
            if (existing >= 0)
            {
                target.RemoveAt(existing);
            }

            target.Insert(i, desired[i]);
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    /// <summary>
    /// True when <paramref name="entry"/> is a folder of the tree under <paramref name="root"/> itself,
    /// not just a folder with the same path in another scan (a C:\ scan and a C:\Users scan both have
    /// a C:\Users\Public, as different entries).
    /// </summary>
    public static bool Contains(FileSystemEntry root, FileSystemEntry entry)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(entry);
        return ScanViewProjector.TryFindDirectoryByPath(root, entry.FullPath, out var found) &&
            ReferenceEquals(found, entry);
    }

    private static int IndexOfReference(IEnumerable<FolderNode> items, FolderNode item, int start)
    {
        var index = 0;
        foreach (var candidate in items)
        {
            if (index >= start && ReferenceEquals(candidate, item))
            {
                return index;
            }

            index++;
        }

        return -1;
    }
}
