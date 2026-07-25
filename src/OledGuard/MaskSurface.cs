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

        clearGeometry =
            mouseGeometry;

        foreach (var region in
                 _regions)
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
            _regions
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
