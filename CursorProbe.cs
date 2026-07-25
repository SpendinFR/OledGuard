using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace OledGuardSimple;

internal sealed class CursorProbe
{
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly int _localWidth;
    private readonly int _localHeight;
    private readonly int[] _marks;
    private readonly int[] _queue;
    private int _generation = 1;

    public CursorProbe(
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

        var pixels = checked(sampleWidth * sampleHeight);
        _marks = new int[pixels];
        _queue = new int[pixels];
    }

    public DrawingRectangle? Probe(
        byte[] frame,
        DrawingPoint cursorLocal)
    {
        var sampleX = Math.Clamp(
            (int)Math.Round(
                cursorLocal.X * _sampleWidth /
                Math.Max(1.0, _localWidth)),
            0,
            _sampleWidth - 1);

        var sampleY = Math.Clamp(
            (int)Math.Round(
                cursorLocal.Y * _sampleHeight /
                Math.Max(1.0, _localHeight)),
            0,
            _sampleHeight - 1);

        var colorRegion = ProbeSimilarColor(
            frame,
            sampleX,
            sampleY);

        var edgeRegion = ProbeNearbyEdge(
            frame,
            sampleX,
            sampleY);

        DrawingRectangle? selected = null;

        if (colorRegion is not null &&
            edgeRegion is not null &&
            colorRegion.Value.IntersectsWith(edgeRegion.Value))
        {
            selected = DrawingRectangle.Union(
                colorRegion.Value,
                edgeRegion.Value);
        }
        else if (colorRegion is not null)
        {
            selected = colorRegion;
        }
        else if (edgeRegion is not null)
        {
            selected = edgeRegion;
        }

        return selected is null
            ? null
            : SampleToLocal(selected.Value, 6);
    }

    private DrawingRectangle? ProbeSimilarColor(
        byte[] frame,
        int seedX,
        int seedY)
    {
        const int radiusX = 72;
        const int radiusY = 48;
        const int colorThreshold = 20;

        var left = Math.Max(0, seedX - radiusX);
        var top = Math.Max(0, seedY - radiusY);
        var right = Math.Min(_sampleWidth - 1, seedX + radiusX);
        var bottom = Math.Min(_sampleHeight - 1, seedY + radiusY);

        var seedIndex = seedY * _sampleStride + seedX * 4;
        var seedBlue = frame[seedIndex];
        var seedGreen = frame[seedIndex + 1];
        var seedRed = frame[seedIndex + 2];

        BeginGeneration();

        var head = 0;
        var tail = 0;
        var start = seedY * _sampleWidth + seedX;
        _queue[tail++] = start;
        _marks[start] = _generation;

        var minimumX = seedX;
        var maximumX = seedX;
        var minimumY = seedY;
        var maximumY = seedY;
        var count = 0;
        var touchedBorders = 0;
        var touchedLeft = false;
        var touchedRight = false;
        var touchedTop = false;
        var touchedBottom = false;

        while (head < tail)
        {
            var packed = _queue[head++];
            var x = packed % _sampleWidth;
            var y = packed / _sampleWidth;
            count++;

            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumY = Math.Max(maximumY, y);

            touchedLeft |= x == left;
            touchedRight |= x == right;
            touchedTop |= y == top;
            touchedBottom |= y == bottom;

            VisitColorNeighbor(
                frame,
                x - 1,
                y,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);

            VisitColorNeighbor(
                frame,
                x + 1,
                y,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);

            VisitColorNeighbor(
                frame,
                x,
                y - 1,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);

            VisitColorNeighbor(
                frame,
                x,
                y + 1,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);
        }

        touchedBorders += touchedLeft ? 1 : 0;
        touchedBorders += touchedRight ? 1 : 0;
        touchedBorders += touchedTop ? 1 : 0;
        touchedBorders += touchedBottom ? 1 : 0;

        var roiArea = (right - left + 1) * (bottom - top + 1);
        var width = maximumX - minimumX + 1;
        var height = maximumY - minimumY + 1;

        if (count < 12 ||
            count > roiArea * 0.45 ||
            touchedBorders >= 2 ||
            width < 3 ||
            height < 3)
        {
            return null;
        }

        return DrawingRectangle.FromLTRB(
            minimumX,
            minimumY,
            maximumX + 1,
            maximumY + 1);
    }

    private void VisitColorNeighbor(
        byte[] frame,
        int x,
        int y,
        int left,
        int top,
        int right,
        int bottom,
        byte seedBlue,
        byte seedGreen,
        byte seedRed,
        int threshold,
        ref int tail)
    {
        if (x < left ||
            x > right ||
            y < top ||
            y > bottom)
        {
            return;
        }

        var packed = y * _sampleWidth + x;

        if (_marks[packed] == _generation)
        {
            return;
        }

        var index = y * _sampleStride + x * 4;

        var difference = Math.Max(
            Math.Abs(frame[index] - seedBlue),
            Math.Max(
                Math.Abs(frame[index + 1] - seedGreen),
                Math.Abs(frame[index + 2] - seedRed)));

        if (difference > threshold)
        {
            return;
        }

        _marks[packed] = _generation;
        _queue[tail++] = packed;
    }

