using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace OledGuard;

internal sealed class MaskSurface : FrameworkElement
{
    private MaskRegion[] _regions =
        Array.Empty<MaskRegion>();
    private MouseReveal[] _mouseReveals =
        Array.Empty<MouseReveal>();
    private double _maximumOpacity;

    public MaskSurface()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public void UpdateScene(
        double maximumOpacity,
        IReadOnlyList<MaskRegion> regions,
        IReadOnlyList<MouseReveal> mouseReveals)
    {
        _maximumOpacity =
            Math.Clamp(
                maximumOpacity,
                0.0,
                1.0);
        _regions =
            regions.Count == 0
                ? Array.Empty<MaskRegion>()
                : regions.ToArray();
        _mouseReveals =
            mouseReveals.Count == 0
                ? Array.Empty<MouseReveal>()
                : mouseReveals.ToArray();

        InvalidateVisual();
    }

    protected override void OnRender(
        DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 0 ||
            ActualHeight <= 0 ||
            _maximumOpacity <= 0.0001)
        {
            return;
        }

        var outer =
            new RectangleGeometry(
                new Rect(
                    0,
                    0,
                    ActualWidth,
                    ActualHeight));

        Geometry? allRegionGeometry = null;
        Geometry? clearGeometry = null;
        Geometry? mouseGeometry = null;

        foreach (var reveal in
                 _mouseReveals)
        {
            var ellipse =
                CreateMouseGeometry(reveal);

            if (ellipse is null)
            {
                continue;
            }

            mouseGeometry =
                Union(
                    mouseGeometry,
                    ellipse);
        }

        // The cached normalized cursor can be offset on a DPI-scaled
        // display. Add one live WPF-coordinate hole at render time so the
        // pixels directly under the visible pointer are always clear.
        var liveCursorGeometry =
            CreateLiveCursorGeometry();
        if (liveCursorGeometry is not null)
        {
            mouseGeometry =
                Union(
                    mouseGeometry,
                    liveCursorGeometry);
        }

        clearGeometry =
            mouseGeometry;

        // These are narrow visual bridges only. Logical tracked regions,
        // recurrence, shrinking and interaction completion remain untouched.
        var renderRegions =
            BuildRenderRegionsWithNarrowBridges();

        foreach (var region in
                 renderRegions)
        {
            if (region.Opacity >=
                _maximumOpacity -
                0.0001)
            {
                continue;
            }

            var rectangle =
                CreateRectangleGeometry(
                    region.NormalizedBounds);

            if (rectangle is null)
            {
                continue;
            }

            allRegionGeometry =
                Union(
                    allRegionGeometry,
                    rectangle);

            if (region.Opacity <= 0.0001)
            {
                clearGeometry =
                    Union(
                        clearGeometry,
                        rectangle);
            }
        }

        var everyHole =
            Union(
                allRegionGeometry,
                mouseGeometry);

