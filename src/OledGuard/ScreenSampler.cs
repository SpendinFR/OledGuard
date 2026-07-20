using System.Runtime.InteropServices;
using DrawingRectangle = System.Drawing.Rectangle;

namespace OledGuard;

internal sealed class ScreenSampler : IDisposable
{
    private readonly DrawingRectangle _sourceBounds;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _stride;
    private readonly byte[] _managedBuffer;
    private readonly object _sync = new();

    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private bool _disposed;

    public ScreenSampler(DrawingRectangle sourceBounds, int sampleWidth, int sampleHeight)
    {
        _sourceBounds = sourceBounds;
        _sampleWidth = sampleWidth;
        _sampleHeight = sampleHeight;
        _stride = checked(sampleWidth * 4);
        _managedBuffer = new byte[checked(_stride * sampleHeight)];

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("Impossible d'obtenir le contexte graphique du bureau.");
        }

        _memoryDc = NativeMethods.CreateCompatibleDC(_screenDc);
        if (_memoryDc == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("Impossible de créer le tampon de capture.");
        }

        var bitmapInfo = new NativeMethods.BitmapInfo
        {
            bmiHeader = new NativeMethods.BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                biWidth = sampleWidth,
                biHeight = -sampleHeight,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BiRgb,
                biSizeImage = (uint)(_stride * sampleHeight)
            }
        };

        _bitmap = NativeMethods.CreateDIBSection(
            _screenDc,
            ref bitmapInfo,
            NativeMethods.DibRgbColors,
            out _bits,
            IntPtr.Zero,
            0);

        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("Impossible de créer la surface de capture.");
        }

        _oldBitmap = NativeMethods.SelectObject(_memoryDc, _bitmap);
        NativeMethods.SetStretchBltMode(_memoryDc, NativeMethods.Halftone);
        NativeMethods.SetBrushOrgEx(_memoryDc, 0, 0, IntPtr.Zero);
    }

    public byte[] Capture()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var success = NativeMethods.StretchBlt(
                _memoryDc,
                0,
                0,
                _sampleWidth,
                _sampleHeight,
                _screenDc,
                _sourceBounds.Left,
                _sourceBounds.Top,
                _sourceBounds.Width,
                _sourceBounds.Height,
                NativeMethods.Srccopy);

            if (!success)
            {
                throw new InvalidOperationException("La capture d'écran a échoué.");
            }

            Marshal.Copy(_bits, _managedBuffer, 0, _managedBuffer.Length);
            return _managedBuffer;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_memoryDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memoryDc, _oldBitmap);
                _oldBitmap = IntPtr.Zero;
            }

            if (_bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }

            if (_memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }

            if (_screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }
        }
    }
}
