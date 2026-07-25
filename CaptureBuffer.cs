using System.Runtime.InteropServices;
using DrawingRectangle = System.Drawing.Rectangle;

namespace OledGuardFresh;

internal sealed class CaptureBuffer : IDisposable
{
    private readonly DrawingRectangle _source;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _pixels;

    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private bool _disposed;

    internal CaptureBuffer(
        DrawingRectangle source,
        int width,
        int height)
    {
        _source =
            source;

        _width =
            width;

        _height =
            height;

        _pixels =
            new byte[
                checked(
                    width *
                    height *
                    4)];

        _screenDc =
            WinApi.GetDC(
                IntPtr.Zero);

        if (_screenDc ==
            IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Impossible d'ouvrir la capture écran.");
        }

        _memoryDc =
            WinApi.CreateCompatibleDC(
                _screenDc);

        if (_memoryDc ==
            IntPtr.Zero)
        {
            Dispose();

            throw new InvalidOperationException(
                "Impossible de créer la capture écran.");
        }

        var bitmapInfo =
            new WinApi.BitmapInfo
            {
                Header =
                    new WinApi.BitmapInfoHeader
                    {
                        Size =
                            (uint)Marshal.SizeOf<
                                WinApi.BitmapInfoHeader>(),
                        Width =
                            width,
                        Height =
                            -height,
                        Planes =
                            1,
                        BitCount =
                            32,
                        Compression =
                            WinApi.BiRgb
                    }
            };

        _bitmap =
            WinApi.CreateDIBSection(
                _screenDc,
                ref bitmapInfo,
                WinApi.DibRgbColors,
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

        _oldBitmap =
            WinApi.SelectObject(
                _memoryDc,
                _bitmap);

        WinApi.SetStretchBltMode(
            _memoryDc,
            WinApi.Halftone);

        WinApi.SetBrushOrgEx(
            _memoryDc,
            0,
            0,
            IntPtr.Zero);
    }

    internal byte[] Grab()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(CaptureBuffer));
        }

        var copied =
            WinApi.StretchBlt(
                _memoryDc,
                0,
                0,
                _width,
                _height,
                _screenDc,
                _source.Left,
                _source.Top,
                _source.Width,
                _source.Height,
                WinApi.Srccopy);

        if (!copied)
        {
            throw new InvalidOperationException(
                "La capture écran a échoué.");
        }

        Marshal.Copy(
            _bits,
            _pixels,
            0,
            _pixels.Length);

        return _pixels;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed =
            true;

        if (_memoryDc !=
                IntPtr.Zero &&
            _oldBitmap !=
                IntPtr.Zero)
        {
            WinApi.SelectObject(
                _memoryDc,
                _oldBitmap);

            _oldBitmap =
                IntPtr.Zero;
        }

        if (_bitmap !=
            IntPtr.Zero)
        {
            WinApi.DeleteObject(
                _bitmap);

            _bitmap =
                IntPtr.Zero;
        }

        if (_memoryDc !=
            IntPtr.Zero)
        {
            WinApi.DeleteDC(
                _memoryDc);

            _memoryDc =
                IntPtr.Zero;
        }

        if (_screenDc !=
            IntPtr.Zero)
        {
            WinApi.ReleaseDC(
                IntPtr.Zero,
                _screenDc);

            _screenDc =
                IntPtr.Zero;
        }

        _bits =
            IntPtr.Zero;
    }
}
