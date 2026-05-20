using System.Globalization;
using System.Windows.Data;

namespace Custodian.App.Controls;

/// Multi-value converter for a percent-fill bar: values are [percent (0-100),
/// track ActualWidth]. Returns the fill width in DIPs. Used so the share bar
/// can be drawn with plain Borders instead of a templated ProgressBar, which
/// is far cheaper to realize per row.
public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public static readonly PercentToWidthConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is not double percent
            || values[1] is not double trackWidth
            || double.IsNaN(trackWidth)
            || trackWidth <= 0)
        {
            return 0.0;
        }

        var clamped = Math.Clamp(percent, 0, 100);
        return trackWidth * clamped / 100.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
