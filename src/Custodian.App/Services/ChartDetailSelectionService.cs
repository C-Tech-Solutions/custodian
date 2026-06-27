using Custodian.Core.Model;
using Custodian.Core.Presentation;

namespace Custodian.App.Services;

internal static class ChartDetailSelectionService
{
    internal static ChartDetailSelectionPlan BuildPlan(IEnumerable<ChartSlice> selectedSlices, ChartScope chartScope)
    {
        ArgumentNullException.ThrowIfNull(selectedSlices);

        var actionableSlices = ChartSelectionState.ActionableSlices(selectedSlices);
        if (actionableSlices.Count == 0)
        {
            return ChartDetailSelectionPlan.Empty;
        }

        var hasEntrySlices = actionableSlices.Any(slice => slice.Entry is not null);
        var desiredView = hasEntrySlices
            ? chartScope switch
            {
                ChartScope.LargestFiles => DetailViewMode.LargestFiles,
                ChartScope.LargestFolders => DetailViewMode.LargestFolders,
                _ => DetailViewMode.Contents
            }
            : DetailViewMode.Extensions;

        var entryPaths = actionableSlices
            .Where(slice => slice.Entry is not null)
            .Select(slice => slice.Entry!.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extensionKeys = actionableSlices
            .Where(slice => slice.Kind == ChartSliceKind.Extension)
            .Select(slice => slice.SourceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ChartDetailSelectionPlan(desiredView, entryPaths, extensionKeys);
    }

    internal static IReadOnlyList<DetailRow> BuildDeleteRows(IEnumerable<ChartSlice> selectedSlices)
    {
        ArgumentNullException.ThrowIfNull(selectedSlices);

        return ChartSelectionState.ActionableSlices(selectedSlices)
            .Select(DeleteRow)
            .ToArray();
    }

    private static DetailRow DeleteRow(ChartSlice slice)
    {
        if (slice.Entry is { } entry)
        {
            return DetailRow.From(entry, Math.Max(1, slice.RawBytes));
        }

        var syntheticEntry = new FileSystemEntry
        {
            Name = slice.Label,
            FullPath = slice.SourceKey,
            Extension = slice.Kind == ChartSliceKind.Extension ? slice.SourceKey : string.Empty,
            LogicalSizeBytes = slice.RawBytes,
            AllocatedSizeBytes = slice.RawBytes,
            FileCount = 0,
            DirectoryCount = 0
        };

        return new DetailRow(
            "•",
            slice.Label,
            slice.Kind == ChartSliceKind.Extension ? "Extension" : slice.Kind.ToString(),
            slice.FormattedSize,
            slice.FormattedSize,
            0,
            0,
            syntheticEntry.Extension,
            syntheticEntry.FullPath,
            0,
            slice.PercentText,
            slice.Category,
            slice.Color,
            syntheticEntry);
    }
}

internal sealed class ChartDetailSelectionPlan(
    DetailViewMode? desiredView,
    IReadOnlySet<string> entryPaths,
    IReadOnlySet<string> extensionKeys)
{
    internal static ChartDetailSelectionPlan Empty { get; } = new(null, new HashSet<string>(), new HashSet<string>());

    internal bool HasActionableSelection => desiredView is not null;

    internal DetailViewMode DesiredView => desiredView ?? DetailViewMode.Contents;

    internal IReadOnlySet<string> EntryPaths => entryPaths;

    internal IReadOnlySet<string> ExtensionKeys => extensionKeys;

    internal bool Matches(DetailRow row)
        => EntryPaths.Contains(row.FullPath) ||
           (!string.IsNullOrWhiteSpace(row.Extension) && ExtensionKeys.Contains(row.Extension));
}