    private DrawingRectangle? ProbeNearbyEdge(
        byte[] frame,
        int cursorX,
        int cursorY)
    {
        const int searchRadius = 8;
        var bestDistance = int.MaxValue;
        var seedX = -1;
        var seedY = -1;

        for (var y = Math.Max(1, cursorY - searchRadius);
             y <= Math.Min(_sampleHeight - 2, cursorY + searchRadius);
             y++)
        {
            for (var x = Math.Max(1, cursorX - searchRadius);
                 x <= Math.Min(_sampleWidth - 2, cursorX + searchRadius);
                 x++)
            {
                if (!IsEdge(frame, x, y))
                {
                    continue;
                }

                var distance =
                    Math.Abs(x - cursorX) +
                    Math.Abs(y - cursorY);

                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                seedX = x;
                seedY = y;
            }
        }

        if (seedX < 0)
        {
            return null;
        }

        BeginGeneration();

        var head = 0;
        var tail = 0;
        var start = seedY * _sampleWidth + seedX;
        _queue[tail++] = start;
        _marks[start] = _generation;

        var minimumX = seedX;
        var maximumX = seedX;
        var minimumY = seedY;
        var maximumY = seedY;
        var count = 0;

        while (head < tail)
        {
            var packed = _queue[head++];
            var x = packed % _sampleWidth;
            var y = packed / _sampleWidth;
            count++;

            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumY = Math.Max(maximumY, y);

            for (var yOffset = -1; yOffset <= 1; yOffset++)
            {
                for (var xOffset = -1; xOffset <= 1; xOffset++)
                {
                    if (xOffset == 0 && yOffset == 0)
                    {
                        continue;
                    }

                    var nextX = x + xOffset;
                    var nextY = y + yOffset;

                    if (nextX <= 0 ||
                        nextX >= _sampleWidth - 1 ||
                        nextY <= 0 ||
                        nextY >= _sampleHeight - 1)
                    {
                        continue;
                    }

                    var nextPacked = nextY * _sampleWidth + nextX;

                    if (_marks[nextPacked] == _generation ||
                        !IsEdge(frame, nextX, nextY))
                    {
                        continue;
                    }

                    _marks[nextPacked] = _generation;
                    _queue[tail++] = nextPacked;
                }
            }

            if (count > 4_000)
            {
                return null;
            }
        }

        var width = maximumX - minimumX + 1;
        var height = maximumY - minimumY + 1;

        if (count < 8 ||
            width < 3 ||
            height < 3 ||
            width > 140 ||
            height > 100)
        {
            return null;
        }

        return DrawingRectangle.FromLTRB(
            minimumX,
            minimumY,
            maximumX + 1,
            maximumY + 1);
    }

    private bool IsEdge(
        byte[] frame,
        int x,
        int y)
    {
        var center = y * _sampleStride + x * 4;
        var right = center + 4;
        var down = center + _sampleStride;

        var horizontal = Math.Max(
            Math.Abs(frame[center] - frame[right]),
            Math.Max(
                Math.Abs(frame[center + 1] - frame[right + 1]),
                Math.Abs(frame[center + 2] - frame[right + 2])));

        var vertical = Math.Max(
            Math.Abs(frame[center] - frame[down]),
            Math.Max(
                Math.Abs(frame[center + 1] - frame[down + 1]),
                Math.Abs(frame[center + 2] - frame[down + 2])));

        return Math.Max(horizontal, vertical) >= 18;
    }

    private DrawingRectangle SampleToLocal(
        DrawingRectangle sampleRectangle,
        int padding)
    {
        var left = (int)Math.Floor(
            sampleRectangle.Left * _localWidth /
            (double)Math.Max(1, _sampleWidth));

        var top = (int)Math.Floor(
            sampleRectangle.Top * _localHeight /
            (double)Math.Max(1, _sampleHeight));

        var right = (int)Math.Ceiling(
            sampleRectangle.Right * _localWidth /
            (double)Math.Max(1, _sampleWidth));

        var bottom = (int)Math.Ceiling(
            sampleRectangle.Bottom * _localHeight /
            (double)Math.Max(1, _sampleHeight));

        var result = DrawingRectangle.FromLTRB(
            left,
            top,
            right,
            bottom);

        result.Inflate(padding, padding);

        return DrawingRectangle.Intersect(
            result,
            new DrawingRectangle(
                0,
                0,
                _localWidth,
                _localHeight));
    }

    private void BeginGeneration()
    {
        _generation++;

        if (_generation != int.MaxValue)
        {
            return;
        }

        Array.Clear(_marks, 0, _marks.Length);
        _generation = 1;
    }
}
