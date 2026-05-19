using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed record ExtensionDetailRow(
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
    FileSystemEntry Entry)
{
    public static DetailRow From(ExtensionSummary summary, long totalBytes)
    {
        var percent = ScanViewProjector.Percent(summary.LogicalSizeBytes, totalBytes);
        var name = summary.Extension;
        var entry = new FileSystemEntry
        {
            Name = name,
            FullPath = name,
            LogicalSizeBytes = summary.LogicalSizeBytes,
            AllocatedSizeBytes = summary.AllocatedSizeBytes,
            FileCount = summary.FileCount,
            Extension = summary.Extension
        };
        var category = FileCategoryClassifier.ClassifyExtension(summary.Extension);

        return new DetailRow(
            FileCategoryClassifier.Glyph(category),
            name,
            "Extension",
            SizeFormatter.Format(summary.LogicalSizeBytes),
            SizeFormatter.Format(summary.AllocatedSizeBytes),
            summary.FileCount,
            0,
            summary.Extension,
            name,
            percent,
            percent.ToString("0.0") + "%",
            category,
            FileCategoryClassifier.DefaultColor(category),
            entry);
    }
}
