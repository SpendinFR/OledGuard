using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class OverlayWindow : Window
{
    private const int ActivationEdgePixels =
        4;

    private readonly FormsScreen _screen;
    private readonly MaskSurface _surface;
    private IntPtr _handle;
    private HwndSource? _source;

    public OverlayWindow(
        FormsScreen screen)
    {
        _screen = screen;
        _surface =
            new MaskSurface();

        WindowStyle =
            WindowStyle.None;
        ResizeMode =
            ResizeMode.NoResize;
        AllowsTransparency = true;
        Background =
            System.Windows.Media
                .Brushes
                .Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _surface;

        SourceInitialized +=
            OnSourceInitialized;
    }

    public bool ExcludedFromCapture
    {
        get;
        private set;
    }

    public void SetMask(
        IReadOnlyList<float> alpha,
        int columns,
        int rows)
    {
        if (!Dispatcher.CheckAccess())
        {
            var copy =
                alpha.ToArray();

            Dispatcher.BeginInvoke(
                new Action(
                    () =>
                        SetMask(
                            copy,
                            columns,
                            rows)));
            return;
        }

        _surface.UpdateMask(
            alpha,
            columns,
            rows);
    }

    public void EnsureVisible()
    {
        if (!IsVisible)
        {
            Show();
        }

        PlaceExactly();
    }

    private void OnSourceInitialized(
        object? sender,
        EventArgs e)
    {
        _handle =
            new WindowInteropHelper(
                this)
                .Handle;
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

        ExcludedFromCapture =
            NativeMethods.SetWindowDisplayAffinity(
                _handle,
                NativeMethods.WdaExcludeFromCapture);

        PlaceExactly();
    }

    private IntPtr WindowProcedure(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message ==
            NativeMethods.WmNcHitTest)
        {
            handled = true;
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

        var bounds =
            _screen.Bounds;

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpShowWindow);

        ApplyActivationEdgeRegion(
            bounds.Width,
            bounds.Height);
    }

    private void ApplyActivationEdgeRegion(
        int width,
        int height)
    {
        if (_handle ==
                IntPtr.Zero ||
            width <=
                ActivationEdgePixels *
                2 ||
            height <=
                ActivationEdgePixels *
                2)
        {
            return;
        }

        var region =
            CreateRectRgn(
                ActivationEdgePixels,
                ActivationEdgePixels,
                width -
                    ActivationEdgePixels,
                height -
                    ActivationEdgePixels);

        if (region ==
            IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(
                _handle,
                region,
                true) == 0)
        {
            DeleteRegionObject(
                region);
        }
        // On success Windows owns the region handle.
    }

    protected override void OnClosed(
        EventArgs e)
    {
        if (_source is not null)
        {
            _source.RemoveHook(
                WindowProcedure);
            _source = null;
        }

        base.OnClosed(
            e);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr
        CreateRectRgn(
            int left,
            int top,
            int right,
            int bottom);

    [DllImport("user32.dll")]
    private static extern int
        SetWindowRgn(
            IntPtr window,
            IntPtr region,
            [MarshalAs(UnmanagedType.Bool)]
            bool redraw);

    [DllImport(
        "gdi32.dll",
        EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool
        DeleteRegionObject(
            IntPtr graphicObject);
}
