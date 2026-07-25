using System.Runtime.InteropServices;
using DrawingRectangle = System.Drawing.Rectangle;

namespace OledGuardSimple;

internal sealed class ScreenCapture : IDisposable
{
    private readonly DrawingRectangle _sourceBounds;
    private readonly IntPtr _screenDeviceContext;
    private readonly IntPtr _memoryDeviceContext;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previousBitmap;
    private readonly IntPtr _bits;
    private bool _disposed;

    public ScreenCapture(
        DrawingRectangle sourceBounds,
        int width,
        int height)
    {
        _sourceBounds = sourceBounds;
        Width = width;
        Height = height;
        Stride = checked(width * 4);

        _screenDeviceContext = NativeMethods.GetDC(
            IntPtr.Zero);

        if (_screenDeviceContext == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Impossible d'obtenir le contexte écran.");
        }

        _memoryDeviceContext = NativeMethods.CreateCompatibleDC(
            _screenDeviceContext);

        if (_memoryDeviceContext == IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(
                IntPtr.Zero,
                _screenDeviceContext);

            throw new InvalidOperationException(
                "Impossible de créer le contexte de capture.");
        }

        var bitmapInfo = new NativeMethods.BitmapInfo
        {
            bmiHeader = new NativeMethods.BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BiRgb,
                biSizeImage = (uint)checked(Stride * height)
            }
        };

        _bitmap = NativeMethods.CreateDIBSection(
            _screenDeviceContext,
            ref bitmapInfo,
            NativeMethods.DibRgbColors,
            out _bits,
            IntPtr.Zero,
            0);

        if (_bitmap == IntPtr.Zero ||
            _bits == IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memoryDeviceContext);
            NativeMethods.ReleaseDC(
                IntPtr.Zero,
                _screenDeviceContext);

            throw new InvalidOperationException(
                "Impossible de créer le tampon de capture.");
        }

        _previousBitmap = NativeMethods.SelectObject(
            _memoryDeviceContext,
            _bitmap);

        NativeMethods.SetStretchBltMode(
            _memoryDeviceContext,
            NativeMethods.Halftone);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int BufferLength => checked(Stride * Height);

    public void CaptureInto(byte[] destination)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(ScreenCapture));
        }

        if (destination.Length < BufferLength)
        {
            throw new ArgumentException(
                "Tampon de capture trop petit.",
                nameof(destination));
        }

        NativeMethods.SetBrushOrgEx(
            _memoryDeviceContext,
            0,
            0,
            IntPtr.Zero);

        var copied = NativeMethods.StretchBlt(
            _memoryDeviceContext,
            0,
            0,
            Width,
            Height,
            _screenDeviceContext,
            _sourceBounds.Left,
            _sourceBounds.Top,
            _sourceBounds.Width,
            _sourceBounds.Height,
            NativeMethods.Srccopy);

        if (!copied)
        {
            throw new InvalidOperationException(
                "La capture écran a échoué.");
        }

        Marshal.Copy(
            _bits,
            destination,
            0,
            BufferLength);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_previousBitmap != IntPtr.Zero)
        {
            NativeMethods.SelectObject(
                _memoryDeviceContext,
                _previousBitmap);
        }

        if (_bitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_bitmap);
        }

        if (_memoryDeviceContext != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memoryDeviceContext);
        }

        if (_screenDeviceContext != IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(
                IntPtr.Zero,
                _screenDeviceContext);
        }
    }
}
