using System.Windows;
using System.Windows.Interop;
using DrawingRectangle = System.Drawing.Rectangle;

namespace OledGuardSimple;

internal sealed class OverlayWindow : Window
{
    private readonly DrawingRectangle _bounds;
    private readonly MaskSurface _surface;

    private IntPtr _handle;
    private HwndSource? _source;

    public OverlayWindow(
        DrawingRectangle bounds)
    {
        _bounds =
            bounds;

        _surface =
            new MaskSurface();

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
            _surface;

        SourceInitialized +=
            OnSourceInitialized;
    }

    public void SetScene(
        double maximumOpacity,
        IReadOnlyList<RevealRegion> regions,
        IReadOnlyList<CursorHole> cursorHoles)
    {
        if (!Dispatcher.CheckAccess())
        {
            var regionCopy =
                regions.ToArray();

            var cursorCopy =
                cursorHoles.ToArray();

            Dispatcher.BeginInvoke(
                new Action(
                    () =>
                        SetScene(
                            maximumOpacity,
                            regionCopy,
                            cursorCopy)));

            return;
        }

        _surface.UpdateScene(
            maximumOpacity,
            regions,
            cursorHoles);
    }

    public void EnsureVisible()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                new Action(
                    EnsureVisible));

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        PlaceExactly();
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

        var updatedStyle =
            currentStyle |
            NativeMethods.WsExTransparent |
            NativeMethods.WsExToolWindow |
            NativeMethods.WsExNoActivate;

        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            new IntPtr(
                updatedStyle));

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
