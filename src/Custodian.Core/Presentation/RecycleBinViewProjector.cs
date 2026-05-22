using System.Collections;
using System.ComponentModel;
using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public static class RecycleBinViewProjector
{
    public static IReadOnlyList<RecycleBinRow> Rows(IEnumerable<RecycleBinEntry> entries)
    {
        return entries
            .Select(RecycleBinRow.From)
            .OrderByDescending(row => row.DateDeletedValue ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.RecyclePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<RecycleBinRow> FilterRows(IEnumerable<RecycleBinRow> rows, string? filter)
    {
        return rows.Where(row => RowMatchesFilter(row, filter)).ToList();
    }

    public static IReadOnlyList<RecycleBinRow> SortRows(
        IEnumerable<RecycleBinRow> rows,
        RecycleBinSortColumn column,
        ListSortDirection direction)
    {
        var comparer = new RecycleBinRowComparer(column, direction);
        return rows.OrderBy(row => row, Comparer<RecycleBinRow>.Create(comparer.Compare)).ToList();
    }

    public static bool RowMatchesFilter(RecycleBinRow row, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.OriginalLocation.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.ItemType.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.RecyclePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    public static ListSortDirection DefaultSortDirection(RecycleBinSortColumn column)
    {
        return column is RecycleBinSortColumn.DateDeleted or RecycleBinSortColumn.Size
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
    }
}

public sealed class RecycleBinRowComparer(
    RecycleBinSortColumn column,
    ListSortDirection direction) : IComparer<RecycleBinRow>, IComparer
{
    public int Compare(RecycleBinRow? left, RecycleBinRow? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return direction == ListSortDirection.Ascending ? -1 : 1;
        if (right is null) return direction == ListSortDirection.Ascending ? 1 : -1;

        var result = column switch
        {
            RecycleBinSortColumn.Name => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name),
            RecycleBinSortColumn.OriginalLocation => StringComparer.CurrentCultureIgnoreCase.Compare(left.OriginalLocation, right.OriginalLocation),
            RecycleBinSortColumn.DateDeleted => Nullable.Compare(left.DateDeletedValue, right.DateDeletedValue),
            RecycleBinSortColumn.Size => left.SizeBytes.CompareTo(right.SizeBytes),
            RecycleBinSortColumn.ItemType => StringComparer.CurrentCultureIgnoreCase.Compare(left.ItemType, right.ItemType),
            RecycleBinSortColumn.RecyclePath => StringComparer.CurrentCultureIgnoreCase.Compare(left.RecyclePath, right.RecyclePath),
            _ => 0
        };

        if (result == 0 && column != RecycleBinSortColumn.RecyclePath)
        {
            result = StringComparer.OrdinalIgnoreCase.Compare(left.RecyclePath, right.RecyclePath);
        }

        return direction == ListSortDirection.Ascending ? Math.Sign(result) : -Math.Sign(result);
    }

    public int Compare(object? x, object? y)
        => Compare(x as RecycleBinRow, y as RecycleBinRow);
}

public enum RecycleBinSortColumn
{
    Name,
    OriginalLocation,
    DateDeleted,
    Size,
    ItemType,
    RecyclePath
}
