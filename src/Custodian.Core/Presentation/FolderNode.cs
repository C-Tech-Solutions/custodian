using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed class FolderNode
{
    private List<FolderNode>? _children;
    private FolderNode? _root;

    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public double Percent { get; init; }
    public string PercentText { get; init; } = string.Empty;
    public FileSystemEntry Entry { get; init; } = new();

    /// <summary>
    /// The root node of the scan this folder belongs to, or the node itself for a root. The folder
    /// map shows several scans at once and uses this to tell which scan a clicked folder came from:
    /// a C:\ scan and a C:\Users scan both have a C:\Users\Public, as different nodes.
    /// </summary>
    public FolderNode Root => _root ?? this;

    /// <summary>
    /// Child folder nodes, largest first. Built from <see cref="Entry"/> on first access, so a
    /// tree view only pays for the levels it shows.
    /// </summary>
    public List<FolderNode> Children
    {
        get => _children ?? BuildChildren();
        init => _children = value;
    }

    public static FolderNode From(FileSystemEntry entry, long parentBytes) => From(entry, parentBytes, root: null);

    private static FolderNode From(FileSystemEntry entry, long parentBytes, FolderNode? root)
    {
        var percent = ScanViewProjector.Percent(entry.LogicalSizeBytes, parentBytes);
        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
        return new FolderNode
        {
            Name = name,
            FullPath = entry.FullPath,
            Size = SizeFormatter.Format(entry.LogicalSizeBytes),
            Percent = percent,
            PercentText = percent.ToString("0.0") + "%",
            Entry = entry,
            _root = root
        };
    }

    private List<FolderNode> BuildChildren()
    {
        var parentBytes = Math.Max(1, Entry.LogicalSizeBytes);
        var children = Entry.Children
            .Where(child => child.IsDirectory)
            .OrderByDescending(child => child.LogicalSizeBytes)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(child => From(child, parentBytes, Root))
            .ToList();
        return Interlocked.CompareExchange(ref _children, children, null) ?? children;
    }
}
