using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace OledGuardSimple;

internal sealed class PixelDetector
{
    private const int CellSize = 6;

    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly int _localWidth;
    private readonly int _localHeight;
    private readonly int _columns;
    private readonly int _rows;

    private readonly bool[] _changedCells;
    private readonly bool[] _visitedCells;
    private readonly int[] _queue;

    public PixelDetector(
        int sampleWidth,
        int sampleHeight,
        int sampleStride,
        int localWidth,
        int localHeight)
    {
        _sampleWidth = sampleWidth;
        _sampleHeight = sampleHeight;
        _sampleStride = sampleStride;
        _localWidth = localWidth;
        _localHeight = localHeight;

        _columns = Math.Max(
            1,
            (int)Math.Ceiling(
                sampleWidth / (double)CellSize));

        _rows = Math.Max(
            1,
            (int)Math.Ceiling(
                sampleHeight / (double)CellSize));

        var cellCount = checked(_columns * _rows);
        _changedCells = new bool[cellCount];
        _visitedCells = new bool[cellCount];
        _queue = new int[cellCount];
    }

    public DetectionResult Detect(
        byte[] previousFrame,
        byte[] currentFrame,
        DrawingPoint? cursorLocal)
    {
        Array.Clear(
            _changedCells,
            0,
            _changedCells.Length);

        var changedCellCount = DetectChangedCells(
            previousFrame,
            currentFrame,
            cursorLocal);

        var components = BuildConnectedComponents(
            cursorLocal);

        return new DetectionResult(
            components,
            changedCellCount /
            (double)Math.Max(1, _changedCells.Length));
    }

    private int DetectChangedCells(
        byte[] previousFrame,
        byte[] currentFrame,
        DrawingPoint? cursorLocal)
    {
        const int normalThreshold = 8;
        var changedCellCount = 0;

        for (var row = 0; row < _rows; row++)
        {
            var top = row * CellSize;
            var bottom = Math.Min(
                _sampleHeight - 1,
                top + CellSize - 1);

            for (var column = 0; column < _columns; column++)
            {
                var left = column * CellSize;
                var right = Math.Min(
                    _sampleWidth - 1,
                    left + CellSize - 1);

                var centerX = (left + right) / 2;
                var centerY = (top + bottom) / 2;

                var nearCursor = IsCellNearCursor(
                    left,
                    top,
                    right,
                    bottom,
                    cursorLocal);

                var threshold = nearCursor
                    ? 5
                    : normalThreshold;

                var changedSamples = 0;

                changedSamples += PixelChanged(
                    previousFrame,
                    currentFrame,
                    left,
                    top,
                    threshold)
                    ? 1
                    : 0;

                changedSamples += PixelChanged(
                    previousFrame,
                    currentFrame,
                    right,
                    top,
                    threshold)
                    ? 1
                    : 0;

                changedSamples += PixelChanged(
                    previousFrame,
                    currentFrame,
                    left,
                    bottom,
                    threshold)
                    ? 1
                    : 0;

                changedSamples += PixelChanged(
                    previousFrame,
                    currentFrame,
                    right,
                    bottom,
                    threshold)
                    ? 1
                    : 0;

                changedSamples += PixelChanged(
                    previousFrame,
                    currentFrame,
                    centerX,
                    centerY,
                    threshold)
                    ? 1
                    : 0;

                var changed = nearCursor
                    ? changedSamples >= 1
                    : changedSamples >= 2;

                if (!changed)
                {
                    continue;
                }

                _changedCells[
                    row * _columns + column] = true;

                changedCellCount++;
            }
        }

        return changedCellCount;
    }

    private bool IsCellNearCursor(
        int left,
        int top,
        int right,
        int bottom,
        DrawingPoint? cursorLocal)
    {
        if (cursorLocal is null)
        {
            return false;
        }

        var sampleCursorX = cursorLocal.Value.X *
            _sampleWidth /
            Math.Max(1.0, _localWidth);

        var sampleCursorY = cursorLocal.Value.Y *
            _sampleHeight /
            Math.Max(1.0, _localHeight);

        var margin = 18.0 *
            _sampleWidth /
            Math.Max(1.0, _localWidth);

        return sampleCursorX >= left - margin &&
               sampleCursorX <= right + margin &&
               sampleCursorY >= top - margin &&
               sampleCursorY <= bottom + margin;
    }

