using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed record DetailRow(
    string Icon,
    string Name,
    string Kind,
    string LogicalSize,
    string AllocatedSize,
    long FileCount,
    long DirectoryCount,
    string Extension,
    string FullPath,
    double Percent,
    string PercentText,
    FileCategory Category,
    string CategoryColor,
    FileSystemEntry Entry)
{
    public static DetailRow From(FileSystemEntry entry, long parentBytes)
    {
        var percent = ScanViewProjector.Percent(entry.LogicalSizeBytes, parentBytes);
        var kind = entry.IsDirectory ? "Folder" : string.IsNullOrWhiteSpace(entry.Extension) ? "File" : entry.Extension.TrimStart('.').ToUpperInvariant();

        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.FullPath : entry.Name;
        var category = FileCategoryClassifier.Classify(entry);

        return new DetailRow(
            FileCategoryClassifier.Glyph(category),
            name,
            kind,
            SizeFormatter.Format(entry.LogicalSizeBytes),
            SizeFormatter.Format(entry.AllocatedSizeBytes),
            entry.FileCount,
            entry.DirectoryCount,
            entry.Extension,
            entry.FullPath,
            percent,
            percent.ToString("0.0") + "%",
            category,
            FileCategoryClassifier.DefaultColor(category),
            entry);
    }

    public override string ToString()
    {
        return $"{Kind}\t{LogicalSize}\t{PercentText}\t{FullPath}";
    }
}
