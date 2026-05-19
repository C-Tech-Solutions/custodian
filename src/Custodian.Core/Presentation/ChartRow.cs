using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed record ChartRow(
    string Name,
    string Detail,
    string Size,
    double Percent,
    FileSystemEntry Entry)
{
    public static ChartRow From(FileSystemEntry entry, long parentBytes)
    {
        var percent = ScanViewProjector.Percent(entry.LogicalSizeBytes, parentBytes);
        var detail = entry.IsDirectory
            ? $"{entry.FileCount:n0} files, {entry.DirectoryCount:n0} folders"
            : string.IsNullOrWhiteSpace(entry.Extension) ? "No extension" : entry.Extension;

        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
        return new ChartRow(name, detail, SizeFormatter.Format(entry.LogicalSizeBytes), percent, entry);
    }
}
