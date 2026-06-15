using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Custodian.App.Services;
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
    private const double DefaultZoomFactor = 1.0;
    private const double MinimumZoomFactor = 0.75;
    private const double MaximumZoomFactor = 2.0;
    private const double PanClickTolerance = 3.0;
    private const double PanClickToleranceSquared = PanClickTolerance * PanClickTolerance;
    private const double PanEdgeMargin = 32.0;

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

    public static readonly DependencyProperty ZoomFactorProperty = DependencyProperty.Register(
        nameof(ZoomFactor),
        typeof(double),
        typeof(PieChartControl),
        new FrameworkPropertyMetadata(
            DefaultZoomFactor,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnZoomFactorChanged,
            CoerceZoomFactor));

    private static readonly Typeface NormalTypeface = new(new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBoldTypeface = new(new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly List<RenderedSlice> _renderedSlices = [];
    private readonly List<ChartSlice> _slices = [];
    private INotifyCollectionChanged? _sliceNotifications;
    private bool _isControlLoaded;
    private long _totalBytes;
    private WpfBrush _calloutBrush = CreateBrush(WpfColor.FromRgb(51, 65, 85));
    private WpfPen _calloutPen = CreatePen(CreateBrush(WpfColor.FromRgb(51, 65, 85)), 1);
    private WpfBrush _emptyStateBrush = CreateBrush(WpfColor.FromRgb(100, 116, 139));
    private WpfPen _emptyStatePen = CreatePen(CreateBrush(WpfColor.FromRgb(203, 213, 225)), 1);
    private WpfBrush _centerBrush = System.Windows.Media.Brushes.White;
    private WpfBrush _centerStrongBrush = CreateBrush(WpfColor.FromRgb(15, 23, 42));
    private WpfBrush _centerMutedBrush = CreateBrush(WpfColor.FromRgb(100, 116, 139));
    private WpfPen _separatorPen = CreatePen(System.Windows.Media.Brushes.White, 1);
    private WpfPen _selectedPen = CreatePen(System.Windows.Media.Brushes.White, 3);
    private double _panOffsetX;
    private double _panOffsetY;
    private WpfPoint _panStartPoint;
    private double _panStartOffsetX;
    private double _panStartOffsetY;
    private int _pendingClickCount;
    private bool _isPotentialPanning;
    private bool _isPanning;

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

    public double ZoomFactor
    {
        get => (double)GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    public event EventHandler<ChartSliceEventArgs>? SliceSelected;
    public event EventHandler<ChartSliceEventArgs>? SliceDoubleClicked;

    public PieChartControl()
    {
        Loaded += PieChartControl_Loaded;
        Unloaded += PieChartControl_Unloaded;
    }

    private void PieChartControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isControlLoaded = true;
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        AttachSliceNotifications(Slices as INotifyCollectionChanged);
        RefreshThemeResources();
    }

    private void PieChartControl_Unloaded(object sender, RoutedEventArgs e)
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
        _calloutBrush = (WpfBrush?)TryFindResource("TextStrong") ?? CreateBrush(WpfColor.FromRgb(15, 23, 42));
        _calloutPen = CreatePen((WpfBrush?)TryFindResource("MutedStrong") ?? _calloutBrush, 1.25);
        _emptyStateBrush = (WpfBrush?)TryFindResource("MutedBrush") ?? CreateBrush(WpfColor.FromRgb(100, 116, 139));
        _emptyStatePen = CreatePen((WpfBrush?)TryFindResource("Border") ?? CreateBrush(WpfColor.FromRgb(203, 213, 225)), 1);
        _centerBrush = (WpfBrush?)TryFindResource("SurfaceRaised") ?? System.Windows.Media.Brushes.White;
        _centerStrongBrush = (WpfBrush?)TryFindResource("TextStrong") ?? CreateBrush(WpfColor.FromRgb(15, 23, 42));
        _centerMutedBrush = (WpfBrush?)TryFindResource("MutedBrush") ?? CreateBrush(WpfColor.FromRgb(100, 116, 139));
        _separatorPen = CreatePen((WpfBrush?)TryFindResource("Border") ?? System.Windows.Media.Brushes.White, 1);
        _selectedPen = CreatePen((WpfBrush?)TryFindResource("AccentBrush") ?? System.Windows.Media.Brushes.White, 3);
    }

    private static void OnSlicesChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        ((PieChartControl)source).SetSliceSource(e.NewValue as IEnumerable);
    }

    private static void OnZoomFactorChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        var control = (PieChartControl)source;
        if (!control.IsZoomed)
        {
            control.ResetPanOffset();
            control.CancelPanInteraction();
            if (control.IsMouseCaptured)
            {
                control.ReleaseMouseCapture();
            }

            control.UpdateHover(Mouse.GetPosition(control));
            return;
        }

        control.ClampPanOffset(control.CalculateOuterRadius());
        control.UpdateHover(Mouse.GetPosition(control));
    }

    private static object CoerceZoomFactor(DependencyObject _, object baseValue)
    {
        if (baseValue is not double value || double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultZoomFactor;
        }

        return Math.Clamp(value, MinimumZoomFactor, MaximumZoomFactor);
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

        var radius = CalculateOuterRadius();
        var innerRadius = CalculateInnerRadius(radius);
        if (innerRadius <= 2)
        {
            return;
        }

        var center = CalculateChartCenter();
        var startAngle = 0.0;
        foreach (var slice in _slices)
        {
            var sweep = Math.Max(0.4, (double)slice.RawBytes / _totalBytes * 360);
            var endAngle = startAngle + sweep;
            var brush = ResolveBrush(slice.Color);

            var isSelected = SelectedSlice is not null && string.Equals(SelectedSlice.SourceKey, slice.SourceKey, StringComparison.Ordinal);
            var pen = isSelected ? _selectedPen : _separatorPen;

            drawingContext.DrawGeometry(brush, pen, BuildSliceGeometry(center, radius, innerRadius, startAngle, endAngle));
            _renderedSlices.Add(new RenderedSlice(slice, startAngle, endAngle));
            startAngle = endAngle;
        }

        drawingContext.DrawEllipse(_centerBrush, null, center, innerRadius - 2, innerRadius - 2);
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
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (IsZoomed)
        {
            _panStartPoint = e.GetPosition(this);
            _panStartOffsetX = _panOffsetX;
            _panStartOffsetY = _panOffsetY;
            _pendingClickCount = e.ClickCount;
            _isPotentialPanning = true;
            _isPanning = false;
            CaptureMouse();
            Cursor = WpfCursors.SizeAll;
            e.Handled = true;
            return;
        }

        SelectSliceAt(e.GetPosition(this), e.ClickCount);
    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_isPotentialPanning)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                UpdatePan(point);
                return;
            }

            CancelPanInteraction();
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }

        UpdateHover(point);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left || !_isPotentialPanning)
        {
            return;
        }

        var point = e.GetPosition(this);
        var wasPanning = _isPanning;
        var clickCount = _pendingClickCount;
        CancelPanInteraction();
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (!wasPanning)
        {
            SelectSliceAt(point, clickCount);
        }

        UpdateHover(point);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(WpfMouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelPanInteraction();
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ClampPanOffset(CalculateOuterRadius());
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

        var radius = CalculateOuterRadius();
        var innerRadius = CalculateInnerRadius(radius);
        if (innerRadius <= 2)
        {
            return null;
        }

        var center = CalculateChartCenter();
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

    private double CalculateOuterRadius()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var baseRadius = Math.Max(0, size / 2 - 52);
        return baseRadius * ZoomFactor;
    }

    private WpfPoint CalculateChartCenter()
    {
        return new WpfPoint(ActualWidth / 2 + _panOffsetX, ActualHeight / 2 + _panOffsetY);
    }

    private bool IsZoomed => ZoomFactor > DefaultZoomFactor + 0.001;

    private void UpdatePan(WpfPoint point)
    {
        var deltaX = point.X - _panStartPoint.X;
        var deltaY = point.Y - _panStartPoint.Y;
        if (!_isPanning && deltaX * deltaX + deltaY * deltaY < PanClickToleranceSquared)
        {
            Cursor = WpfCursors.SizeAll;
            ToolTip = null;
            return;
        }

        _isPanning = true;
        _panOffsetX = _panStartOffsetX + deltaX;
        _panOffsetY = _panStartOffsetY + deltaY;
        ClampPanOffset(CalculateOuterRadius());
        Cursor = WpfCursors.SizeAll;
        ToolTip = null;
        InvalidateVisual();
    }

    private void UpdateHover(WpfPoint point)
    {
        var slice = SliceAt(point);
        Cursor = slice is not null ? WpfCursors.Hand : IsZoomed ? WpfCursors.SizeAll : WpfCursors.Arrow;
        ToolTip = slice is null ? null : $"{slice.Label}\n{slice.FormattedSize} ({slice.PercentText})";
    }

    private void SelectSliceAt(WpfPoint point, int clickCount)
    {
        var slice = SliceAt(point);
        if (slice is null)
        {
            return;
        }

        SelectedSlice = slice;
        SliceSelected?.Invoke(this, new ChartSliceEventArgs(slice));
        if (clickCount > 1)
        {
            SliceDoubleClicked?.Invoke(this, new ChartSliceEventArgs(slice));
        }
    }

    private void ClampPanOffset(double radius)
    {
        if (!IsZoomed || radius <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            ResetPanOffset();
            return;
        }

        var maxPanX = Math.Max(0, radius - ActualWidth / 2 + PanEdgeMargin);
        var maxPanY = Math.Max(0, radius - ActualHeight / 2 + PanEdgeMargin);
        _panOffsetX = Math.Clamp(_panOffsetX, -maxPanX, maxPanX);
        _panOffsetY = Math.Clamp(_panOffsetY, -maxPanY, maxPanY);
    }

    private void ResetPanOffset()
    {
        _panOffsetX = 0;
        _panOffsetY = 0;
    }

    private void CancelPanInteraction()
    {
        _isPotentialPanning = false;
        _isPanning = false;
        _pendingClickCount = 0;
    }

    private static double CalculateInnerRadius(double outerRadius) => outerRadius * 0.58;

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
        drawingContext.DrawEllipse(null, _emptyStatePen, center, radius, radius);
        DrawText(drawingContext, "No chart data", center, 13, FontWeights.SemiBold, _emptyStateBrush);
    }

    private void DrawCenterText(DrawingContext drawingContext, WpfPoint center, int count)
    {
        DrawText(drawingContext, $"{count}", new WpfPoint(center.X, center.Y - 8), 20, FontWeights.SemiBold, _centerStrongBrush);
        DrawText(drawingContext, "items", new WpfPoint(center.X, center.Y + 14), 11, FontWeights.Normal, _centerMutedBrush);
    }

    private void DrawCallout(DrawingContext drawingContext, WpfPoint center, double radius, RenderedSlice rendered)
    {
        var midpoint = rendered.StartAngle + (rendered.EndAngle - rendered.StartAngle) / 2;
        var lineStart = PointAt(center, radius + 2, midpoint);
        var lineEnd = PointAt(center, radius + 16, midpoint);
        var labelPoint = PointAt(center, radius + 20, midpoint);
        var text = $"{rendered.Slice.ShortLabel} {rendered.Slice.PercentText}";

        drawingContext.DrawLine(_calloutPen, lineStart, lineEnd);

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
            11.5,
            _calloutBrush,
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

    private void DrawText(DrawingContext drawingContext, string text, WpfPoint center, double size, FontWeight weight, WpfBrush brush)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            weight == FontWeights.SemiBold ? SemiBoldTypeface : NormalTypeface,
            size,
            brush,
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

    private static WpfBrush CreateBrush(WpfColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed record RenderedSlice(ChartSlice Slice, double StartAngle, double EndAngle);
}
