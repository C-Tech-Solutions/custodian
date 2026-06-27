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
