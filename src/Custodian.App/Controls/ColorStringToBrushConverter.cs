using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Custodian.App.Controls;

public sealed class ColorStringToBrushConverter : IValueConverter
{
    public static readonly ColorStringToBrushConverter Instance = new();

    private static readonly ConcurrentDictionary<string, WpfBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string color || string.IsNullOrWhiteSpace(color))
        {
            return WpfBrushes.Transparent;
        }

        return Cache.GetOrAdd(color, key =>
        {
            try
            {
                var c = (WpfColor)WpfColorConverter.ConvertFromString(key);
                var brush = new WpfSolidColorBrush(c);
                brush.Freeze();
                return brush;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return WpfBrushes.Transparent;
            }
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
