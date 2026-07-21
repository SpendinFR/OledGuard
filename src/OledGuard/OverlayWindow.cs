using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class OverlayWindow : Window
{
    private readonly FormsScreen _screen;
    private readonly MaskSurface _surface;
    private IntPtr _handle;
    private HwndSource? _source;

    public OverlayWindow(FormsScreen screen)
    {
        _screen = screen;
        _surface = new MaskSurface();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _surface;

        SourceInitialized += OnSourceInitialized;
    }

    public bool ExcludedFromCapture { get; private set; }

    public void SetMask(float[] alpha, int columns, int rows)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetMask(alpha, columns, rows)));
            return;
        }

        _surface.UpdateMask(alpha, columns, rows);
    }

    public void EnsureVisible()
    {
        if (!IsVisible)
        {
            Show();
        }

        PlaceExactly();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProcedure);
        var currentStyle = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        var updatedStyle = currentStyle
            | NativeMethods.WsExTransparent
            | NativeMethods.WsExToolWindow
            | NativeMethods.WsExNoActivate;

        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, new IntPtr(updatedStyle));
        ExcludedFromCapture = NativeMethods.SetWindowDisplayAffinity(_handle, NativeMethods.WdaExcludeFromCapture);
        PlaceExactly();
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmNcHitTest)
        {
            handled = true;
            return new IntPtr(NativeMethods.HtTransparent);
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source is not null)
        {
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }

        base.OnClosed(e);
    }

    private void PlaceExactly()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var bounds = _screen.Bounds;
        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }
}
