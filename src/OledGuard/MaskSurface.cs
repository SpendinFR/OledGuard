using System.Windows;
using System.Windows.Media;

namespace OledGuard;

/// <summary>
/// Original cell renderer: no bitmap blur. Each fine cell is drawn with one of
/// many black opacity levels, creating a crisp pixelated temporal gradient.
/// </summary>
internal sealed class MaskSurface : FrameworkElement
{
    private readonly SolidColorBrush[] _brushes;
    private float[] _alpha = Array.Empty<float>();
    private int _columns;
    private int _rows;

    public MaskSurface()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _brushes = new SolidColorBrush[65];

        for (var index = 0; index < _brushes.Length; index++)
        {
            var alpha = (byte)Math.Round(index * 255.0 / (_brushes.Length - 1));
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 0, 0, 0));
            brush.Freeze();
            _brushes[index] = brush;
        }
    }

    public void UpdateMask(float[] alpha, int columns, int rows)
    {
        _alpha = alpha;
        _columns = columns;
        _rows = rows;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_columns <= 0 || _rows <= 0 || _alpha.Length != _columns * _rows ||
            ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var cellWidth = ActualWidth / _columns;
        var cellHeight = ActualHeight / _rows;

        for (var row = 0; row < _rows; row++)
        {
            var top = row * cellHeight;
            var bottom = row == _rows - 1 ? ActualHeight : (row + 1) * cellHeight;
            var column = 0;

            while (column < _columns)
            {
                var brushIndex = GetBrushIndex(_alpha[row * _columns + column]);
                if (brushIndex == 0)
                {
                    column++;
                    continue;
                }

                var runStart = column;
                column++;
                while (column < _columns && GetBrushIndex(_alpha[row * _columns + column]) == brushIndex)
                {
                    column++;
                }

                var left = Math.Floor(runStart * cellWidth);
                var right = Math.Ceiling(column == _columns ? ActualWidth : column * cellWidth);
                var alignedTop = Math.Floor(top);
                var alignedBottom = Math.Ceiling(bottom);
                var rectangle = new Rect(
                    left,
                    alignedTop,
                    Math.Max(1.0, right - left),
                    Math.Max(1.0, alignedBottom - alignedTop));
                drawingContext.DrawRectangle(_brushes[brushIndex], null, rectangle);
            }
        }
    }

    private int GetBrushIndex(float value)
    {
        if (value <= 0.005f)
        {
            return 0;
        }
        return Math.Clamp(
            (int)MathF.Round(value * (_brushes.Length - 1)),
            1,
            _brushes.Length - 1);
    }
}
