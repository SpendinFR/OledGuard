using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuardSimple;

internal static class DisplayArea
{
    private const int AutoHideActivationPixels = 3;

    public static DrawingRectangle GetProtectionBounds(
        FormsScreen screen)
    {
        var screenBounds = screen.Bounds;
        var taskbar = FindTaskbarForScreen(screen);

        if (taskbar != IntPtr.Zero &&
            NativeMethods.GetWindowRect(
                taskbar,
                out var nativeRectangle))
        {
            var taskbarBounds = DrawingRectangle.FromLTRB(
                nativeRectangle.Left,
                nativeRectangle.Top,
                nativeRectangle.Right,
                nativeRectangle.Bottom);

            var intersection = DrawingRectangle.Intersect(
                taskbarBounds,
                screenBounds);

            if (IsUsableTaskbarIntersection(
                    screenBounds,
                    intersection))
            {
                return ExcludeTaskbar(
                    screenBounds,
                    intersection);
            }
        }

        if (screen.WorkingArea.Width > 0 &&
            screen.WorkingArea.Height > 0 &&
            screen.WorkingArea != screenBounds)
        {
            return screen.WorkingArea;
        }

        return DrawingRectangle.FromLTRB(
            screenBounds.Left,
            screenBounds.Top,
            screenBounds.Right,
            Math.Max(
                screenBounds.Top + 1,
                screenBounds.Bottom - AutoHideActivationPixels));
    }

    public static IntPtr FindTaskbarForScreen(
        FormsScreen screen)
    {
        var primary = NativeMethods.FindWindow(
            "Shell_TrayWnd",
            null);

        if (TouchesScreen(
                primary,
                screen.Bounds))
        {
            return primary;
        }

        var current = IntPtr.Zero;

        while (true)
        {
            current = NativeMethods.FindWindowEx(
                IntPtr.Zero,
                current,
                "Shell_SecondaryTrayWnd",
                null);

            if (current == IntPtr.Zero)
            {
                break;
            }

            if (TouchesScreen(
                    current,
                    screen.Bounds))
            {
                return current;
            }
        }

        return IntPtr.Zero;
    }

    private static bool IsUsableTaskbarIntersection(
        DrawingRectangle screen,
        DrawingRectangle taskbar)
    {
        if (taskbar.Width <= 0 ||
            taskbar.Height <= 0)
        {
            return false;
        }

        var horizontal =
            taskbar.Width >= screen.Width * 0.60 &&
            taskbar.Height <= screen.Height * 0.30;

        var vertical =
            taskbar.Height >= screen.Height * 0.60 &&
            taskbar.Width <= screen.Width * 0.30;

        return horizontal || vertical;
    }

    private static DrawingRectangle ExcludeTaskbar(
        DrawingRectangle screen,
        DrawingRectangle taskbar)
    {
        if (taskbar.Width >= taskbar.Height)
        {
            var reserve = Math.Max(
                AutoHideActivationPixels,
                taskbar.Height);

            var atTop =
                taskbar.Top <=
                screen.Top + screen.Height / 2;

            return atTop
                ? DrawingRectangle.FromLTRB(
                    screen.Left,
                    Math.Min(
                        screen.Bottom - 1,
                        screen.Top + reserve),
                    screen.Right,
                    screen.Bottom)
                : DrawingRectangle.FromLTRB(
                    screen.Left,
                    screen.Top,
                    screen.Right,
                    Math.Max(
                        screen.Top + 1,
                        screen.Bottom - reserve));
        }

        var horizontalReserve = Math.Max(
            AutoHideActivationPixels,
            taskbar.Width);

        var atLeft =
            taskbar.Left <=
            screen.Left + screen.Width / 2;

        return atLeft
            ? DrawingRectangle.FromLTRB(
                Math.Min(
                    screen.Right - 1,
                    screen.Left + horizontalReserve),
                screen.Top,
                screen.Right,
                screen.Bottom)
            : DrawingRectangle.FromLTRB(
                screen.Left,
                screen.Top,
                Math.Max(
                    screen.Left + 1,
                    screen.Right - horizontalReserve),
                screen.Bottom);
    }

    private static bool TouchesScreen(
        IntPtr window,
        DrawingRectangle screenBounds)
    {
        if (window == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(
                window,
                out var nativeRectangle))
        {
            return false;
        }

        var windowBounds = DrawingRectangle.FromLTRB(
            nativeRectangle.Left,
            nativeRectangle.Top,
            nativeRectangle.Right,
            nativeRectangle.Bottom);

        var intersection = DrawingRectangle.Intersect(
            windowBounds,
            screenBounds);

        return intersection.Width > 0 &&
               intersection.Height > 0;
    }
}
