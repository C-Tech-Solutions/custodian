using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed record FolderJumpRow(string Name, string FullPath, string Size, FileSystemEntry Entry)
{
    public static FolderJumpRow From(FileSystemEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
        return new FolderJumpRow(name, entry.FullPath, SizeFormatter.Format(entry.LogicalSizeBytes), entry);
    }

    public override string ToString()
    {
        return FullPath;
    }
}
