using System.Windows;
using System.Windows.Media;

namespace OledGuard;

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
        _brushes = new SolidColorBrush[33];

        for (var i = 0; i < _brushes.Length; i++)
        {
            var alpha = (byte)Math.Round(i * 255.0 / (_brushes.Length - 1));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
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

        if (_columns <= 0 || _rows <= 0 || _alpha.Length != _columns * _rows || ActualWidth <= 0 || ActualHeight <= 0)
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

                var left = runStart * cellWidth;
                var right = column == _columns ? ActualWidth : column * cellWidth;

                // A tiny overlap prevents hairline gaps caused by fractional DPI coordinates.
                var rect = new Rect(left - 0.35, top - 0.35, (right - left) + 0.7, (bottom - top) + 0.7);
                drawingContext.DrawRectangle(_brushes[brushIndex], null, rect);
            }
        }
    }

    private int GetBrushIndex(float value)
    {
        if (value <= 0.01f)
        {
            return 0;
        }

        return Math.Clamp((int)MathF.Round(value * (_brushes.Length - 1)), 1, _brushes.Length - 1);
    }
}
