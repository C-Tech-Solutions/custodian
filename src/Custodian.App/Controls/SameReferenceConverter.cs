using System.Globalization;
using System.Windows.Data;

namespace Custodian.App.Controls;

/// <summary>
/// True when both bound values are the same object. The folder map uses it to highlight the row whose
/// item is the tree's selected item: with virtualization a row scrolled out of view gets a fresh
/// container, and that container's own IsSelected comes back false.
/// </summary>
public sealed class SameReferenceConverter : IMultiValueConverter
{
    public static readonly SameReferenceConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        => values is [{ } first, var second] && ReferenceEquals(first, second);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
