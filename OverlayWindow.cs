using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace OledGuardNeuf;

internal readonly record struct VisibleRectangle(
    WpfRect NormalizedBounds,
    double MaskOpacity);

internal readonly record struct CursorReveal(
    WpfPoint NormalizedPosition,
    double NormalizedRadiusX,
    double NormalizedRadiusY);

internal sealed class OverlayWindow : Window
{
    private readonly DrawingRectangle _bounds;
    private readonly MaskCanvas _canvas;

    private IntPtr _handle;
    private HwndSource? _source;

    public OverlayWindow(
        DrawingRectangle bounds)
    {
        _bounds =
            bounds;

        _canvas =
            new MaskCanvas();

        WindowStyle =
            WindowStyle.None;

        ResizeMode =
            ResizeMode.NoResize;

        AllowsTransparency =
            true;

        Background =
            System.Windows.Media.Brushes.Transparent;

        ShowInTaskbar =
            false;

        ShowActivated =
            false;

        Topmost =
            true;

        Focusable =
            false;

        IsHitTestVisible =
            false;

        Content =
            _canvas;

        SourceInitialized +=
            OnSourceInitialized;
    }

    public void ShowOverlay()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                new Action(
                    ShowOverlay));

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        PlaceExactly();
    }

    public void Render(
        double maximumOpacity,
        IReadOnlyList<VisibleRectangle> rectangles,
        CursorReveal? cursorReveal)
    {
        if (!Dispatcher.CheckAccess())
        {
            var rectangleCopy =
                rectangles.ToArray();

            Dispatcher.BeginInvoke(
                new Action(
                    () =>
                        Render(
                            maximumOpacity,
                            rectangleCopy,
                            cursorReveal)));

            return;
        }

        _canvas.SetScene(
            maximumOpacity,
            rectangles,
            cursorReveal);
    }

    private void OnSourceInitialized(
        object? sender,
        EventArgs eventArgs)
    {
        _handle =
            new WindowInteropHelper(
                this).Handle;

        _source =
            HwndSource.FromHwnd(
                _handle);

        _source?.AddHook(
            WindowProcedure);

        var currentStyle =
            NativeMethods.GetWindowLongPtr(
                    _handle,
                    NativeMethods.GwlExStyle)
                .ToInt64();

        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            new IntPtr(
                currentStyle |
                NativeMethods.WsExTransparent |
                NativeMethods.WsExToolWindow |
                NativeMethods.WsExNoActivate));

        NativeMethods.SetWindowDisplayAffinity(
            _handle,
            NativeMethods.WdaExcludeFromCapture);

        PlaceExactly();
    }

    private IntPtr WindowProcedure(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message ==
            NativeMethods.WmNcHitTest)
        {
            handled =
                true;

            return new IntPtr(
                NativeMethods.HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void PlaceExactly()
    {
        if (_handle ==
            IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            _bounds.Left,
            _bounds.Top,
            _bounds.Width,
            _bounds.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpShowWindow);
    }

    protected override void OnClosed(
        EventArgs eventArgs)
    {
        if (_source is not null)
        {
            _source.RemoveHook(
                WindowProcedure);

            _source =
                null;
        }

        base.OnClosed(
            eventArgs);
    }
}

internal sealed class MaskCanvas : FrameworkElement
{
    private VisibleRectangle[] _rectangles =
        Array.Empty<VisibleRectangle>();

    private CursorReveal? _cursorReveal;
    private double _maximumOpacity;

    public MaskCanvas()
    {
        IsHitTestVisible =
            false;

        SnapsToDevicePixels =
            true;

        UseLayoutRounding =
            true;
    }

    public void SetScene(
        double maximumOpacity,
        IReadOnlyList<VisibleRectangle> rectangles,
        CursorReveal? cursorReveal)
    {
        _maximumOpacity =
            Math.Clamp(
                maximumOpacity,
                0.0,
                1.0);

        _rectangles =
            rectangles.Count == 0
                ? Array.Empty<VisibleRectangle>()
                : rectangles.ToArray();

        _cursorReveal =
            cursorReveal;

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
                new WpfRect(
                    0.0,
                    0.0,
                    ActualWidth,
                    ActualHeight));

        Geometry? allHoles =
            null;

        Geometry? fullyClear =
            null;

        foreach (var rectangle in
                 _rectangles)
        {
            if (rectangle.MaskOpacity >=
                _maximumOpacity -
                0.0001)
            {
                continue;
            }

            var geometry =
                RectangleGeometry(
                    rectangle.NormalizedBounds);

            allHoles =
                Union(
                    allHoles,
                    geometry);

            if (rectangle.MaskOpacity <=
                0.0001)
            {
                fullyClear =
                    Union(
                        fullyClear,
                        geometry);
            }
        }

        if (_cursorReveal is
            CursorReveal cursor)
        {
            var cursorGeometry =
                CursorGeometry(
                    cursor);

            allHoles =
                Union(
                    allHoles,
                    cursorGeometry);

            fullyClear =
                Union(
                    fullyClear,
                    cursorGeometry);
        }

        Geometry outside =
            allHoles is null
                ? outer
                : new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    outer,
                    allHoles);

        drawingContext.DrawGeometry(
            BlackBrush(
                _maximumOpacity),
            null,
            outside);

        var groups =
            _rectangles
                .Where(
                    rectangle =>
                        rectangle.MaskOpacity >
                            0.0001 &&
                        rectangle.MaskOpacity <
                            _maximumOpacity -
                            0.0001)
                .GroupBy(
                    rectangle =>
                        Math.Round(
                            rectangle.MaskOpacity,
                            4))
                .OrderBy(
                    group =>
                        group.Key);

        var lowerOpacity =
            fullyClear;

        foreach (var group in
                 groups)
        {
            Geometry? groupGeometry =
                null;

            foreach (var rectangle in
                     group)
            {
                groupGeometry =
                    Union(
                        groupGeometry,
                        RectangleGeometry(
                            rectangle.NormalizedBounds));
            }

            if (groupGeometry is null)
            {
                continue;
            }

            Geometry drawable =
                lowerOpacity is null
                    ? groupGeometry
                    : new CombinedGeometry(
                        GeometryCombineMode.Exclude,
                        groupGeometry,
                        lowerOpacity);

            drawingContext.DrawGeometry(
                BlackBrush(
                    Math.Clamp(
                        group.Key,
                        0.0,
                        _maximumOpacity)),
                null,
                drawable);

            lowerOpacity =
                Union(
                    lowerOpacity,
                    groupGeometry);
        }
    }

    private Geometry? RectangleGeometry(
        WpfRect normalized)
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
            new WpfRect(
                left,
                top,
                right - left,
                bottom - top));
    }

    private Geometry? CursorGeometry(
        CursorReveal cursor)
    {
        var radiusX =
            cursor.NormalizedRadiusX *
            ActualWidth;

        var radiusY =
            cursor.NormalizedRadiusY *
            ActualHeight;

        if (radiusX <= 0.1 ||
            radiusY <= 0.1)
        {
            return null;
        }

        return new EllipseGeometry(
            new WpfPoint(
                cursor.NormalizedPosition.X *
                ActualWidth,
                cursor.NormalizedPosition.Y *
                ActualHeight),
            radiusX,
            radiusY);
    }

    private static Geometry? Union(
        Geometry? first,
        Geometry? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return new CombinedGeometry(
            GeometryCombineMode.Union,
            first,
            second);
    }

    private static System.Windows.Media.Brush BlackBrush(
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
