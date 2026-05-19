using System.Collections;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Custodian.Core.Presentation;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
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
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedSliceProperty = DependencyProperty.Register(
        nameof(SelectedSlice),
        typeof(ChartSlice),
        typeof(PieChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly List<RenderedSlice> _renderedSlices = [];

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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _renderedSlices.Clear();

        var slices = Slices?.OfType<ChartSlice>().Where(slice => slice.RawBytes > 0).ToList() ?? [];
        if (slices.Count == 0)
        {
            DrawEmptyState(drawingContext);
            return;
        }

        var size = Math.Min(ActualWidth, ActualHeight);
        var radius = Math.Max(0, size / 2 - 40);
        if (radius <= 0)
        {
            return;
        }

        var innerRadius = radius * 0.58;
        var center = new WpfPoint(ActualWidth / 2, ActualHeight / 2);
        var total = slices.Sum(slice => slice.RawBytes);
        var startAngle = 0.0;

        foreach (var slice in slices)
        {
            var sweep = Math.Max(0.4, (double)slice.RawBytes / total * 360);
            var endAngle = startAngle + sweep;
            var brush = new SolidColorBrush(ParseColor(slice.Color));
            brush.Freeze();

            var isSelected = SelectedSlice is not null && string.Equals(SelectedSlice.SourceKey, slice.SourceKey, StringComparison.Ordinal);
            var pen = isSelected
                ? new WpfPen(System.Windows.Media.Brushes.White, 3)
                : new WpfPen(new SolidColorBrush(WpfColor.FromRgb(248, 250, 252)), 1);
            if (pen.Brush.CanFreeze)
            {
                pen.Brush.Freeze();
            }
            pen.Freeze();

            drawingContext.DrawGeometry(brush, pen, BuildSliceGeometry(center, radius, innerRadius, startAngle, endAngle));
            _renderedSlices.Add(new RenderedSlice(slice, startAngle, endAngle));
            startAngle = endAngle;
        }

        drawingContext.DrawEllipse(System.Windows.Media.Brushes.White, null, center, innerRadius - 2, innerRadius - 2);
        foreach (var rendered in _renderedSlices.Where(slice => slice.Slice.ShowCallout))
        {
            DrawCallout(drawingContext, center, radius, rendered);
        }

        DrawCenterText(drawingContext, center, slices.Count);
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

        CaptureMouse();
        ReleaseMouseCapture();
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
        var radius = Math.Max(0, size / 2 - 40);
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

    private static WpfColor ParseColor(string color)
    {
        try
        {
            return (WpfColor)WpfColorConverter.ConvertFromString(color);
        }
        catch (FormatException)
        {
            return WpfColor.FromRgb(37, 99, 235);
        }
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
        var lineEnd = PointAt(center, radius + 18, midpoint);
        var labelPoint = PointAt(center, radius + 24, midpoint);
        var text = $"{rendered.Slice.ShortLabel} {rendered.Slice.PercentText}";
        var color = WpfColor.FromRgb(51, 65, 85);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new WpfPen(brush, 1);
        pen.Freeze();

        drawingContext.DrawLine(pen, lineStart, lineEnd);

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var x = midpoint is > 180 and < 360 ? labelPoint.X - formatted.Width : labelPoint.X;
        var y = labelPoint.Y - formatted.Height / 2;
        drawingContext.DrawText(formatted, new WpfPoint(x, y));
    }

    private void DrawText(DrawingContext drawingContext, string text, WpfPoint center, double size, FontWeight weight, WpfColor color)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new WpfFontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(formatted, new WpfPoint(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private sealed record RenderedSlice(ChartSlice Slice, double StartAngle, double EndAngle);
}
