using System.Globalization;
using System.Windows.Data;
using Custodian.Platform.Windows.Logging;
using Microsoft.Extensions.Logging;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Custodian.App.Controls;

public sealed class ColorStringToBrushConverter : IValueConverter
{
    public static readonly ColorStringToBrushConverter Instance = new();

    private const int MaxCacheEntries = 256;
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(ColorStringToBrushConverter).FullName!);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<CacheEntry> CacheOrder = [];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string color || string.IsNullOrWhiteSpace(color))
        {
            return WpfBrushes.Transparent;
        }

        if (TryGetCachedBrush(color, out var cachedBrush))
        {
            return cachedBrush;
        }

        var brush = CreateBrush(color);
        AddCachedBrush(color, brush);
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryGetCachedBrush(string key, out WpfBrush brush)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var node))
            {
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);
                brush = node.Value.Brush;
                return true;
            }
        }

        brush = null!;
        return false;
    }

    private static void AddCachedBrush(string key, WpfBrush brush)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var existingNode))
            {
                CacheOrder.Remove(existingNode);
                Cache.Remove(existingNode.Value.Key);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, brush));
            CacheOrder.AddFirst(node);
            Cache[key] = node;

            while (Cache.Count > MaxCacheEntries && CacheOrder.Last is not null)
            {
                var last = CacheOrder.Last;
                CacheOrder.RemoveLast();
                Cache.Remove(last.Value.Key);
            }
        }
    }

    private static WpfBrush CreateBrush(string key)
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
            Logger.LogWarning(ex, "Failed to convert color string '{Color}' to a WPF brush.", key);
            return WpfBrushes.Transparent;
        }
    }

    private sealed record CacheEntry(string Key, WpfBrush Brush);
}
