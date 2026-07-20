using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OledGuard;

/// <summary>
/// Renders the complete alpha mask as one tiny bitmap. Scaling one surface avoids
/// the hairline seams produced by drawing thousands of adjacent rectangles.
/// </summary>
internal sealed class MaskSurface : FrameworkElement
{
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = Array.Empty<byte>();
    private int _columns;
    private int _rows;

    public MaskSurface()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
    }

    public void UpdateMask(float[] alpha, int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || alpha.Length != columns * rows)
        {
            return;
        }

        EnsureBitmap(columns, rows);
        var pixelCount = columns * rows;

        for (var index = 0; index < pixelCount; index++)
        {
            var offset = index * 4;
            var opacity = (byte)Math.Clamp((int)MathF.Round(alpha[index] * 255f), 0, 255);
            _pixels[offset] = 0;
            _pixels[offset + 1] = 0;
            _pixels[offset + 2] = 0;
            _pixels[offset + 3] = opacity;
        }

        _bitmap!.WritePixels(
            new Int32Rect(0, 0, columns, rows),
            _pixels,
            columns * 4,
            0);
        InvalidateVisual();
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
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_bitmap is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        drawingContext.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }
}
