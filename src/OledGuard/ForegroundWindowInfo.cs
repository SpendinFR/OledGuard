using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace OledGuard;

internal readonly record struct ForegroundWindowInfo(
    IntPtr Handle,
    Rectangle Bounds,
    string ClassName)
{
    public static bool TryGetForScreen(Rectangle screenBounds, out ForegroundWindowInfo info)
    {
        info = default;

        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle))
        {
            return false;
        }

        NativeMethods.Rect nativeRect;
        var dwmResult = NativeMethods.DwmGetWindowAttribute(
            handle,
            NativeMethods.DwmwaExtendedFrameBounds,
            out nativeRect,
            Marshal.SizeOf<NativeMethods.Rect>());

        if (dwmResult != 0 && !NativeMethods.GetWindowRect(handle, out nativeRect))
        {
            return false;
        }

        var width = nativeRect.Right - nativeRect.Left;
        var height = nativeRect.Bottom - nativeRect.Top;
        if (width < 8 || height < 8)
        {
            return false;
        }

        var windowBounds = Rectangle.FromLTRB(
            nativeRect.Left,
            nativeRect.Top,
            nativeRect.Right,
            nativeRect.Bottom);
        var intersection = Rectangle.Intersect(windowBounds, screenBounds);
        if (intersection.Width < 8 || intersection.Height < 8)
        {
            return false;
        }

        var classNameBuffer = new StringBuilder(256);
        _ = NativeMethods.GetClassName(handle, classNameBuffer, classNameBuffer.Capacity);
        var className = classNameBuffer.ToString();

        // Desktop hosts are effectively the whole monitor for interaction purposes.
        if (className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase))
        {
            intersection = screenBounds;
        }

        info = new ForegroundWindowInfo(handle, intersection, className);
        return true;
    }
}
