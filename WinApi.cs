using System.Runtime.InteropServices;

namespace OledGuardFresh;

internal static class WinApi
{
    internal const int GwlExStyle = -20;

    internal const long WsExTransparent =
        0x00000020L;

    internal const long WsExToolWindow =
        0x00000080L;

    internal const long WsExNoActivate =
        0x08000000L;

    internal const int WmNcHitTest =
        0x0084;

    internal const int HtTransparent =
        -1;

    internal const uint WdaExcludeFromCapture =
        0x00000011;

    internal const uint SwpNoActivate =
        0x0010;

    internal const uint SwpShowWindow =
        0x0040;

    internal const int Srccopy =
        0x00CC0020;

    internal const int Halftone =
        4;

    internal const uint DibRgbColors =
        0;

    internal const int BiRgb =
        0;

    internal static readonly IntPtr HwndTopmost =
        new(-1);

    private static readonly IntPtr
        PerMonitorAwareV2 =
            new(-4);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal int Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    internal static void EnableDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(
                PerMonitorAwareV2);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool
        SetProcessDpiAwarenessContext(
            IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(
        out NativePoint point);

    [DllImport("user32.dll")]
    internal static extern IntPtr
        GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(
        IntPtr window,
        out NativeRect rectangle);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(
        IntPtr window,
        System.Text.StringBuilder className,
        int maximumCharacters);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    internal static extern IntPtr
        GetWindowLongPtr(
            IntPtr window,
            int index);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    internal static extern IntPtr
        SetWindowLongPtr(
            IntPtr window,
            int index,
            IntPtr value);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool
        SetWindowDisplayAffinity(
            IntPtr window,
            uint affinity);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(
        IntPtr window);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(
        IntPtr window,
        IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr
        CreateCompatibleDC(
            IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(
        IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(
        IntPtr deviceContext,
        IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(
        IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr
        CreateDIBSection(
            IntPtr deviceContext,
            ref BitmapInfo bitmapInfo,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

    [DllImport("gdi32.dll")]
    internal static extern int
        SetStretchBltMode(
            IntPtr deviceContext,
            int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetBrushOrgEx(
        IntPtr deviceContext,
        int x,
        int y,
        IntPtr previousPoint);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StretchBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        IntPtr source,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        int rasterOperation);
}
