using System.Windows;

namespace OledGuard;

internal sealed partial class MonitorSession
{
    private sealed class InteractionRevealRegion
    {
        public int MinimumRow;
        public int MaximumRow;
        public int MinimumColumn;
        public int MaximumColumn;
        public int SeedRow;
        public int SeedColumn;
        public long LastActivityTicks;
        public long CompletionUntilTicks;
        public int DimStep;
        public byte[]? ReferenceFrame;
    }

    private readonly List<InteractionRevealRegion>
        _interactionRevealRegions = new();
    private readonly List<Rect>
        _manualRevealZones = new();

    private bool[]? _interactionWeakMotion;
    private bool[]? _interactionVisited;
    private int[]? _interactionQueue;

    public void SetManualRevealZones(
        IReadOnlyList<Rect> zones)
    {
        lock (_sync)
        {
            _manualRevealZones.Clear();

            foreach (var zone in zones)
            {
                var left =
                    Math.Clamp(
                        zone.Left,
                        0.0,
                        1.0);
                var top =
                    Math.Clamp(
                        zone.Top,
                        0.0,
                        1.0);
                var right =
                    Math.Clamp(
                        zone.Right,
                        0.0,
                        1.0);
                var bottom =
                    Math.Clamp(
                        zone.Bottom,
                        0.0,
                        1.0);

                if (right - left < 0.002 ||
                    bottom - top < 0.002)
                {
                    continue;
                }

                _manualRevealZones.Add(
                    new Rect(
                        left,
                        top,
                        right - left,
                        bottom - top));
            }

            _maskDirty = true;
        }
    }

    private void ResetInteractionAssistance()
    {
        _interactionRevealRegions.Clear();
    }

    private bool UpdateInteractionAssistance(
        byte[] current,
        long now)
    {
        if (!_settings.InteractionAssistEnabled)
        {
            if (_interactionRevealRegions.Count == 0)
            {
                return false;
            }

            _interactionRevealRegions.Clear();
            return true;
        }

        var changed =
            CompleteInteractionReveals(
                current,
                now);

        foreach (var detected in
                 _detectedRegions)
        {
            if (!IsInteractionSeed(
                    detected))
            {
                continue;
            }

            if (ArmInteractionReveal(
                    detected,
                    now))
            {
                changed = true;
            }
        }

        return changed;
    }

    private bool IsInteractionSeed(
        DetectedRegion detected)
    {
        if (!IsCursorNearDetectedRegion(
                detected,
                36))
        {
            return false;
        }

        var bounds =
            _screen.Bounds;
        var widthPixels =
            detected.Width *
            bounds.Width /
            (double)Math.Max(
                1,
                _columns);
        var heightPixels =
            detected.Height *
            bounds.Height /
            (double)Math.Max(
                1,
                _rows);
        var areaPixels =
            widthPixels *
            heightPixels;
        var screenArea =
            Math.Max(
                1.0,
                (double)bounds.Width *
                bounds.Height);
        var compactLimit =
            Math.Max(
                22_000.0,
                screenArea *
                0.006);
        var compact =
            areaPixels <= compactLimit &&
            Math.Max(
                widthPixels,
                heightPixels) <= 260.0;
        var thinControl =
            Math.Min(
                widthPixels,
                heightPixels) <= 52.0 &&
            areaPixels <= 90_000.0;

        return compact ||
               thinControl;
    }

