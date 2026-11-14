using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

/// <param name="SizeBytes">
/// Logical size of <paramref name="Entry"/> when the row was built. It is part of equality, so a
/// rebuilt row for a folder whose size changed replaces the row the jump box already shows.
/// </param>
public sealed record FolderJumpRow(string Name, string FullPath, FileSystemEntry Entry, long SizeBytes)
{
    /// <summary>
    /// Formatted <see cref="SizeBytes"/>. Formatted when read, because the jump box shows a few
    /// dozen rows out of an index that can hold millions.
    /// </summary>
    public string Size => SizeFormatter.Format(SizeBytes);

    public static FolderJumpRow From(FileSystemEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
        return new FolderJumpRow(name, entry.FullPath, entry, entry.LogicalSizeBytes);
    }

    public override string ToString()
    {
        return FullPath;
    }
}
