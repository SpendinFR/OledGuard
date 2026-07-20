using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfImage = System.Windows.Controls.Image;

namespace OledGuard;

/// <summary>
/// Renders the protection map as a tiny alpha bitmap stretched over the monitor.
/// The bitmap stays small (roughly one pixel per 16 screen pixels), and WPF lets
/// the GPU scale it smoothly instead of drawing thousands of visible rectangles.
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
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
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