    private bool ArmInteractionReveal(
        DetectedRegion detected,
        long now)
    {
        var candidate =
            CreateImmediateInteractionBounds(
                detected);
        var completionTicks =
            ToStopwatchTicks(
                _settings
                    .InteractionAssistCompletionMilliseconds);

        foreach (var existing in
                 _interactionRevealRegions)
        {
            if (!RectanglesNear(
                    existing.MinimumRow,
                    existing.MaximumRow,
                    existing.MinimumColumn,
                    existing.MaximumColumn,
                    candidate.MinimumRow,
                    candidate.MaximumRow,
                    candidate.MinimumColumn,
                    candidate.MaximumColumn,
                    2))
            {
                continue;
            }

            var changed =
                existing.MinimumRow !=
                    Math.Min(
                        existing.MinimumRow,
                        candidate.MinimumRow) ||
                existing.MaximumRow !=
                    Math.Max(
                        existing.MaximumRow,
                        candidate.MaximumRow) ||
                existing.MinimumColumn !=
                    Math.Min(
                        existing.MinimumColumn,
                        candidate.MinimumColumn) ||
                existing.MaximumColumn !=
                    Math.Max(
                        existing.MaximumColumn,
                        candidate.MaximumColumn) ||
                existing.DimStep != 0;

            existing.MinimumRow =
                Math.Min(
                    existing.MinimumRow,
                    candidate.MinimumRow);
            existing.MaximumRow =
                Math.Max(
                    existing.MaximumRow,
                    candidate.MaximumRow);
            existing.MinimumColumn =
                Math.Min(
                    existing.MinimumColumn,
                    candidate.MinimumColumn);
            existing.MaximumColumn =
                Math.Max(
                    existing.MaximumColumn,
                    candidate.MaximumColumn);
            existing.LastActivityTicks =
                now;
            existing.DimStep = 0;

            // Keep the first pre-animation frame fixed during the short
            // completion window. Replacing it every 20 ms would lose the
            // accumulated fade that this layer is meant to recover.
            if (existing.ReferenceFrame is null ||
                now >
                    existing.CompletionUntilTicks)
            {
                existing.CompletionUntilTicks =
                    now +
                    completionTicks;
                existing.ReferenceFrame =
                    ClonePreviousFrame();
            }

            return changed;
        }

        var centerRow =
            (candidate.MinimumRow +
             candidate.MaximumRow) /
            2;
        var centerColumn =
            (candidate.MinimumColumn +
             candidate.MaximumColumn) /
            2;

        _interactionRevealRegions.Add(
            new InteractionRevealRegion
            {
                MinimumRow =
                    candidate.MinimumRow,
                MaximumRow =
                    candidate.MaximumRow,
                MinimumColumn =
                    candidate.MinimumColumn,
                MaximumColumn =
                    candidate.MaximumColumn,
                SeedRow = centerRow,
                SeedColumn = centerColumn,
                LastActivityTicks = now,
                CompletionUntilTicks =
                    now +
                    completionTicks,
                DimStep = 0,
                ReferenceFrame =
                    ClonePreviousFrame()
            });

        if (_interactionRevealRegions.Count > 8)
        {
            var oldest =
                _interactionRevealRegions
                    .OrderBy(
                        region =>
                            region.LastActivityTicks)
                    .First();
            _interactionRevealRegions.Remove(
                oldest);
        }

        return true;
    }

