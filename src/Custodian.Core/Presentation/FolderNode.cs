using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed class FolderNode
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public double Percent { get; init; }
    public string PercentText { get; init; } = string.Empty;
    public FileSystemEntry Entry { get; init; } = new();
    public List<FolderNode> Children { get; init; } = [];

    public static FolderNode From(FileSystemEntry entry, long parentBytes)
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
            Children = entry.Children
                .Where(child => child.IsDirectory)
                .OrderByDescending(child => child.LogicalSizeBytes)
                .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child => From(child, Math.Max(1, entry.LogicalSizeBytes)))
                .ToList()
        };
    }
}
