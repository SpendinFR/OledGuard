using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OledGuard;

internal sealed class MaskSurface : FrameworkElement
{
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = Array.Empty<byte>();
    private int _columns;
    private int _rows;

    public MaskSurface()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public void UpdateMask(float[] alpha, int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || alpha.Length != columns * rows)
        {
            _bitmap = null;
            _pixels = Array.Empty<byte>();
            _columns = 0;
            _rows = 0;
            InvalidateVisual();
            return;
        }

        if (_bitmap is null || _columns != columns || _rows != rows)
        {
            _columns = columns;
            _rows = rows;
            _pixels = new byte[checked(columns * rows * 4)];
            _bitmap = new WriteableBitmap(
                columns,
                rows,
                96.0,
                96.0,
                PixelFormats.Bgra32,
                null);
        }

        for (var index = 0; index < alpha.Length; index++)
        {
            var offset = index * 4;
            _pixels[offset] = 0;
            _pixels[offset + 1] = 0;
            _pixels[offset + 2] = 0;
            _pixels[offset + 3] = (byte)Math.Clamp(
                (int)MathF.Round(Math.Clamp(alpha[index], 0f, 1f) * 255f),
                0,
                255);
        }

        _bitmap.WritePixels(
            new Int32Rect(0, 0, columns, rows),
            _pixels,
            checked(columns * 4),
            0);

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (_bitmap is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        drawingContext.DrawImage(
            _bitmap,
            new Rect(0, 0, ActualWidth, ActualHeight));
    }
}