    private bool PixelChanged(
        byte[] previousFrame,
        byte[] currentFrame,
        int x,
        int y,
        int threshold)
    {
        var index = y * _sampleStride + x * 4;

        var blue = Math.Abs(
            currentFrame[index] - previousFrame[index]);

        var green = Math.Abs(
            currentFrame[index + 1] - previousFrame[index + 1]);

        var red = Math.Abs(
            currentFrame[index + 2] - previousFrame[index + 2]);

        return Math.Max(
                   blue,
                   Math.Max(green, red)) >= threshold;
    }

    private List<DrawingRectangle> BuildConnectedComponents(
        DrawingPoint? cursorLocal)
    {
        Array.Clear(
            _visitedCells,
            0,
            _visitedCells.Length);

        var result = new List<DrawingRectangle>();

        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var startIndex = row * _columns + column;

                if (!_changedCells[startIndex] ||
                    _visitedCells[startIndex])
                {
                    continue;
                }

                var head = 0;
                var tail = 0;
                _queue[tail++] = startIndex;
                _visitedCells[startIndex] = true;

                var minimumRow = row;
                var maximumRow = row;
                var minimumColumn = column;
                var maximumColumn = column;
                var cellCount = 0;

                while (head < tail)
                {
                    var index = _queue[head++];
                    var currentRow = index / _columns;
                    var currentColumn = index % _columns;
                    cellCount++;

                    minimumRow = Math.Min(
                        minimumRow,
                        currentRow);

                    maximumRow = Math.Max(
                        maximumRow,
                        currentRow);

                    minimumColumn = Math.Min(
                        minimumColumn,
                        currentColumn);

                    maximumColumn = Math.Max(
                        maximumColumn,
                        currentColumn);

                    for (var rowOffset = -1;
                         rowOffset <= 1;
                         rowOffset++)
                    {
                        for (var columnOffset = -1;
                             columnOffset <= 1;
                             columnOffset++)
                        {
                            if (rowOffset == 0 &&
                                columnOffset == 0)
                            {
                                continue;
                            }

                            var nextRow = currentRow + rowOffset;
                            var nextColumn = currentColumn + columnOffset;

                            if (nextRow < 0 ||
                                nextRow >= _rows ||
                                nextColumn < 0 ||
                                nextColumn >= _columns)
                            {
                                continue;
                            }

                            var nextIndex =
                                nextRow * _columns + nextColumn;

                            if (!_changedCells[nextIndex] ||
                                _visitedCells[nextIndex])
                            {
                                continue;
                            }

                            _visitedCells[nextIndex] = true;
                            _queue[tail++] = nextIndex;
                        }
                    }
                }

                var bounds = CellsToLocalRectangle(
                    minimumRow,
                    maximumRow,
                    minimumColumn,
                    maximumColumn);

                var cursorNear = cursorLocal is not null &&
                    Expanded(bounds, 20).Contains(cursorLocal.Value);

                if (cellCount < 2 && !cursorNear)
                {
                    continue;
                }

                if (bounds.Width < 5 ||
                    bounds.Height < 5)
                {
                    continue;
                }

                result.Add(bounds);
            }
        }

        return result;
    }

    private DrawingRectangle CellsToLocalRectangle(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn)
    {
        var sampleLeft = minimumColumn * CellSize;
        var sampleTop = minimumRow * CellSize;
        var sampleRight = Math.Min(
            _sampleWidth,
            (maximumColumn + 1) * CellSize);
        var sampleBottom = Math.Min(
            _sampleHeight,
            (maximumRow + 1) * CellSize);

        var left = (int)Math.Floor(
            sampleLeft * _localWidth /
            (double)Math.Max(1, _sampleWidth));

        var top = (int)Math.Floor(
            sampleTop * _localHeight /
            (double)Math.Max(1, _sampleHeight));

        var right = (int)Math.Ceiling(
            sampleRight * _localWidth /
            (double)Math.Max(1, _sampleWidth));

        var bottom = (int)Math.Ceiling(
            sampleBottom * _localHeight /
            (double)Math.Max(1, _sampleHeight));

        var rectangle = DrawingRectangle.FromLTRB(
            left,
            top,
            right,
            bottom);

        rectangle.Inflate(4, 4);

        return DrawingRectangle.Intersect(
            rectangle,
            new DrawingRectangle(
                0,
                0,
                _localWidth,
                _localHeight));
    }

    private static DrawingRectangle Expanded(
        DrawingRectangle rectangle,
        int pixels)
    {
        rectangle.Inflate(pixels, pixels);
        return rectangle;
    }
}
