using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace OledGuardFresh;

internal readonly record struct RevealBox(
    WpfRect NormalizedBounds);

internal readonly record struct CursorSpot(
    WpfPoint NormalizedPosition,
    double NormalizedRadiusX,
    double NormalizedRadiusY);

internal sealed class FreshOverlay : Window
{
    private readonly DrawingRectangle _bounds;
    private readonly FreshMask _mask;

    private IntPtr _handle;
    private HwndSource? _source;

    internal FreshOverlay(
        DrawingRectangle bounds)
    {
        _bounds =
            bounds;

        _mask =
            new FreshMask();

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
            _mask;

        SourceInitialized +=
            SourceReady;
    }

    internal void ShowOverlay()
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

        Place();
    }

    internal void Draw(
        bool enabled,
        IReadOnlyList<RevealBox> boxes,
        CursorSpot? cursor)
    {
        if (!Dispatcher.CheckAccess())
        {
            var boxCopy =
                boxes.ToArray();

            Dispatcher.BeginInvoke(
                new Action(
                    () =>
                        Draw(
                            enabled,
                            boxCopy,
                            cursor)));

            return;
        }

        _mask.SetScene(
            enabled,
            boxes,
            cursor);
    }

    private void SourceReady(
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
            WinApi.GetWindowLongPtr(
                    _handle,
                    WinApi.GwlExStyle)
                .ToInt64();

        WinApi.SetWindowLongPtr(
            _handle,
            WinApi.GwlExStyle,
            new IntPtr(
                currentStyle |
                WinApi.WsExTransparent |
                WinApi.WsExToolWindow |
                WinApi.WsExNoActivate));

        WinApi.SetWindowDisplayAffinity(
            _handle,
            WinApi.WdaExcludeFromCapture);

        Place();
    }

    private IntPtr WindowProcedure(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message ==
            WinApi.WmNcHitTest)
        {
            handled =
                true;

            return new IntPtr(
                WinApi.HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void Place()
    {
        if (_handle ==
            IntPtr.Zero)
        {
            return;
        }

        WinApi.SetWindowPos(
            _handle,
            WinApi.HwndTopmost,
            _bounds.Left,
            _bounds.Top,
            _bounds.Width,
            _bounds.Height,
            WinApi.SwpNoActivate |
            WinApi.SwpShowWindow);
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

internal sealed class FreshMask : FrameworkElement
{
    private const double MaskOpacity =
        0.85;

    private RevealBox[] _boxes =
        Array.Empty<RevealBox>();

    private CursorSpot? _cursor;
    private bool _enabled;

    internal FreshMask()
    {
        IsHitTestVisible =
            false;

        SnapsToDevicePixels =
            true;

        UseLayoutRounding =
            true;
    }

    internal void SetScene(
        bool enabled,
        IReadOnlyList<RevealBox> boxes,
        CursorSpot? cursor)
    {
        _enabled =
            enabled;

        _boxes =
            boxes.Count == 0
                ? Array.Empty<RevealBox>()
                : boxes.ToArray();

        _cursor =
            cursor;

        InvalidateVisual();
    }

    protected override void OnRender(
        DrawingContext drawingContext)
    {
        base.OnRender(
            drawingContext);

        if (!_enabled ||
            ActualWidth <=
                0.0 ||
            ActualHeight <=
                0.0)
        {
            return;
        }

        Geometry? holes =
            null;

        foreach (var box in
                 _boxes)
        {
            holes =
                Union(
                    holes,
                    CreateRectangle(
                        box.NormalizedBounds));
        }

        if (_cursor is
            CursorSpot cursor)
        {
            holes =
                Union(
                    holes,
                    CreateCursor(
                        cursor));
        }

        var outside =
            new RectangleGeometry(
                new WpfRect(
                    0.0,
                    0.0,
                    ActualWidth,
                    ActualHeight));

        Geometry blackArea =
            holes is null
                ? outside
                : new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    outside,
                    holes);

        var brush =
            new SolidColorBrush(
                Colors.Black)
            {
                Opacity =
                    MaskOpacity
            };

        brush.Freeze();

        drawingContext.DrawGeometry(
            brush,
            null,
            blackArea);
    }

    private Geometry? CreateRectangle(
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
                right -
                    left,
                bottom -
                    top));
    }

    private Geometry? CreateCursor(
        CursorSpot cursor)
    {
        var radiusX =
            cursor.NormalizedRadiusX *
            ActualWidth;

        var radiusY =
            cursor.NormalizedRadiusY *
            ActualHeight;

        if (radiusX <=
                0.1 ||
            radiusY <=
                0.1)
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
}
