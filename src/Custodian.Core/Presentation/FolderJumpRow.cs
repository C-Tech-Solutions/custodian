using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed record FolderJumpRow(string Name, string FullPath, FileSystemEntry Entry)
{
    /// <summary>
    /// Formatted size of <see cref="Entry"/>. Computed when read, because the jump box shows a
    /// few dozen rows out of an index that can hold millions.
    /// </summary>
    public string Size => SizeFormatter.Format(Entry.LogicalSizeBytes);

    public static FolderJumpRow From(FileSystemEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
        return new FolderJumpRow(name, entry.FullPath, entry);
    }

    public override string ToString()
    {
        return FullPath;
    }
}
