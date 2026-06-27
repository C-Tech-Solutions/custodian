using Custodian.Core.Formatting;
using Custodian.Core.Presentation;

namespace Custodian.App.Services;

internal sealed class ChartSelectionState
{
    private readonly HashSet<string> _sourceKeys = new(StringComparer.Ordinal);
    private readonly List<string> _selectionOrder = [];
    private string? _primarySourceKey;

    public int Count => _sourceKeys.Count;

    public string? PrimarySourceKey => _primarySourceKey;

    public IReadOnlyList<string> SourceKeys => _selectionOrder.ToArray();

    public void Clear()
    {
        _sourceKeys.Clear();
        _selectionOrder.Clear();
        _primarySourceKey = null;
    }

    public void SelectSingle(ChartSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        _sourceKeys.Clear();
        _selectionOrder.Clear();
        _sourceKeys.Add(slice.SourceKey);
        _selectionOrder.Add(slice.SourceKey);
        _primarySourceKey = slice.SourceKey;
    }

    public void Toggle(ChartSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (!_sourceKeys.Add(slice.SourceKey))
        {
            _sourceKeys.Remove(slice.SourceKey);
            _selectionOrder.RemoveAll(key => string.Equals(key, slice.SourceKey, StringComparison.Ordinal));
            if (string.Equals(_primarySourceKey, slice.SourceKey, StringComparison.Ordinal))
            {
                _primarySourceKey = _selectionOrder.LastOrDefault();
            }

            return;
        }

        _selectionOrder.Add(slice.SourceKey);
        _primarySourceKey = slice.SourceKey;
    }

    public void ReplaceWith(IEnumerable<ChartSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);

        _sourceKeys.Clear();
        _selectionOrder.Clear();
        _primarySourceKey = null;
        foreach (var slice in slices)
        {
            if (_sourceKeys.Add(slice.SourceKey))
            {
                _selectionOrder.Add(slice.SourceKey);
                _primarySourceKey = slice.SourceKey;
            }
        }
    }

    public void PruneTo(IEnumerable<ChartSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);

        var availableKeys = slices
            .Select(slice => slice.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        _sourceKeys.RemoveWhere(key => !availableKeys.Contains(key));
        _selectionOrder.RemoveAll(key => !availableKeys.Contains(key));
        if (_primarySourceKey is not null && !_sourceKeys.Contains(_primarySourceKey))
        {
            _primarySourceKey = _selectionOrder.LastOrDefault();
        }
    }

    public bool IsSelected(ChartSlice slice)
        => _sourceKeys.Contains(slice.SourceKey);

    public ChartSlice? PrimarySlice(IEnumerable<ChartSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);

        var materialized = slices as IReadOnlyList<ChartSlice> ?? slices.ToArray();
        return materialized.FirstOrDefault(slice => string.Equals(slice.SourceKey, _primarySourceKey, StringComparison.Ordinal))
            ?? materialized.FirstOrDefault(IsSelected);
    }

    public IReadOnlyList<ChartSlice> SelectedSlices(IEnumerable<ChartSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        return slices.Where(IsSelected).ToArray();
    }

    public static IReadOnlyList<ChartSlice> ActionableSlices(IEnumerable<ChartSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        return slices.Where(slice => slice.Kind != ChartSliceKind.Other).ToArray();
    }

    public static string SelectionText(IReadOnlyCollection<ChartSlice> selectedSlices)
    {
        return selectedSlices.Count switch
        {
            0 => "Select a slice to locate it in the grid.",
            1 => $"{selectedSlices.First().Label}: {selectedSlices.First().FormattedSize} ({selectedSlices.First().PercentText})",
            _ => $"{selectedSlices.Count:n0} chart items selected - {SizeFormatter.Format(selectedSlices.Sum(slice => slice.RawBytes))}"
        };
    }
}
