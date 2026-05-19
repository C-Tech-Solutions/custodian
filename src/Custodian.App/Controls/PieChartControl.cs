using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Custodian.Core.Presentation;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Custodian.App.Controls;

public sealed class PieChartControl : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices),
        typeof(IEnumerable),
        typeof(PieChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSlicesChanged));

    public static readonly DependencyProperty SelectedSliceProperty = DependencyProperty.Register(
        nameof(SelectedSlice),
        typeof(ChartSlice),
        typeof(PieChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface NormalTypeface = new(new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBoldTypeface = new(new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly List<RenderedSlice> _renderedSlices = [];
    private readonly List<ChartSlice> _slices = [];
    private INotifyCollectionChanged? _sliceNotifications;
    private long _totalBytes;

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

    private static void OnSlicesChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        ((PieChartControl)source).SetSliceSource(e.NewValue as IEnumerable);
    }

    private void SetSliceSource(IEnumerable? slices)
    {
        if (_sliceNotifications is not null)
        {
            _sliceNotifications.CollectionChanged -= Slices_CollectionChanged;
        }

        _sliceNotifications = slices as INotifyCollectionChanged;
        if (_sliceNotifications is not null)
        {
            _sliceNotifications.CollectionChanged += Slices_CollectionChanged;
        }

        RefreshSliceCache(slices);
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
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _renderedSlices.Clear();

        if (_slices.Count == 0 || _totalBytes <= 0)
        {
            DrawEmptyState(drawingContext);
            return;
        }

        var size = Math.Min(ActualWidth, ActualHeight);
        var radius = Math.Max(0, size / 2 - 52);
        if (radius <= 0)
        {
            return;
        }

        var innerRadius = radius * 0.58;
        var center = new WpfPoint(ActualWidth / 2, ActualHeight / 2);
        var startAngle = 0.0;
        var separatorPen = CreatePen((WpfBrush?)TryFindResource("Border") ?? System.Windows.Media.Brushes.White, 1);
        var selectedPen = CreatePen(System.Windows.Media.Brushes.White, 3);

        foreach (var slice in _slices)
        {
            var sweep = Math.Max(0.4, (double)slice.RawBytes / _totalBytes * 360);
            var endAngle = startAngle + sweep;
            var brush = ResolveBrush(slice.Color);

            var isSelected = SelectedSlice is not null && string.Equals(SelectedSlice.SourceKey, slice.SourceKey, StringComparison.Ordinal);
            var pen = isSelected ? selectedPen : separatorPen;

            drawingContext.DrawGeometry(brush, pen, BuildSliceGeometry(center, radius, innerRadius, startAngle, endAngle));
            _renderedSlices.Add(new RenderedSlice(slice, startAngle, endAngle));
            startAngle = endAngle;
        }

        drawingContext.DrawEllipse(System.Windows.Media.Brushes.White, null, center, innerRadius - 2, innerRadius - 2);
        foreach (var rendered in _renderedSlices)
        {
            if (rendered.Slice.ShowCallout)
            {
                DrawCallout(drawingContext, center, radius, rendered);
            }
        }

        DrawCenterText(drawingContext, center, _slices.Count);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var slice = SliceAt(e.GetPosition(this));
        if (slice is null)
        {
            return;
        }

        SelectedSlice = slice;
        SliceSelected?.Invoke(this, new ChartSliceEventArgs(slice));
        if (e.ClickCount > 1)
        {
            SliceDoubleClicked?.Invoke(this, new ChartSliceEventArgs(slice));
        }

    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        base.OnMouseMove(e);
        var slice = SliceAt(e.GetPosition(this));
        Cursor = slice is null ? WpfCursors.Arrow : WpfCursors.Hand;
        ToolTip = slice is null ? null : $"{slice.Label}\n{slice.FormattedSize} ({slice.PercentText})";
    }

    private static Geometry BuildSliceGeometry(WpfPoint center, double outerRadius, double innerRadius, double startAngle, double endAngle)
    {
        var sweep = endAngle - startAngle;
        var largeArc = sweep > 180;
        var outerStart = PointAt(center, outerRadius, startAngle);
        var outerEnd = PointAt(center, outerRadius, endAngle);
        var innerEnd = PointAt(center, innerRadius, endAngle);
        var innerStart = PointAt(center, innerRadius, startAngle);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(outerStart, true, true);
            context.ArcTo(outerEnd, new WpfSize(outerRadius, outerRadius), 0, largeArc, SweepDirection.Clockwise, true, false);
            context.LineTo(innerEnd, true, false);
            context.ArcTo(innerStart, new WpfSize(innerRadius, innerRadius), 0, largeArc, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private ChartSlice? SliceAt(WpfPoint point)
    {
        if (_renderedSlices.Count == 0)
        {
            return null;
        }

        var size = Math.Min(ActualWidth, ActualHeight);
        var radius = Math.Max(0, size / 2 - 52);
        var innerRadius = radius * 0.58;
        var center = new WpfPoint(ActualWidth / 2, ActualHeight / 2);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < innerRadius || distance > radius)
        {
            return null;
        }

        var angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        return _renderedSlices.FirstOrDefault(rendered => angle >= rendered.StartAngle && angle < rendered.EndAngle)?.Slice;
    }

    private static WpfPoint PointAt(WpfPoint center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new WpfPoint(
            center.X + radius * Math.Sin(radians),
            center.Y - radius * Math.Cos(radians));
    }

    private void DrawEmptyState(DrawingContext drawingContext)
    {
        var center = new WpfPoint(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 16);
        drawingContext.DrawEllipse(null, new WpfPen(new SolidColorBrush(WpfColor.FromRgb(203, 213, 225)), 1), center, radius, radius);
        DrawText(drawingContext, "No chart data", center, 13, FontWeights.SemiBold, WpfColor.FromRgb(100, 116, 139));
    }

    private void DrawCenterText(DrawingContext drawingContext, WpfPoint center, int count)
    {
        DrawText(drawingContext, $"{count}", new WpfPoint(center.X, center.Y - 8), 20, FontWeights.SemiBold, WpfColor.FromRgb(15, 23, 42));
        DrawText(drawingContext, "items", new WpfPoint(center.X, center.Y + 14), 11, FontWeights.Normal, WpfColor.FromRgb(100, 116, 139));
    }

    private void DrawCallout(DrawingContext drawingContext, WpfPoint center, double radius, RenderedSlice rendered)
    {
        var midpoint = rendered.StartAngle + (rendered.EndAngle - rendered.StartAngle) / 2;
        var lineStart = PointAt(center, radius + 2, midpoint);
        var lineEnd = PointAt(center, radius + 16, midpoint);
        var labelPoint = PointAt(center, radius + 20, midpoint);
        var text = $"{rendered.Slice.ShortLabel} {rendered.Slice.PercentText}";
        var color = WpfColor.FromRgb(51, 65, 85);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new WpfPen(brush, 1);
        pen.Freeze();

        drawingContext.DrawLine(pen, lineStart, lineEnd);

        var isLeftSide = midpoint is > 180 and < 360;
        const double EdgePadding = 4;
        var maxWidth = Math.Max(20, isLeftSide
            ? labelPoint.X - EdgePadding
            : ActualWidth - labelPoint.X - EdgePadding);

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            SemiBoldTypeface,
            10,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };

        var x = isLeftSide ? labelPoint.X - formatted.Width : labelPoint.X;
        var y = labelPoint.Y - formatted.Height / 2;
        drawingContext.DrawText(formatted, new WpfPoint(x, y));
    }

    private void DrawText(DrawingContext drawingContext, string text, WpfPoint center, double size, FontWeight weight, WpfColor color)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            weight == FontWeights.SemiBold ? SemiBoldTypeface : NormalTypeface,
            size,
            new SolidColorBrush(color),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(formatted, new WpfPoint(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static WpfBrush ResolveBrush(string color)
        => ColorStringToBrushConverter.Instance.Convert(color, typeof(WpfBrush), null, CultureInfo.InvariantCulture) as WpfBrush
           ?? System.Windows.Media.Brushes.Transparent;

    private static WpfPen CreatePen(WpfBrush brush, double thickness)
    {
        var pen = new WpfPen(brush, thickness);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private sealed record RenderedSlice(ChartSlice Slice, double StartAngle, double EndAngle);
}
