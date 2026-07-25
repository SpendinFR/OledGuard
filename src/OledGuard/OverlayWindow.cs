using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class OverlayWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly FormsScreen _screen;
    private readonly DrawingRectangle
        _protectionBounds;
    private readonly MaskSurface _surface;

    private IntPtr _handle;
    private HwndSource? _source;

    public OverlayWindow(
        FormsScreen screen)
    {
        _screen = screen;
        _protectionBounds =
            ProtectionArea.GetBounds(
                screen);
        _surface =
            new MaskSurface();

        WindowStyle =
            WindowStyle.None;
        ResizeMode =
            ResizeMode.NoResize;
        AllowsTransparency = true;
        Background =
            System.Windows.Media.Brushes.Transparent;
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

    public void SetScene(
        double maximumOpacity,
        IReadOnlyList<MaskRegion> regions,
        IReadOnlyList<MouseReveal> mouseReveals)
    {
        if (!Dispatcher.CheckAccess())
        {
            var regionCopy =
                regions.ToArray();
            var mouseCopy =
                mouseReveals.ToArray();

            Dispatcher.BeginInvoke(
                new Action(
                    () =>
                        SetScene(
                            maximumOpacity,
                            regionCopy,
                            mouseCopy)));
            return;
        }

        _surface.UpdateScene(
            maximumOpacity,
            regions,
            mouseReveals);
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
        EventArgs e)
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

        var taskbar =
            FindTaskbarForScreen();

        NativeMethods.SetWindowPos(
            _handle,
            taskbar != IntPtr.Zero
                ? taskbar
                : NativeMethods.HwndTopmost,
            _protectionBounds.Left,
            _protectionBounds.Top,
            _protectionBounds.Width,
            _protectionBounds.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpShowWindow);
    }

    private IntPtr FindTaskbarForScreen()
    {
        var primary =
            FindWindow(
                "Shell_TrayWnd",
                null);

        if (WindowTouchesScreen(
                primary))
        {
            return primary;
        }

        var current =
            IntPtr.Zero;

        while (true)
        {
            current =
                FindWindowEx(
                    IntPtr.Zero,
                    current,
                    "Shell_SecondaryTrayWnd",
                    null);

            if (current ==
                IntPtr.Zero)
            {
                break;
            }

            if (WindowTouchesScreen(
                    current))
            {
                return current;
            }
        }

        return IntPtr.Zero;
    }

    private bool WindowTouchesScreen(
        IntPtr window)
    {
        if (window ==
                IntPtr.Zero ||
            !GetWindowRect(
                window,
                out var native))
        {
            return false;
        }

        var rectangle =
            DrawingRectangle.FromLTRB(
                native.Left,
                native.Top,
                native.Right,
                native.Bottom);
        var intersection =
            DrawingRectangle.Intersect(
                rectangle,
                _screen.Bounds);

        return intersection.Width > 0 &&
               intersection.Height > 0;
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

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(
        string? className,
        string? windowName);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out NativeWindowRect rectangle);
}
