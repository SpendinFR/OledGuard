using System.Windows;
using System.Windows.Media;

namespace OledGuardSimple;

internal readonly record struct RevealRegion(
    Rect NormalizedBounds,
    double Opacity);

internal readonly record struct CursorHole(
    System.Windows.Point NormalizedPosition,
    double NormalizedRadiusX,
    double NormalizedRadiusY);

internal sealed class MaskSurface : FrameworkElement
{
    private RevealRegion[] _regions =
        Array.Empty<RevealRegion>();

    private CursorHole[] _cursorHoles =
        Array.Empty<CursorHole>();

    private double _maximumOpacity;

    public MaskSurface()
    {
        IsHitTestVisible =
            false;

        SnapsToDevicePixels =
            true;

        UseLayoutRounding =
            true;
    }

    public void UpdateScene(
        double maximumOpacity,
        IReadOnlyList<RevealRegion> regions,
        IReadOnlyList<CursorHole> cursorHoles)
    {
        _maximumOpacity =
            Math.Clamp(
                maximumOpacity,
                0.0,
                1.0);

        _regions =
            regions.Count == 0
                ? Array.Empty<RevealRegion>()
                : regions.ToArray();

        _cursorHoles =
            cursorHoles.Count == 0
                ? Array.Empty<CursorHole>()
                : cursorHoles.ToArray();

        InvalidateVisual();
    }

    protected override void OnRender(
        DrawingContext drawingContext)
    {
        base.OnRender(
            drawingContext);

        if (ActualWidth <= 0.0 ||
            ActualHeight <= 0.0 ||
            _maximumOpacity <= 0.0001)
        {
            return;
        }

        var outer =
            new RectangleGeometry(
                new Rect(
                    0.0,
                    0.0,
                    ActualWidth,
                    ActualHeight));

        Geometry? everyRegion = null;
        Geometry? fullyClear = null;
        Geometry? cursorGeometry = null;

        foreach (var cursorHole in
                 _cursorHoles)
        {
            var ellipse =
                CreateCursorGeometry(
                    cursorHole);

            cursorGeometry =
                Union(
                    cursorGeometry,
                    ellipse);
        }

        fullyClear =
            cursorGeometry;

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

            everyRegion =
                Union(
                    everyRegion,
                    rectangle);

            if (region.Opacity <=
                0.0001)
            {
                fullyClear =
                    Union(
                        fullyClear,
                        rectangle);
            }
        }

        var everyHole =
            Union(
                everyRegion,
                cursorGeometry);

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
                        region.Opacity >
                            0.0001 &&
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
            fullyClear;

        foreach (var group in
                 opacityGroups)
        {
            Geometry? groupGeometry = null;

            foreach (var region in
                     group)
            {
                groupGeometry =
                    Union(
                        groupGeometry,
                        CreateRectangleGeometry(
                            region.NormalizedBounds));
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

    private Geometry? CreateCursorGeometry(
        CursorHole cursorHole)
    {
        var radiusX =
            cursorHole.NormalizedRadiusX *
            ActualWidth;

        var radiusY =
            cursorHole.NormalizedRadiusY *
            ActualHeight;

        if (radiusX <= 0.1 ||
            radiusY <= 0.1)
        {
            return null;
        }

        return new EllipseGeometry(
            new System.Windows.Point(
                cursorHole.NormalizedPosition.X *
                ActualWidth,
                cursorHole.NormalizedPosition.Y *
                ActualHeight),
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

    private static System.Windows.Media.Brush
        CreateBlackBrush(
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
