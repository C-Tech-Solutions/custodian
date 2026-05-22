using Custodian.Core.Formatting;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed record RecycleBinRow(
    string Icon,
    string Name,
    string OriginalLocation,
    string DateDeleted,
    DateTimeOffset? DateDeletedValue,
    string Size,
    long SizeBytes,
    string ItemType,
    string RecyclePath,
    string CategoryColor,
    string StableKey,
    RecycleBinEntry Entry)
{
    public static RecycleBinRow From(RecycleBinEntry entry)
    {
        var category = CategoryFor(entry);
        var dateDeleted = entry.DateDeleted?.LocalDateTime.ToString("g") ?? string.Empty;

        return new RecycleBinRow(
            FileCategoryClassifier.Glyph(category),
            entry.Name,
            entry.OriginalLocation,
            dateDeleted,
            entry.DateDeleted,
            SizeFormatter.Format(entry.SizeBytes),
            entry.SizeBytes,
            string.IsNullOrWhiteSpace(entry.ItemType) ? "Item" : entry.ItemType,
            entry.RecyclePath,
            FileCategoryClassifier.DefaultColor(category),
            entry.StableKey,
            entry);
    }

    private static FileCategory CategoryFor(RecycleBinEntry entry)
    {
        if (entry.ItemType.Contains("zip", StringComparison.OrdinalIgnoreCase)
            || entry.ItemType.Contains("archive", StringComparison.OrdinalIgnoreCase))
        {
            return FileCategory.Archive;
        }

        if (entry.IsFolder)
        {
            return FileCategory.Folder;
        }

        return FileCategoryClassifier.ClassifyExtension(Path.GetExtension(entry.Name));
    }
}