        Geometry outside =
            everyHole is null
                ? outer
                : new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    outer,
                    everyHole);

        drawingContext.DrawGeometry(
            CreateBlackBrush(
                _maximumOpacity),
            null,
            outside);

        var opacityGroups =
            renderRegions
                .Where(
                    region =>
                        region.Opacity > 0.0001 &&
                        region.Opacity <
                            _maximumOpacity -
                            0.0001)
                .GroupBy(
                    region =>
                        Math.Round(
                            region.Opacity,
                            4))
                .OrderBy(
                    group =>
                        group.Key);

        var lowerOpacityGeometry =
            clearGeometry;

        foreach (var group in
                 opacityGroups)
        {
            Geometry? groupGeometry = null;

            foreach (var region in
                     group)
            {
                var rectangle =
                    CreateRectangleGeometry(
                        region.NormalizedBounds);

                if (rectangle is null)
                {
                    continue;
                }

                groupGeometry =
                    Union(
                        groupGeometry,
                        rectangle);
            }

            if (groupGeometry is null)
            {
                continue;
            }

            Geometry drawable =
                lowerOpacityGeometry is null
                    ? groupGeometry
                    : new CombinedGeometry(
                        GeometryCombineMode.Exclude,
                        groupGeometry,
                        lowerOpacityGeometry);

            drawingContext.DrawGeometry(
                CreateBlackBrush(
                    Math.Clamp(
                        group.Key,
                        0.0,
                        _maximumOpacity)),
                null,
                drawable);

            lowerOpacityGeometry =
                Union(
                    lowerOpacityGeometry,
                    groupGeometry);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(
        out NativeCursorPoint point);

    private Geometry? CreateLiveCursorGeometry()
    {
        if (!GetCursorPos(out var cursor))
        {
            return null;
        }

        System.Windows.Point local;
        try
        {
            local = PointFromScreen(
                new System.Windows.Point(
                    cursor.X,
                    cursor.Y));
        }
        catch
        {
            return null;
        }

        if (local.X < 0.0 ||
            local.Y < 0.0 ||
            local.X > ActualWidth ||
            local.Y > ActualHeight)
        {
            return null;
        }

        var radiusX = 24.0;
        var radiusY = 22.0;

        // Keep the configured radius when it is larger. The live position is
        // the important part; this does not modify the stored trail.
        if (_mouseReveals.Length > 0)
        {
            radiusX = Math.Max(
                radiusX,
                _mouseReveals[0].NormalizedRadiusX * ActualWidth);
            radiusY = Math.Max(
                radiusY,
                _mouseReveals[0].NormalizedRadiusY * ActualHeight);
        }

        return new EllipseGeometry(
            local,
            radiusX,
            radiusY);
    }

    private MaskRegion[] BuildRenderRegionsWithNarrowBridges()
    {
        if (_regions.Length < 2 ||
            ActualWidth <= 0.0 ||
            ActualHeight <= 0.0)
        {
            return _regions;
        }

        var result =
            new List<MaskRegion>(
                _regions.Length +
                Math.Min(192, _regions.Length * 2));
        result.AddRange(_regions);

        const double maximumHorizontalGapPixels = 30.0;
        const double maximumVerticalGapPixels = 22.0;
        const double minimumAlignment = 0.62;
        const double maximumOpacityDifference = 0.20;
        var maximumBridgeCount =
            Math.Min(
                192,
                Math.Max(24, _regions.Length * 2));
        var bridgeCount = 0;

        for (var firstIndex = 0;
             firstIndex < _regions.Length;
             firstIndex++)
        {
            var first = _regions[firstIndex];
            var firstBounds = first.NormalizedBounds;
            var firstLeft = firstBounds.Left * ActualWidth;
            var firstTop = firstBounds.Top * ActualHeight;
            var firstRight = firstBounds.Right * ActualWidth;
            var firstBottom = firstBounds.Bottom * ActualHeight;
            var firstWidth = firstRight - firstLeft;
            var firstHeight = firstBottom - firstTop;

            if (firstWidth < 12.0 || firstHeight < 12.0)
            {
                continue;
            }

            for (var secondIndex = firstIndex + 1;
                 secondIndex < _regions.Length;
                 secondIndex++)
            {
                var second = _regions[secondIndex];
                if (Math.Abs(first.Opacity - second.Opacity) >
                    maximumOpacityDifference)
                {
                    continue;
                }

                var secondBounds = second.NormalizedBounds;
                var secondLeft = secondBounds.Left * ActualWidth;
                var secondTop = secondBounds.Top * ActualHeight;
                var secondRight = secondBounds.Right * ActualWidth;
                var secondBottom = secondBounds.Bottom * ActualHeight;
                var secondWidth = secondRight - secondLeft;
                var secondHeight = secondBottom - secondTop;

                if (secondWidth < 12.0 || secondHeight < 12.0)
                {
                    continue;
                }

                var overlapWidth = Math.Max(
                    0.0,
                    Math.Min(firstRight, secondRight) -
                    Math.Max(firstLeft, secondLeft));
                var overlapHeight = Math.Max(
                    0.0,
                    Math.Min(firstBottom, secondBottom) -
                    Math.Max(firstTop, secondTop));

                var horizontalGap = Math.Max(
                    0.0,
                    Math.Max(firstLeft, secondLeft) -
                    Math.Min(firstRight, secondRight));
                var verticalGap = Math.Max(
                    0.0,
                    Math.Max(firstTop, secondTop) -
                    Math.Min(firstBottom, secondBottom));

                Rect? bridge = null;

                if (horizontalGap > 0.0 &&
                    horizontalGap <= maximumHorizontalGapPixels &&
                    overlapHeight >=
                        Math.Min(firstHeight, secondHeight) * minimumAlignment)
                {
                    var left = Math.Min(firstRight, secondRight);
                    var right = Math.Max(firstLeft, secondLeft);
                    var top = Math.Max(firstTop, secondTop);
                    var bottom = Math.Min(firstBottom, secondBottom);
                    bridge = new Rect(
                        left / ActualWidth,
                        top / ActualHeight,
                        (right - left) / ActualWidth,
                        (bottom - top) / ActualHeight);
                }
                else if (verticalGap > 0.0 &&
                         verticalGap <= maximumVerticalGapPixels &&
                         overlapWidth >=
                            Math.Min(firstWidth, secondWidth) * minimumAlignment)
                {
                    var left = Math.Max(firstLeft, secondLeft);
                    var right = Math.Min(firstRight, secondRight);
                    var top = Math.Min(firstBottom, secondBottom);
                    var bottom = Math.Max(firstTop, secondTop);
                    bridge = new Rect(
                        left / ActualWidth,
                        top / ActualHeight,
                        (right - left) / ActualWidth,
                        (bottom - top) / ActualHeight);
                }

                if (bridge is null ||
                    bridge.Value.Width <= 0.0 ||
                    bridge.Value.Height <= 0.0)
                {
                    continue;
                }

                result.Add(
                    new MaskRegion(
                        bridge.Value,
                        Math.Min(first.Opacity, second.Opacity)));
                bridgeCount++;

                if (bridgeCount >= maximumBridgeCount)
                {
                    return result.ToArray();
                }
            }
        }

        return result.ToArray();
    }

    private Geometry? CreateRectangleGeometry(
        Rect normalized)
    {
        var left =
            Math.Floor(
                Math.Clamp(
                    normalized.Left,
                    0.0,
                    1.0) *
                ActualWidth);
        var top =
            Math.Floor(
                Math.Clamp(
                    normalized.Top,
                    0.0,
                    1.0) *
                ActualHeight);
        var right =
            Math.Ceiling(
                Math.Clamp(
                    normalized.Right,
                    0.0,
                    1.0) *
                ActualWidth);
        var bottom =
            Math.Ceiling(
                Math.Clamp(
                    normalized.Bottom,
                    0.0,
                    1.0) *
                ActualHeight);

        const double seamBleed = 1.0;
        left = Math.Max(0.0, left - seamBleed);
        top = Math.Max(0.0, top - seamBleed);
        right = Math.Min(ActualWidth, right + seamBleed);
        bottom = Math.Min(ActualHeight, bottom + seamBleed);

        if (right <= left ||
            bottom <= top)
        {
            return null;
        }

        return new RectangleGeometry(
            new Rect(
                left,
                top,
                right - left,
                bottom - top));
    }

    private Geometry? CreateMouseGeometry(
        MouseReveal reveal)
    {
        var x =
            reveal.NormalizedPosition.X *
            ActualWidth;
        var y =
            reveal.NormalizedPosition.Y *
            ActualHeight;
        var radiusX =
            reveal.NormalizedRadiusX *
            ActualWidth;
        var radiusY =
            reveal.NormalizedRadiusY *
            ActualHeight;

        if (radiusX <= 0.1 ||
            radiusY <= 0.1)
        {
            return null;
        }

        return new EllipseGeometry(
            new System.Windows.Point(
                x,
                y),
            radiusX,
            radiusY);
    }

    private static Geometry? Union(
        Geometry? current,
        Geometry? addition)
    {
        if (current is null)
        {
            return addition;
        }

        if (addition is null)
        {
            return current;
        }

        return new CombinedGeometry(
            GeometryCombineMode.Union,
            current,
            addition);
    }

    private static System.Windows.Media.Brush CreateBlackBrush(
        double opacity)
    {
        var brush =
            new SolidColorBrush(
                Colors.Black)
            {
                Opacity =
                    Math.Clamp(
                        opacity,
                        0.0,
                        1.0)
            };

        brush.Freeze();
        return brush;
    }
}