    private DetectedRegion CreateImmediateInteractionBounds(
        DetectedRegion detected)
    {
        var bounds =
            _screen.Bounds;
        var paddingColumns =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    18.0 *
                    _columns /
                    Math.Max(
                        1.0,
                        bounds.Width)));
        var paddingRows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    18.0 *
                    _rows /
                    Math.Max(
                        1.0,
                        bounds.Height)));
        var minimumColumns =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    58.0 *
                    _columns /
                    Math.Max(
                        1.0,
                        bounds.Width)));
        var minimumRows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    58.0 *
                    _rows /
                    Math.Max(
                        1.0,
                        bounds.Height)));

        var minimumRow =
            Math.Max(
                0,
                detected.MinimumRow -
                paddingRows);
        var maximumRow =
            Math.Min(
                _rows - 1,
                detected.MaximumRow +
                paddingRows);
        var minimumColumn =
            Math.Max(
                0,
                detected.MinimumColumn -
                paddingColumns);
        var maximumColumn =
            Math.Min(
                _columns - 1,
                detected.MaximumColumn +
                paddingColumns);

        ExpandToMinimumSize(
            ref minimumRow,
            ref maximumRow,
            minimumRows,
            _rows);
        ExpandToMinimumSize(
            ref minimumColumn,
            ref maximumColumn,
            minimumColumns,
            _columns);

        return new DetectedRegion(
            minimumRow,
            maximumRow,
            minimumColumn,
            maximumColumn,
            detected.MotionCells);
    }

    private static void ExpandToMinimumSize(
        ref int minimum,
        ref int maximum,
        int targetSize,
        int limit)
    {
        var currentSize =
            maximum -
            minimum +
            1;

        if (currentSize >= targetSize)
        {
            return;
        }

        var missing =
            targetSize -
            currentSize;
        var before =
            missing /
            2;
        var after =
            missing -
            before;

        minimum =
            Math.Max(
                0,
                minimum -
                before);
        maximum =
            Math.Min(
                limit - 1,
                maximum +
                after);

        currentSize =
            maximum -
            minimum +
            1;

        if (currentSize >= targetSize)
        {
            return;
        }

        if (minimum == 0)
        {
            maximum =
                Math.Min(
                    limit - 1,
                    targetSize - 1);
        }
        else if (maximum == limit - 1)
        {
            minimum =
                Math.Max(
                    0,
                    limit -
                    targetSize);
        }
    }

    private byte[] ClonePreviousFrame()
    {
        var clone =
            new byte[_previousFrame.Length];
        Buffer.BlockCopy(
            _previousFrame,
            0,
            clone,
            0,
            clone.Length);
        return clone;
    }

    private bool CompleteInteractionReveals(
        byte[] current,
        long now)
    {
        var changed = false;

        foreach (var reveal in
                 _interactionRevealRegions)
        {
            if (reveal.ReferenceFrame is null)
            {
                continue;
            }

            if (now >
                reveal.CompletionUntilTicks)
            {
                reveal.ReferenceFrame = null;
                continue;
            }

            if (TryCompleteInteractionReveal(
                    reveal,
                    current))
            {
                reveal.LastActivityTicks =
                    now;
                reveal.DimStep = 0;
                changed = true;
            }
        }

        return changed;
    }

    private bool TryCompleteInteractionReveal(
        InteractionRevealRegion reveal,
        byte[] current)
    {
        EnsureInteractionBuffers();

        var bounds =
            _screen.Bounds;
        var horizontalRadius =
            Math.Max(
                4,
                (int)Math.Ceiling(
                    340.0 *
                    _columns /
                    Math.Max(
                        1.0,
                        bounds.Width)));
        var verticalRadius =
            Math.Max(
                4,
                (int)Math.Ceiling(
                    520.0 *
                    _rows /
                    Math.Max(
                        1.0,
                        bounds.Height)));
        var searchMinimumRow =
            Math.Max(
                0,
                reveal.SeedRow -
                verticalRadius);
        var searchMaximumRow =
            Math.Min(
                _rows - 1,
                reveal.SeedRow +
                verticalRadius);
        var searchMinimumColumn =
            Math.Max(
                0,
                reveal.SeedColumn -
                horizontalRadius);
        var searchMaximumColumn =
            Math.Min(
                _columns - 1,
                reveal.SeedColumn +
                horizontalRadius);

        for (var row = searchMinimumRow;
             row <= searchMaximumRow;
             row++)
        {
            for (var column = searchMinimumColumn;
                 column <= searchMaximumColumn;
                 column++)
            {
                var index =
                    row *
                    _columns +
                    column;
                _interactionVisited![index] = false;
                _interactionWeakMotion![index] =
                    CellChangedAgainstReference(
                        current,
                        reveal.ReferenceFrame!,
                        row,
                        column,
                        _settings
                            .InteractionAssistWeakPixelThreshold);
            }
        }

        var head = 0;
        var tail = 0;
        var seedMargin = 2;

        for (var row =
                 Math.Max(
                     searchMinimumRow,
                     reveal.MinimumRow -
                     seedMargin);
             row <=
                 Math.Min(
                     searchMaximumRow,
                     reveal.MaximumRow +
                     seedMargin);
             row++)
        {
            for (var column =
                     Math.Max(
                         searchMinimumColumn,
                         reveal.MinimumColumn -
                         seedMargin);
                 column <=
                     Math.Min(
                         searchMaximumColumn,
                         reveal.MaximumColumn +
                         seedMargin);
                 column++)
            {
                var index =
                    row *
                    _columns +
                    column;

                if (!_interactionWeakMotion![index] ||
                    _interactionVisited![index])
                {
                    continue;
                }

                _interactionVisited[index] = true;
                _interactionQueue![tail++] = index;
            }
        }

        if (tail == 0)
        {
            return false;
        }

        var minimumRow =
            reveal.MinimumRow;
        var maximumRow =
            reveal.MaximumRow;
        var minimumColumn =
            reveal.MinimumColumn;
        var maximumColumn =
            reveal.MaximumColumn;
        var visitedCells = 0;

        while (head < tail)
        {
            var index =
                _interactionQueue![head++];
            var row =
                index /
                _columns;
            var column =
                index %
                _columns;

            minimumRow =
                Math.Min(
                    minimumRow,
                    row);
            maximumRow =
                Math.Max(
                    maximumRow,
                    row);
            minimumColumn =
                Math.Min(
                    minimumColumn,
                    column);
            maximumColumn =
                Math.Max(
                    maximumColumn,
                    column);
            visitedCells++;

            for (var offsetY = -1;
                 offsetY <= 1;
                 offsetY++)
            {
                var neighbourRow =
                    row +
                    offsetY;

                if (neighbourRow <
                        searchMinimumRow ||
                    neighbourRow >
                        searchMaximumRow)
                {
                    continue;
                }

                for (var offsetX = -1;
                     offsetX <= 1;
                     offsetX++)
                {
                    if (offsetX == 0 &&
                        offsetY == 0)
                    {
                        continue;
                    }

                    var neighbourColumn =
                        column +
                        offsetX;

                    if (neighbourColumn <
                            searchMinimumColumn ||
                        neighbourColumn >
                            searchMaximumColumn)
                    {
                        continue;
                    }

                    var neighbourIndex =
                        neighbourRow *
                        _columns +
                        neighbourColumn;

                    if (_interactionVisited![
                            neighbourIndex] ||
                        !_interactionWeakMotion![
                            neighbourIndex])
                    {
                        continue;
                    }

                    _interactionVisited[neighbourIndex] = true;
                    _interactionQueue![tail++] =
                        neighbourIndex;
                }
            }
        }

        if (visitedCells < 2 ||
            !CanUseInteractionBounds(
                minimumRow,
                maximumRow,
                minimumColumn,
                maximumColumn))
        {
            return false;
        }

        if (minimumRow == reveal.MinimumRow &&
            maximumRow == reveal.MaximumRow &&
            minimumColumn == reveal.MinimumColumn &&
            maximumColumn == reveal.MaximumColumn)
        {
            return false;
        }

        reveal.MinimumRow =
            minimumRow;
        reveal.MaximumRow =
            maximumRow;
        reveal.MinimumColumn =
            minimumColumn;
        reveal.MaximumColumn =
            maximumColumn;
        return true;
    }

    private bool CellChangedAgainstReference(
        byte[] current,
        byte[] reference,
        int row,
        int column,
        int pixelThreshold)
    {
        var sampleTop =
            row *
            _samplesPerCell;
        var sampleLeft =
            column *
            _samplesPerCell;
        var changedSamples = 0;
        var maximumDifference = 0;
        var differenceTotal = 0;
        var sampleCount =
            _samplesPerCell *
            _samplesPerCell;
        var minimumChangedSamples =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    sampleCount *
                    _settings
                        .MotionZoneChangedFraction));

        for (var sampleY = 0;
             sampleY < _samplesPerCell;
             sampleY++)
        {
            var sourceRow =
                sampleTop +
                sampleY;
            var rowOffset =
                sourceRow *
                _sampleStride;

            for (var sampleX = 0;
                 sampleX < _samplesPerCell;
                 sampleX++)
            {
                var sourceColumn =
                    sampleLeft +
                    sampleX;
                var offset =
                    rowOffset +
                    sourceColumn *
                    4;
                var blueDifference =
                    Math.Abs(
                        current[offset] -
                        reference[offset]);
                var greenDifference =
                    Math.Abs(
                        current[offset + 1] -
                        reference[offset + 1]);
                var redDifference =
                    Math.Abs(
                        current[offset + 2] -
                        reference[offset + 2]);
                var difference =
                    Math.Max(
                        blueDifference,
                        Math.Max(
                            greenDifference,
                            redDifference));

                differenceTotal +=
                    difference;
                maximumDifference =
                    Math.Max(
                        maximumDifference,
                        difference);

                if (difference >=
                    pixelThreshold)
                {
                    changedSamples++;
                }
            }
        }

        var meanDifference =
            differenceTotal /
            (double)sampleCount;

        return changedSamples >=
                   minimumChangedSamples ||
               maximumDifference >=
                   pixelThreshold *
                   2 ||
               meanDifference >=
                   pixelThreshold *
                   0.60;
    }

    private void EnsureInteractionBuffers()
    {
        var cellCount =
            _columns *
            _rows;

        if (_interactionWeakMotion is null ||
            _interactionWeakMotion.Length !=
                cellCount)
        {
            _interactionWeakMotion =
                new bool[cellCount];
            _interactionVisited =
                new bool[cellCount];
            _interactionQueue =
                new int[cellCount];
        }
    }

    private bool CanUseInteractionBounds(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn)
    {
        var bounds =
            _screen.Bounds;
        var widthPixels =
            (maximumColumn -
             minimumColumn +
             1) *
            bounds.Width /
            (double)Math.Max(
                1,
                _columns);
        var heightPixels =
            (maximumRow -
             minimumRow +
             1) *
            bounds.Height /
            (double)Math.Max(
                1,
                _rows);
        var areaPixels =
            widthPixels *
            heightPixels;
        var screenArea =
            Math.Max(
                1.0,
                (double)bounds.Width *
                bounds.Height);
        var maximumArea =
            Math.Max(
                320_000.0,
                screenArea *
                0.04);

        return widthPixels <= 720.0 &&
               heightPixels <= 1_100.0 &&
               areaPixels <= maximumArea;
    }

    private bool UpdateInteractionVisualStates(
        long now)
    {
        if (_interactionRevealRegions.Count == 0)
        {
            return false;
        }

        var changed = false;
        var holdTicks =
            ToStopwatchTicks(
                _settings
                    .InteractionAssistHoldMilliseconds);
        var fadeTicks =
            ToStopwatchTicks(
                _settings
                    .InteractionAssistFadeMilliseconds);
        var dimSteps =
            Math.Max(
                2,
                _settings
                    .MotionZoneDimSteps);
        var hasCursor =
            TryGetCursorCell(
                out var cursorRow,
                out var cursorColumn);

        for (var index =
                 _interactionRevealRegions.Count - 1;
             index >= 0;
             index--)
        {
            var reveal =
                _interactionRevealRegions[index];

            if (hasCursor &&
                cursorRow >=
                    reveal.MinimumRow - 1 &&
                cursorRow <=
                    reveal.MaximumRow + 1 &&
                cursorColumn >=
                    reveal.MinimumColumn - 1 &&
                cursorColumn <=
                    reveal.MaximumColumn + 1)
            {
                reveal.LastActivityTicks =
                    now;

                if (reveal.DimStep != 0)
                {
                    reveal.DimStep = 0;
                    changed = true;
                }

                continue;
            }

            var elapsed =
                now -
                reveal.LastActivityTicks;

            if (elapsed < holdTicks)
            {
                if (reveal.DimStep != 0)
                {
                    reveal.DimStep = 0;
                    changed = true;
                }

                continue;
            }

            if (fadeTicks <= 0 ||
                elapsed >=
                    holdTicks +
                    fadeTicks)
            {
                _interactionRevealRegions.RemoveAt(
                    index);
                changed = true;
                continue;
            }

            var fadeElapsed =
                elapsed -
                holdTicks;
            var targetStep =
                Math.Clamp(
                    1 +
                    (int)(
                        fadeElapsed *
                        dimSteps /
                        Math.Max(
                            1L,
                            fadeTicks)),
                    1,
                    dimSteps);

            if (targetStep !=
                reveal.DimStep)
            {
                reveal.DimStep =
                    targetStep;
                changed = true;
            }
        }

        return changed;
    }

    private bool TryGetCursorCell(
        out int row,
        out int column)
    {
        row = 0;
        column = 0;

        if (!NativeMethods.GetCursorPos(
                out var cursor))
        {
            return false;
        }

        var bounds =
            _screen.Bounds;

        if (!bounds.Contains(
                cursor.X,
                cursor.Y))
        {
            return false;
        }

        column =
            Math.Clamp(
                (cursor.X -
                 bounds.Left) *
                _columns /
                Math.Max(
                    1,
                    bounds.Width),
                0,
                _columns - 1);
        row =
            Math.Clamp(
                (cursor.Y -
                 bounds.Top) *
                _rows /
                Math.Max(
                    1,
                    bounds.Height),
                0,
                _rows - 1);
        return true;
    }

    private void AppendSupplementalMaskRegions(
        List<MaskRegion> result,
        int dimSteps,
        double maximumOpacity)
    {
        foreach (var reveal in
                 _interactionRevealRegions)
        {
            var left =
                reveal.MinimumColumn /
                (double)_columns;
            var top =
                reveal.MinimumRow /
                (double)_rows;
            var right =
                (reveal.MaximumColumn + 1) /
                (double)_columns;
            var bottom =
                (reveal.MaximumRow + 1) /
                (double)_rows;
            var opacity =
                maximumOpacity *
                Math.Clamp(
                    reveal.DimStep,
                    0,
                    dimSteps) /
                dimSteps;

            result.Add(
                new MaskRegion(
                    new Rect(
                        left,
                        top,
                        right - left,
                        bottom - top),
                    opacity));
        }

        foreach (var manual in
                 _manualRevealZones)
        {
            result.Add(
                new MaskRegion(
                    manual,
                    0.0));
        }
    }

    private bool IsPointInsideSupplementalReveal(
        int row,
        int column)
    {
        foreach (var reveal in
                 _interactionRevealRegions)
        {
            if (reveal.DimStep == 0 &&
                row >= reveal.MinimumRow &&
                row <= reveal.MaximumRow &&
                column >= reveal.MinimumColumn &&
                column <= reveal.MaximumColumn)
            {
                return true;
            }
        }

        var normalizedX =
            (column + 0.5) /
            Math.Max(
                1.0,
                _columns);
        var normalizedY =
            (row + 0.5) /
            Math.Max(
                1.0,
                _rows);

        foreach (var manual in
                 _manualRevealZones)
        {
            if (manual.Contains(
                    normalizedX,
                    normalizedY))
            {
                return true;
            }
        }

        return false;
    }
}