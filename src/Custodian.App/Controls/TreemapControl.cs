using System.Collections;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Custodian.App.Services;
using Custodian.Core.Presentation;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace Custodian.App.Controls;

/// <summary>
/// Squarified treemap renderer. Pure WPF <see cref="OnRender"/> drawing, no dependencies.
/// Reuses <see cref="ChartSlice"/> data and the same SliceSelected / SliceDoubleClicked events
/// as <see cref="PieChartControl"/> so grid sync works for free.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices),
        typeof(IEnumerable),
        typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSlicesChanged));

    public static readonly DependencyProperty SelectedSliceProperty = DependencyProperty.Register(
        nameof(SelectedSlice),
        typeof(ChartSlice),
        typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly WpfFontFamily LabelFontFamily = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Typeface LabelTypeface = new(LabelFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBoldLabelTypeface = new(LabelFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly LinearGradientBrush TileHighlightBrush = CreateTileHighlightBrush();
    private static readonly ConcurrentDictionary<string, WpfBrush> TileTextBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private const double LayoutEpsilon = 1e-6;
    private readonly List<RenderedTile> _tiles = [];
    private readonly List<ChartSlice> _slices = [];
    private INotifyCollectionChanged? _sliceNotifications;
    private bool _isControlLoaded;
    private double _totalBytes;
    private WpfBrush _backgroundBrush = WpfBrushes.Transparent;
    private WpfBrush _emptyStateBrush = WpfBrushes.Gray;
    private WpfPen _separatorPen = CreatePen(WpfBrushes.White, 1);
    private WpfPen _selectionPen = CreatePen(WpfBrushes.White, 2.5);

    public IEnumerable? Slices
    {
        get => (IEnumerable?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public ChartSlice? SelectedSlice
    {
        get => (ChartSlice?)GetValue(SelectedSliceProperty);
        set => SetValue(SelectedSliceProperty, value);
    }

    public event EventHandler<ChartSliceEventArgs>? SliceSelected;
    public event EventHandler<ChartSliceEventArgs>? SliceDoubleClicked;

    static TreemapControl()
    {
        FocusableProperty.OverrideMetadata(typeof(TreemapControl), new FrameworkPropertyMetadata(true));
    }

    public TreemapControl()
    {
        Loaded += TreemapControl_Loaded;
        Unloaded += TreemapControl_Unloaded;
    }

    private void TreemapControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isControlLoaded = true;
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        AttachSliceNotifications(Slices as INotifyCollectionChanged);
        RefreshThemeResources();
    }

    private void TreemapControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _isControlLoaded = false;
        DetachSliceNotifications();
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
    }

    private void ThemeManager_ThemeChanged(object? sender, AppTheme e)
    {
        RefreshThemeResources();
        InvalidateVisual();
    }

    private void RefreshThemeResources()
    {
        _backgroundBrush = (WpfBrush?)TryFindResource("SurfaceRaised") ?? WpfBrushes.Transparent;
        _emptyStateBrush = (WpfBrush?)TryFindResource("MutedBrush") ?? WpfBrushes.Gray;
        _separatorPen = CreatePen((WpfBrush?)TryFindResource("SurfaceRaised") ?? WpfBrushes.White, 1);
        _selectionPen = CreatePen((WpfBrush?)TryFindResource("OnAccentBrush") ?? (WpfBrush?)TryFindResource("AccentBrush") ?? WpfBrushes.White, 2.5);
    }

    private static void OnSlicesChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        ((TreemapControl)source).SetSliceSource(e.NewValue as IEnumerable);
    }

    private void SetSliceSource(IEnumerable? slices)
    {
        DetachSliceNotifications();
        if (_isControlLoaded)
        {
            AttachSliceNotifications(slices as INotifyCollectionChanged);
        }

        RefreshSliceCache(slices);
    }

    private void AttachSliceNotifications(INotifyCollectionChanged? notifications)
    {
        if (ReferenceEquals(_sliceNotifications, notifications))
        {
            return;
        }

        DetachSliceNotifications();
        _sliceNotifications = notifications;
        if (_sliceNotifications is not null)
        {
            _sliceNotifications.CollectionChanged += Slices_CollectionChanged;
        }
    }

    private void DetachSliceNotifications()
    {
        if (_sliceNotifications is not null)
        {
            _sliceNotifications.CollectionChanged -= Slices_CollectionChanged;
            _sliceNotifications = null;
        }
    }

    private void Slices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSliceCache(Slices);
        InvalidateVisual();
    }

    private void RefreshSliceCache(IEnumerable? slices)
    {
        _slices.Clear();
        _totalBytes = 0;
        if (slices is null)
        {
            return;
        }

        foreach (var slice in slices.OfType<ChartSlice>())
        {
            if (slice.RawBytes <= 0)
            {
                continue;
            }

            _slices.Add(slice);
            _totalBytes += slice.RawBytes;
        }

        _slices.Sort(static (left, right) =>
        {
            var sizeCompare = right.RawBytes.CompareTo(left.RawBytes);
            return sizeCompare != 0
                ? sizeCompare
                : string.Compare(left.SourceKey, right.SourceKey, StringComparison.Ordinal);
        });
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _tiles.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 4 || height <= 4)
        {
            return;
        }

        // Background plate so the empty area inside the panel matches surface color.
        drawingContext.DrawRectangle(_backgroundBrush, null, new Rect(0, 0, width, height));

        if (_slices.Count == 0 || _totalBytes <= 0)
        {
            DrawEmptyState(drawingContext, width, height);
            return;
        }

        // Squarified treemap layout.
        var bounds = new Rect(0, 0, width, height);
        Squarify(_slices, bounds, _totalBytes);

        foreach (var tile in _tiles)
        {
            DrawTile(drawingContext, tile);
        }
    }

    protected override void OnMouseDown(WpfMouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var tile = TileAt(e.GetPosition(this));
        if (tile is null)
        {
            return;
        }

        SelectedSlice = tile.Slice;
        SliceSelected?.Invoke(this, new ChartSliceEventArgs(tile.Slice));
        if (e.ClickCount > 1)
        {
            SliceDoubleClicked?.Invoke(this, new ChartSliceEventArgs(tile.Slice));
        }
    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        base.OnMouseMove(e);
        var tile = TileAt(e.GetPosition(this));
        Cursor = tile is null ? WpfCursors.Arrow : WpfCursors.Hand;
        ToolTip = tile is null
            ? null
            : $"{tile.Slice.Label}\n{tile.Slice.FormattedSize} ({tile.Slice.PercentText})";
    }

    private RenderedTile? TileAt(WpfPoint p)
    {
        for (var i = _tiles.Count - 1; i >= 0; i--)
        {
            if (_tiles[i].Bounds.Contains(p))
            {
                return _tiles[i];
            }
        }
        return null;
    }

    // ============================================================
    //  Squarified treemap (Bruls, Huijing, van Wijk 2000)
    // ============================================================
    private void Squarify(IList<ChartSlice> children, Rect rect, double total)
    {
        if (children.Count == 0 || rect.Width <= 0 || rect.Height <= 0 || total <= 0)
        {
            return;
        }

        var scale = (rect.Width * rect.Height) / total;
        var remaining = new List<(ChartSlice slice, double area)>(children.Count);
        foreach (var child in children)
        {
            remaining.Add((child, child.RawBytes * scale));
        }

        Layout(remaining, rect);
    }

    private void Layout(IReadOnlyList<(ChartSlice slice, double area)> items, Rect rect)
    {
        var start = 0;
        while (start < items.Count && rect.Width > 0.5 && rect.Height > 0.5)
        {
            var shortSide = Math.Min(rect.Width, rect.Height);
            var rowEnd = start + 1;
            var sum = items[start].area;
            var min = sum;
            var max = sum;
            var bestWorst = Worst(sum, min, max, shortSide);

            while (rowEnd < items.Count)
            {
                var area = items[rowEnd].area;
                var candidateSum = sum + area;
                var candidateMin = Math.Min(min, area);
                var candidateMax = Math.Max(max, area);
                var w = Worst(candidateSum, candidateMin, candidateMax, shortSide);
                if (w > bestWorst)
                {
                    break;
                }

                sum = candidateSum;
                min = candidateMin;
                max = candidateMax;
                bestWorst = w;
                rowEnd++;
            }

            rect = PlaceRow(items, start, rowEnd, sum, rect);
            start = rowEnd;
        }
    }

    private static double Worst(double sum, double min, double max, double shortSide)
    {
        if (shortSide <= LayoutEpsilon) return double.PositiveInfinity;
        if (sum <= LayoutEpsilon || min <= LayoutEpsilon)
        {
            return double.PositiveInfinity;
        }

        var s2 = shortSide * shortSide;
        if (s2 <= LayoutEpsilon)
        {
            return double.PositiveInfinity;
        }

        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private Rect PlaceRow(
        IReadOnlyList<(ChartSlice slice, double area)> items,
        int start,
        int end,
        double sum,
        Rect rect)
    {
        if (sum <= 0) return rect;

        if (rect.Width >= rect.Height)
        {
            // Lay row top-to-bottom along the left edge, advance rect.X to the right.
            var rowWidth = sum / rect.Height;
            var y = rect.Y;
            for (var i = start; i < end; i++)
            {
                var item = items[i];
                var h = item.area / rowWidth;
                _tiles.Add(new RenderedTile(item.slice, new Rect(rect.X, y, rowWidth, h)));
                y += h;
            }
            return new Rect(rect.X + rowWidth, rect.Y, Math.Max(0, rect.Width - rowWidth), rect.Height);
        }
        else
        {
            // Lay row left-to-right along the top edge, advance rect.Y downward.
            var rowHeight = sum / rect.Width;
            var x = rect.X;
            for (var i = start; i < end; i++)
            {
                var item = items[i];
                var w = item.area / rowHeight;
                _tiles.Add(new RenderedTile(item.slice, new Rect(x, rect.Y, w, rowHeight)));
                x += w;
            }
            return new Rect(rect.X, rect.Y + rowHeight, rect.Width, Math.Max(0, rect.Height - rowHeight));
        }
    }

    // ============================================================
    //  Drawing
    // ============================================================
    private void DrawTile(DrawingContext dc, RenderedTile tile)
    {
        var rect = Shrink(tile.Bounds, 1.5);
        if (rect.Width < 1 || rect.Height < 1) return;

        var fill = ResolveBrush(tile.Slice.Color);
        var isSelected = SelectedSlice is not null && string.Equals(SelectedSlice.SourceKey, tile.Slice.SourceKey, StringComparison.Ordinal);

        dc.DrawRoundedRectangle(fill, null, rect, 3, 3);

        // Slight inner highlight along the top for depth.
        if (rect.Height > 16 && rect.Width > 16)
        {
            var highlightHeight = Math.Min(rect.Height * 0.18, 14);
            dc.DrawRectangle(TileHighlightBrush, null, new Rect(rect.X, rect.Y, rect.Width, highlightHeight));
        }

        // Selection ring
        if (isSelected)
        {
            var inner = Shrink(rect, 1.5);
            if (inner.Width > 0 && inner.Height > 0)
            {
                dc.DrawRoundedRectangle(null, _selectionPen, inner, 2.5, 2.5);
            }
        }

        dc.DrawRoundedRectangle(null, _separatorPen, rect, 3, 3);

        // Label
        if (rect.Width >= 70 && rect.Height >= 28)
        {
            DrawLabel(dc, rect, tile.Slice);
        }
        else if (rect.Width >= 38 && rect.Height >= 18)
        {
            DrawCompactLabel(dc, rect, tile.Slice);
        }
    }

    private void DrawLabel(DrawingContext dc, Rect rect, ChartSlice slice)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var primary = new FormattedText(
            slice.Label,
            CultureInfo.CurrentCulture,
            WpfFlowDirection.LeftToRight,
            SemiBoldLabelTypeface,
            12.5, OnTileTextBrush(slice.Color),
            dpi)
        {
            MaxTextWidth = Math.Max(0, rect.Width - 16),
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis
        };

        var secondary = new FormattedText(
            $"{slice.FormattedSize} · {slice.PercentText}",
            CultureInfo.CurrentCulture,
            WpfFlowDirection.LeftToRight,
            LabelTypeface,
            10.5, OnTileTextBrush(slice.Color, secondary: true),
            dpi)
        {
            MaxTextWidth = Math.Max(0, rect.Width - 16),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };

        var totalHeight = primary.Height + secondary.Height + 2;
        if (totalHeight > rect.Height - 10)
        {
            dc.DrawText(primary, new WpfPoint(rect.X + 8, rect.Y + 6));
            return;
        }

        dc.DrawText(primary, new WpfPoint(rect.X + 8, rect.Y + 6));
        dc.DrawText(secondary, new WpfPoint(rect.X + 8, rect.Y + 6 + primary.Height + 1));
    }

    private void DrawCompactLabel(DrawingContext dc, Rect rect, ChartSlice slice)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var t = new FormattedText(
            slice.ShortLabel,
            CultureInfo.CurrentCulture,
            WpfFlowDirection.LeftToRight,
            SemiBoldLabelTypeface,
            10.5, OnTileTextBrush(slice.Color),
            dpi)
        {
            MaxTextWidth = Math.Max(0, rect.Width - 6),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(t, new WpfPoint(rect.X + 4, rect.Y + 3));
    }

    private void DrawEmptyState(DrawingContext dc, double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            "No data to visualize",
            CultureInfo.CurrentCulture,
            WpfFlowDirection.LeftToRight,
            SemiBoldLabelTypeface,
            13, _emptyStateBrush, dpi);
        dc.DrawText(text, new WpfPoint((width - text.Width) / 2, (height - text.Height) / 2));
    }

    private static WpfBrush OnTileTextBrush(string color, bool secondary = false)
        => TileTextBrushCache.GetOrAdd((secondary ? "1|" : "0|") + color, _ => CreateTileTextBrush(color, secondary));

    private static WpfBrush CreateTileTextBrush(string color, bool secondary)
    {
        var source = ResolveBrush(color) as SolidColorBrush;
        var c = source?.Color ?? WpfColor.FromRgb(59, 130, 246);
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        var on = luminance > 0.62
            ? WpfColor.FromArgb((byte)(secondary ? 200 : 255), 15, 23, 42)
            : WpfColor.FromArgb((byte)(secondary ? 200 : 255), 255, 255, 255);
        var brush = new SolidColorBrush(on);
        brush.Freeze();
        return brush;
    }

    private static WpfBrush ResolveBrush(string color)
        => ColorStringToBrushConverter.Instance.Convert(color, typeof(WpfBrush), null!, CultureInfo.InvariantCulture) as WpfBrush
           ?? WpfBrushes.Gray;

    private static Rect Shrink(Rect r, double by)
    {
        var x = r.X + by;
        var y = r.Y + by;
        var w = Math.Max(0, r.Width - by * 2);
        var h = Math.Max(0, r.Height - by * 2);
        return new Rect(x, y, w, h);
    }

    private static LinearGradientBrush CreateTileHighlightBrush()
    {
        var brush = new LinearGradientBrush(
            WpfColor.FromArgb(48, 255, 255, 255),
            WpfColor.FromArgb(0, 255, 255, 255),
            new WpfPoint(0, 0),
            new WpfPoint(0, 1));
        brush.Freeze();
        return brush;
    }

    private static WpfPen CreatePen(WpfBrush brush, double thickness)
    {
        var pen = new WpfPen(brush, thickness) { LineJoin = PenLineJoin.Round };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private sealed record RenderedTile(ChartSlice Slice, Rect Bounds);
}
