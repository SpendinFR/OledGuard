using System.Windows.Interop;

namespace OledGuard;

internal sealed class HotkeyHost : IDisposable
{
    private const int ToggleHotkeyId = 0x4F47;
    private const int RevealHotkeyId = 0x4F48;
    private const int DrawZoneHotkeyId = 0x4F49;

    private readonly ProtectionController _controller;
    private readonly HwndSource _source;
    private bool _disposed;

    public HotkeyHost(ProtectionController controller)
    {
        _controller = controller;
        var parameters = new HwndSourceParameters("OledGuard.Hotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = (int)NativeMethods.WsExToolWindow
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WindowProcedure);
        NativeMethods.RegisterHotKey(
            _source.Handle,
            ToggleHotkeyId,
            NativeMethods.ModControl | NativeMethods.ModAlt,
            NativeMethods.VkO);
        NativeMethods.RegisterHotKey(
            _source.Handle,
            RevealHotkeyId,
            NativeMethods.ModControl | NativeMethods.ModAlt,
            NativeMethods.VkR);
        NativeMethods.RegisterHotKey(
            _source.Handle,
            DrawZoneHotkeyId,
            NativeMethods.ModControl | NativeMethods.ModAlt,
            NativeMethods.VkD);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotKey)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        if (id == ToggleHotkeyId)
        {
            _controller.Toggle();
            handled = true;
        }
        else if (id == RevealHotkeyId)
        {
            _controller.RevealAll();
            handled = true;
        }
        else if (id == DrawZoneHotkeyId)
        {
            _controller.BeginManualZoneSelection();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMethods.UnregisterHotKey(_source.Handle, ToggleHotkeyId);
        NativeMethods.UnregisterHotKey(_source.Handle, RevealHotkeyId);
        NativeMethods.UnregisterHotKey(_source.Handle, DrawZoneHotkeyId);
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
    }
}
