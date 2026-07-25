using System.Runtime.InteropServices;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal static class ProtectionArea
{
    private const int MinimumActivationEdgePixels = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static DrawingRectangle GetBounds(
        FormsScreen screen)
    {
        var screenBounds =
            screen.Bounds;

        var taskbar =
            FindTaskbarForScreen(
                screen);

        if (taskbar !=
                IntPtr.Zero &&
            GetWindowRect(
                taskbar,
                out var nativeRectangle))
        {
            var taskbarBounds =
                DrawingRectangle.FromLTRB(
                    nativeRectangle.Left,
                    nativeRectangle.Top,
                    nativeRectangle.Right,
                    nativeRectangle.Bottom);

            var intersection =
                DrawingRectangle.Intersect(
                    taskbarBounds,
                    screenBounds);

            if (intersection.Width > 0 &&
                intersection.Height > 0)
            {
                return ExcludeTaskbarEdge(
                    screenBounds,
                    intersection);
            }
        }

        var workingArea =
            screen.WorkingArea;

        if (workingArea.Width > 0 &&
            workingArea.Height > 0 &&
            workingArea !=
            screenBounds)
        {
            return workingArea;
        }

        return DrawingRectangle.FromLTRB(
            screenBounds.Left,
            screenBounds.Top,
            screenBounds.Right,
            Math.Max(
                screenBounds.Top + 1,
                screenBounds.Bottom -
                MinimumActivationEdgePixels));
    }

    public static IntPtr FindTaskbarForScreen(
        FormsScreen screen)
    {
        var primary =
            FindWindow(
                "Shell_TrayWnd",
                null);

        if (TouchesScreen(
                primary,
                screen.Bounds))
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

            if (TouchesScreen(
                    current,
                    screen.Bounds))
            {
                return current;
            }
        }

        return IntPtr.Zero;
    }

    private static DrawingRectangle
        ExcludeTaskbarEdge(
            DrawingRectangle screen,
            DrawingRectangle taskbar)
    {
        var horizontal =
            taskbar.Width >=
            taskbar.Height;

        if (horizontal)
        {
            var atTop =
                taskbar.Top <=
                screen.Top +
                screen.Height /
                2;

            var reserved =
                Math.Max(
                    MinimumActivationEdgePixels,
                    taskbar.Height);

            return atTop
                ? DrawingRectangle.FromLTRB(
                    screen.Left,
                    Math.Min(
                        screen.Bottom - 1,
                        screen.Top +
                        reserved),
                    screen.Right,
                    screen.Bottom)
                : DrawingRectangle.FromLTRB(
                    screen.Left,
                    screen.Top,
                    screen.Right,
                    Math.Max(
                        screen.Top + 1,
                        screen.Bottom -
                        reserved));
        }

        var atLeft =
            taskbar.Left <=
            screen.Left +
            screen.Width /
            2;

        var horizontalReserved =
            Math.Max(
                MinimumActivationEdgePixels,
                taskbar.Width);

        return atLeft
            ? DrawingRectangle.FromLTRB(
                Math.Min(
                    screen.Right - 1,
                    screen.Left +
                    horizontalReserved),
                screen.Top,
                screen.Right,
                screen.Bottom)
            : DrawingRectangle.FromLTRB(
                screen.Left,
                screen.Top,
                Math.Max(
                    screen.Left + 1,
                    screen.Right -
                    horizontalReserved),
                screen.Bottom);
    }

    private static bool TouchesScreen(
        IntPtr window,
        DrawingRectangle screenBounds)
    {
        if (window ==
                IntPtr.Zero ||
            !GetWindowRect(
                window,
                out var nativeRectangle))
        {
            return false;
        }

        var windowBounds =
            DrawingRectangle.FromLTRB(
                nativeRectangle.Left,
                nativeRectangle.Top,
                nativeRectangle.Right,
                nativeRectangle.Bottom);

        var intersection =
            DrawingRectangle.Intersect(
                windowBounds,
                screenBounds);

        return intersection.Width > 0 &&
               intersection.Height > 0;
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
