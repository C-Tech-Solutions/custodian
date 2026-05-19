using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Custodian.Core.Presentation;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
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
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedSliceProperty = DependencyProperty.Register(
        nameof(SelectedSlice),
        typeof(ChartSlice),
        typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly List<RenderedTile> _tiles = [];

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
        var bg = (WpfBrush?)TryFindResource("SurfaceRaised") ?? WpfBrushes.Transparent;
        drawingContext.DrawRectangle(bg, null, new Rect(0, 0, width, height));

        var slices = (Slices?.OfType<ChartSlice>() ?? []).Where(s => s.RawBytes > 0).ToList();
        if (slices.Count == 0)
        {
            DrawEmptyState(drawingContext, width, height);
            return;
        }

        // Squarified treemap layout.
        var bounds = new Rect(0, 0, width, height);
        var total = slices.Sum(s => (double)s.RawBytes);
        var sorted = slices.OrderByDescending(s => s.RawBytes).ToList();
        Squarify(sorted, bounds, total);

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
        var remaining = children.Select(c => (slice: c, area: c.RawBytes * scale)).ToList();
        Layout(remaining, rect);
    }

    private void Layout(List<(ChartSlice slice, double area)> items, Rect rect)
    {
        while (items.Count > 0 && rect.Width > 0.5 && rect.Height > 0.5)
        {
            var shortSide = Math.Min(rect.Width, rect.Height);
            var row = new List<(ChartSlice slice, double area)> { items[0] };
            var rowIndex = 1;
            var bestWorst = Worst(row, shortSide);

            while (rowIndex < items.Count)
            {
                var candidate = new List<(ChartSlice slice, double area)>(row) { items[rowIndex] };
                var w = Worst(candidate, shortSide);
                if (w > bestWorst)
                {
                    break;
                }
                row = candidate;
                bestWorst = w;
                rowIndex++;
            }

            rect = PlaceRow(row, rect);
            items.RemoveRange(0, row.Count);
        }
    }

    private static double Worst(IReadOnlyList<(ChartSlice slice, double area)> row, double shortSide)
    {
        if (row.Count == 0) return double.PositiveInfinity;
        var sum = row.Sum(r => r.area);
        var max = row.Max(r => r.area);
        var min = row.Min(r => r.area);
        var s2 = shortSide * shortSide;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private Rect PlaceRow(IReadOnlyList<(ChartSlice slice, double area)> row, Rect rect)
    {
        var sum = row.Sum(r => r.area);
        if (sum <= 0) return rect;

        if (rect.Width >= rect.Height)
        {
            // Lay row top-to-bottom along the left edge, advance rect.X to the right.
            var rowWidth = sum / rect.Height;
            var y = rect.Y;
            foreach (var item in row)
            {
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
            foreach (var item in row)
            {
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
        var separatorBrush = (WpfBrush?)TryFindResource("SurfaceRaised") ?? WpfBrushes.White;

        // Filled rectangle
        var geometry = new RectangleGeometry(rect, 3, 3);
        geometry.Freeze();
        dc.DrawGeometry(fill, null, geometry);

        // Slight inner highlight along the top for depth.
        if (rect.Height > 16 && rect.Width > 16)
        {
            var highlightHeight = Math.Min(rect.Height * 0.18, 14);
            var highlight = new LinearGradientBrush(
                WpfColor.FromArgb(48, 255, 255, 255),
                WpfColor.FromArgb(0, 255, 255, 255),
                new WpfPoint(0, 0), new WpfPoint(0, 1));
            highlight.Freeze();
            dc.DrawRectangle(highlight, null, new Rect(rect.X, rect.Y, rect.Width, highlightHeight));
        }

        // Selection ring
        if (isSelected)
        {
            var ringBrush = (WpfBrush?)TryFindResource("OnAccentBrush") ?? WpfBrushes.White;
            var pen = new WpfPen(ringBrush, 2.5) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var inner = Shrink(rect, 1.5);
            if (inner.Width > 0 && inner.Height > 0)
            {
                dc.DrawGeometry(null, pen, new RectangleGeometry(inner, 2.5, 2.5));
            }
        }

        // Separator
        var separatorPen = new WpfPen(separatorBrush, 1);
        separatorPen.Freeze();
        dc.DrawGeometry(null, separatorPen, geometry);

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
            new Typeface(new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
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
            new Typeface(new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
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
            new Typeface(new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
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
        var brush = (WpfBrush?)TryFindResource("MutedBrush") ?? WpfBrushes.Gray;
        var text = new FormattedText(
            "No data to visualize",
            CultureInfo.CurrentCulture,
            WpfFlowDirection.LeftToRight,
            new Typeface(new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            13, brush, dpi);
        dc.DrawText(text, new WpfPoint((width - text.Width) / 2, (height - text.Height) / 2));
    }

    private static WpfBrush OnTileTextBrush(string color, bool secondary = false)
    {
        // Pick white or near-black based on perceived luminance.
        try
        {
            var c = (WpfColor)WpfColorConverter.ConvertFromString(color);
            var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            var on = luminance > 0.62
                ? WpfColor.FromArgb((byte)(secondary ? 200 : 255), 15, 23, 42)
                : WpfColor.FromArgb((byte)(secondary ? 200 : 255), 255, 255, 255);
            var b = new SolidColorBrush(on);
            b.Freeze();
            return b;
        }
        catch
        {
            return WpfBrushes.White;
        }
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

    private sealed record RenderedTile(ChartSlice Slice, Rect Bounds);
}
