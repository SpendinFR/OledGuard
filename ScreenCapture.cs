using System.Runtime.InteropServices;
using DrawingRectangle = System.Drawing.Rectangle;

namespace OledGuardSimple;

internal sealed class ScreenCapture : IDisposable
{
    private readonly DrawingRectangle _sourceBounds;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly byte[] _buffer;

    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private IntPtr _bits;
    private bool _disposed;

    public ScreenCapture(
        DrawingRectangle sourceBounds,
        int width,
        int height)
    {
        _sourceBounds =
            sourceBounds;

        _width =
            width;

        _height =
            height;

        _stride =
            checked(
                width *
                4);

        _buffer =
            new byte[
                checked(
                    _stride *
                    height)];

        _screenDc =
            NativeMethods.GetDC(
                IntPtr.Zero);

        if (_screenDc ==
            IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Impossible d'ouvrir l'écran.");
        }

        _memoryDc =
            NativeMethods.CreateCompatibleDC(
                _screenDc);

        if (_memoryDc ==
            IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException(
                "Impossible de créer le contexte de capture.");
        }

        var bitmapInfo =
            new NativeMethods.BitmapInfo
            {
                Header =
                    new NativeMethods.BitmapInfoHeader
                    {
                        Size =
                            (uint)Marshal.SizeOf<
                                NativeMethods.BitmapInfoHeader>(),
                        Width =
                            width,
                        Height =
                            -height,
                        Planes =
                            1,
                        BitCount =
                            32,
                        Compression =
                            NativeMethods.BiRgb
                    }
            };

        _bitmap =
            NativeMethods.CreateDIBSection(
                _screenDc,
                ref bitmapInfo,
                NativeMethods.DibRgbColors,
                out _bits,
                IntPtr.Zero,
                0);

        if (_bitmap ==
                IntPtr.Zero ||
            _bits ==
                IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException(
                "Impossible de créer le tampon de capture.");
        }

        _previousBitmap =
            NativeMethods.SelectObject(
                _memoryDc,
                _bitmap);

        NativeMethods.SetStretchBltMode(
            _memoryDc,
            NativeMethods.Halftone);

        NativeMethods.SetBrushOrgEx(
            _memoryDc,
            0,
            0,
            IntPtr.Zero);
    }

    public byte[] Capture()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(ScreenCapture));
        }

        var copied =
            NativeMethods.StretchBlt(
                _memoryDc,
                0,
                0,
                _width,
                _height,
                _screenDc,
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
            _buffer,
            0,
            _buffer.Length);

        return _buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_memoryDc !=
                IntPtr.Zero &&
            _previousBitmap !=
                IntPtr.Zero)
        {
            NativeMethods.SelectObject(
                _memoryDc,
                _previousBitmap);

            _previousBitmap =
                IntPtr.Zero;
        }

        if (_bitmap !=
            IntPtr.Zero)
        {
            NativeMethods.DeleteObject(
                _bitmap);

            _bitmap =
                IntPtr.Zero;
        }

        if (_memoryDc !=
            IntPtr.Zero)
        {
            NativeMethods.DeleteDC(
                _memoryDc);

            _memoryDc =
                IntPtr.Zero;
        }

        if (_screenDc !=
            IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(
                IntPtr.Zero,
                _screenDc);

            _screenDc =
                IntPtr.Zero;
        }

        _bits =
            IntPtr.Zero;
    }
}
