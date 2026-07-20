using System.Windows;
using System.Windows.Media;

namespace OledGuard;

/// <summary>
/// Draws a crisp square grid with temporal alpha fades. There is deliberately
/// no bitmap interpolation or spatial blur: this recreates the clean original
/// OledGuard look while keeping the cells smaller.
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
        UseLayoutRounding = true;

        // 65 cached alpha levels make the fade smooth without allocating brushes
        // every frame. The grid itself remains perfectly square.
        _brushes = new SolidColorBrush[65];
        for (var i = 0; i < _brushes.Length; i++)
        {
            var alpha = (byte)Math.Round(i * 255.0 / (_brushes.Length - 1));
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 0, 0, 0));
            brush.Freeze();
            _brushes[i] = brush;
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

        if (_columns <= 0 || _rows <= 0 ||
            _alpha.Length != _columns * _rows ||
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

            // Merge adjacent squares with the same alpha into one horizontal run.
            // This reduces draw calls while preserving crisp square boundaries.
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
                while (column < _columns &&
                       GetBrushIndex(_alpha[row * _columns + column]) == brushIndex)
                {
                    column++;
                }

                var left = runStart * cellWidth;
                var right = column == _columns ? ActualWidth : column * cellWidth;

                // Small overlap prevents bright hairline gaps at non-integer DPI.
                var rectangle = new Rect(
                    left - 0.4,
                    top - 0.4,
                    (right - left) + 0.8,
                    (bottom - top) + 0.8);

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
