using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfImage = System.Windows.Controls.Image;

namespace OledGuard;

/// <summary>
/// Renders a very small black-alpha map stretched over the monitor. The source
/// map is one pixel per analysis cell, so OledGuard never stores a 4K frame for
/// the overlay. Nearest-neighbour scaling preserves the deliberate square rings;
/// temporal animation provides the smooth fade without spatial blur.
/// </summary>
internal sealed class MaskSurface : WpfImage
{
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = Array.Empty<byte>();
    private int _columns;
    private int _rows;

    public MaskSurface()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Stretch = Stretch.Fill;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public void UpdateMask(float[] alpha, int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || alpha.Length != columns * rows)
        {
            Source = null;
            return;
        }

        EnsureBitmap(columns, rows);

        for (var index = 0; index < alpha.Length; index++)
        {
            var pixelOffset = index * 4;
            _pixels[pixelOffset] = 0;
            _pixels[pixelOffset + 1] = 0;
            _pixels[pixelOffset + 2] = 0;
            _pixels[pixelOffset + 3] = (byte)Math.Clamp(
                (int)MathF.Round(alpha[index] * 255f),
                0,
                255);
        }

        _bitmap!.WritePixels(
            new Int32Rect(0, 0, columns, rows),
            _pixels,
            columns * 4,
            0);
    }

    private void EnsureBitmap(int columns, int rows)
    {
        if (_bitmap is not null && _columns == columns && _rows == rows)
        {
            return;
        }

        _columns = columns;
        _rows = rows;
        _pixels = new byte[checked(columns * rows * 4)];
        _bitmap = new WriteableBitmap(
            columns,
            rows,
            96,
            96,
            PixelFormats.Pbgra32,
            null);
        Source = _bitmap;
    }
}
