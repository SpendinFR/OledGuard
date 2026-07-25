using System.Runtime.InteropServices;

namespace OledGuardNeuf;

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const long WsExTransparent = 0x00000020L;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExNoActivate = 0x08000000L;

    public const int WmNcHitTest = 0x0084;
    public const int HtTransparent = -1;

    public const uint WdaExcludeFromCapture = 0x00000011;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;

    public const int Srccopy = 0x00CC0020;
    public const int Halftone = 4;
    public const uint DibRgbColors = 0;
    public const int BiRgb = 0;

    public static readonly IntPtr HwndTopmost =
        new(-1);

    private static readonly IntPtr
        DpiAwarenessContextPerMonitorAwareV2 =
            new(-4);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    public static void TryEnablePerMonitorDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(
                DpiAwarenessContextPerMonitorAwareV2);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(
        IntPtr dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(
        out Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(
        IntPtr window,
        out Rect rectangle);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    public static extern int GetClassName(
        IntPtr window,
        System.Text.StringBuilder className,
        int maximumCharacters);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(
        IntPtr window,
        int index);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(
        IntPtr window,
        int index,
        IntPtr newValue);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(
        IntPtr window,
        uint affinity);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(
        IntPtr window);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(
        IntPtr window,
        IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(
        IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(
        IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(
        IntPtr deviceContext,
        IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(
        IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(
        IntPtr deviceContext,
        int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetBrushOrgEx(
        IntPtr deviceContext,
        int x,
        int y,
        IntPtr previousPoint);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StretchBlt(
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
