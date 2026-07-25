using System.Windows;
using System.Windows.Interop;

namespace OledGuardSimple;

internal sealed class HotkeyWindow : Window
{
    private const int ToggleHotkeyId = 1;
    private const int ExitHotkeyId = 2;

    private readonly Action _toggle;
    private readonly Action _exit;
    private IntPtr _handle;
    private HwndSource? _source;

    public HotkeyWindow(
        Action toggle,
        Action exit)
    {
        _toggle = toggle;
        _exit = exit;

        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 1;
        Height = 1;
        Left = -32_000;
        Top = -32_000;
        Opacity = 0.0;
        Focusable = false;

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(
        object? sender,
        EventArgs eventArgs)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProcedure);

        NativeMethods.RegisterHotKey(
            _handle,
            ToggleHotkeyId,
            NativeMethods.ModControl |
            NativeMethods.ModAlt,
            NativeMethods.VkO);

        NativeMethods.RegisterHotKey(
            _handle,
            ExitHotkeyId,
            NativeMethods.ModControl |
            NativeMethods.ModAlt,
            NativeMethods.VkQ);
    }

    private IntPtr WindowProcedure(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != NativeMethods.WmHotKey)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();

        if (id == ToggleHotkeyId)
        {
            handled = true;
            _toggle();
        }
        else if (id == ExitHotkeyId)
        {
            handled = true;
            _exit();
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(
        EventArgs eventArgs)
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(
                _handle,
                ToggleHotkeyId);

            NativeMethods.UnregisterHotKey(
                _handle,
                ExitHotkeyId);
        }

        if (_source is not null)
        {
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }

        base.OnClosed(eventArgs);
    }
}
