using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace OledGuard;

internal sealed partial class MonitorSession
{
    private const uint GaRoot = 2;

    private sealed class InteractionRevealRegion
    {
        public int MinimumRow;
        public int MaximumRow;
        public int MinimumColumn;
        public int MaximumColumn;
        public long CreatedTicks;
        public long LastActivityTicks;
        public long CompletionUntilTicks;
        public int DimStep;
        public bool IsPanel;

        public int Width =>
            MaximumColumn -
            MinimumColumn +
            1;

        public int Height =>
            MaximumRow -
            MinimumRow +
            1;

        public int Area =>
            Math.Max(
                1,
                Width *
                Height);
    }

    private readonly List<InteractionRevealRegion>
        _interactionRevealRegions = new();
    private readonly List<Rect>
        _manualRevealZones = new();

    private InteractionRevealRegion?
        _pointerControlReveal;
    private byte[]?
        _interactionReferenceFrame;
    private long
        _interactionReferenceUntilTicks;

    private bool[]?
        _interactionWeakMotion;
    private bool[]?
        _interactionVisited;
    private int[]?
        _interactionQueue;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(
        NativeMethods.Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(
        IntPtr window,
        uint flags);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int maximumCharacters);

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
        _pointerControlReveal = null;
        _interactionReferenceUntilTicks = 0;
    }

    private bool UpdateInteractionAssistance(
        byte[] current,
        long now)
    {
        if (!_settings.InteractionAssistEnabled)
        {
            var hadInteractionState =
                _interactionRevealRegions.Count > 0 ||
                _pointerControlReveal is not null;

            ResetInteractionAssistance();
            return hadInteractionState;
        }

        var changed =
            CompleteInteractionReveals(
                current,
                now);

        foreach (var detected in
                 _detectedRegions)
        {
            if (!IsInteractionSeed(
                    detected,
                    out var isPanel))
            {
                continue;
            }

            if (ArmInteractionReveal(
                    detected,
                    isPanel,
                    now))
            {
                changed = true;
            }
        }

        if (_interactionRevealRegions.Count > 6)
        {
            var excess =
                _interactionRevealRegions.Count -
                6;

            foreach (var oldest in
                     _interactionRevealRegions
                         .OrderBy(
                             region =>
                                 region.LastActivityTicks)
                         .Take(excess)
                         .ToArray())
            {
                _interactionRevealRegions.Remove(
                    oldest);
                changed = true;
            }
        }

        return changed;
    }

    private bool IsInteractionSeed(
        DetectedRegion detected,
        out bool isPanel)
    {
        isPanel = false;

        if (!IsCursorNearDetectedRegion(
                detected,
                44))
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
        var minimumDimension =
            Math.Min(
                widthPixels,
                heightPixels);
        var maximumDimension =
            Math.Max(
                widthPixels,
                heightPixels);

        var compact =
            areaPixels <= 36_000.0 &&
            maximumDimension <= 360.0 &&
            minimumDimension <= 190.0;

        // Menus, flyouts and list panels often arrive as several separated
        // text/icon components. Accept a bounded panel around the cursor so
        // those components can be consolidated into one solid visual region.
        var panel =
            widthPixels <= 760.0 &&
            heightPixels <= 1_000.0 &&
            minimumDimension <= 420.0 &&
            areaPixels <= 280_000.0;

        isPanel =
            panel &&
            !compact;

        return compact ||
               panel;
    }

    private bool ArmInteractionReveal(
        DetectedRegion detected,
        bool isPanel,
        long now)
    {
        var candidate =
            CreateImmediateInteractionBounds(
                detected,
                isPanel);
        var completionTicks =
            ToStopwatchTicks(
                _settings
                    .InteractionAssistCompletionMilliseconds);

        EnsureInteractionReferenceSnapshot(
            now,
            completionTicks);

        InteractionRevealRegion? best =
            null;
        var bestScore =
            double.NegativeInfinity;
        var recentTicks =
            ToStopwatchTicks(
                180);

        foreach (var existing in
                 _interactionRevealRegions)
        {
            if (now -
                    existing.LastActivityTicks >
                recentTicks)
            {
                continue;
            }

            if (!TryGetMergedInteractionBounds(
                    existing,
                    candidate,
                    isPanel,
                    out var mergedMinimumRow,
                    out var mergedMaximumRow,
                    out var mergedMinimumColumn,
                    out var mergedMaximumColumn,
                    out var score))
            {
                continue;
            }

            if (score >
                bestScore)
            {
                best = existing;
                bestScore = score;
            }
        }

        if (best is not null)
        {
            var changed =
                best.MinimumRow !=
                    Math.Min(
                        best.MinimumRow,
                        candidate.MinimumRow) ||
                best.MaximumRow !=
                    Math.Max(
                        best.MaximumRow,
                        candidate.MaximumRow) ||
                best.MinimumColumn !=
                    Math.Min(
                        best.MinimumColumn,
                        candidate.MinimumColumn) ||
                best.MaximumColumn !=
                    Math.Max(
                        best.MaximumColumn,
                        candidate.MaximumColumn) ||
                best.DimStep != 0;

            best.MinimumRow =
                Math.Min(
                    best.MinimumRow,
                    candidate.MinimumRow);
            best.MaximumRow =
                Math.Max(
                    best.MaximumRow,
                    candidate.MaximumRow);
            best.MinimumColumn =
                Math.Min(
                    best.MinimumColumn,
                    candidate.MinimumColumn);
            best.MaximumColumn =
                Math.Max(
                    best.MaximumColumn,
                    candidate.MaximumColumn);
            best.IsPanel =
                best.IsPanel ||
                isPanel;
            best.LastActivityTicks =
                now;
            best.CompletionUntilTicks =
                now +
                completionTicks;
            best.DimStep = 0;
            return changed;
        }

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
                CreatedTicks = now,
                LastActivityTicks = now,
                CompletionUntilTicks =
                    now +
                    completionTicks,
                DimStep = 0,
                IsPanel = isPanel
            });

        return true;
    }

    private bool TryGetMergedInteractionBounds(
        InteractionRevealRegion existing,
        DetectedRegion candidate,
        bool candidateIsPanel,
        out int minimumRow,
        out int maximumRow,
        out int minimumColumn,
        out int maximumColumn,
        out double score)
    {
        minimumRow =
            Math.Min(
                existing.MinimumRow,
                candidate.MinimumRow);
        maximumRow =
            Math.Max(
                existing.MaximumRow,
                candidate.MaximumRow);
        minimumColumn =
            Math.Min(
                existing.MinimumColumn,
                candidate.MinimumColumn);
        maximumColumn =
            Math.Max(
                existing.MaximumColumn,
                candidate.MaximumColumn);
        score =
            double.NegativeInfinity;

        var rowOverlap =
            OverlapLength(
                existing.MinimumRow,
                existing.MaximumRow,
                candidate.MinimumRow,
                candidate.MaximumRow);
        var columnOverlap =
            OverlapLength(
                existing.MinimumColumn,
                existing.MaximumColumn,
                candidate.MinimumColumn,
                candidate.MaximumColumn);
        var rowGap =
            AxisGap(
                existing.MinimumRow,
                existing.MaximumRow,
                candidate.MinimumRow,
                candidate.MaximumRow);
        var columnGap =
            AxisGap(
                existing.MinimumColumn,
                existing.MaximumColumn,
                candidate.MinimumColumn,
                candidate.MaximumColumn);
        var rowAlignment =
            rowOverlap /
            (double)Math.Max(
                1,
                Math.Min(
                    existing.Height,
                    candidate.Height));
        var columnAlignment =
            columnOverlap /
            (double)Math.Max(
                1,
                Math.Min(
                    existing.Width,
                    candidate.Width));

        var bounds =
            _screen.Bounds;
        var compactGapColumns =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    12.0 *
                    _columns /
                    Math.Max(
                        1.0,
                        bounds.Width)));
        var compactGapRows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    10.0 *
                    _rows /
                    Math.Max(
                        1.0,
                        bounds.Height)));
        var panelGapColumns =
            Math.Max(
                compactGapColumns,
                (int)Math.Ceiling(
                    76.0 *
                    _columns /
                    Math.Max(
                        1.0,
                        bounds.Width)));
        var panelGapRows =
            Math.Max(
                compactGapRows,
                (int)Math.Ceiling(
                    40.0 *
                    _rows /
                    Math.Max(
                        1.0,
                        bounds.Height)));

        var panelRelation =
            existing.IsPanel ||
            candidateIsPanel;
        var horizontallyRelated =
            rowAlignment >=
                (panelRelation
                    ? 0.45
                    : 0.70) &&
            columnGap <=
                (panelRelation
                    ? panelGapColumns
                    : compactGapColumns);
        var verticallyRelated =
            columnAlignment >=
                (panelRelation
                    ? 0.62
                    : 0.78) &&
            rowGap <=
                (panelRelation
                    ? panelGapRows
                    : compactGapRows);

        if (!horizontallyRelated &&
            !verticallyRelated)
        {
            return false;
        }

        var existingArea =
            Math.Max(
                1,
                existing.Area);
        var candidateArea =
            Math.Max(
                1,
                candidate.Area);
        var intersection =
            IntersectionArea(
                existing.MinimumRow,
                existing.MaximumRow,
                existing.MinimumColumn,
                existing.MaximumColumn,
                candidate.MinimumRow,
                candidate.MaximumRow,
                candidate.MinimumColumn,
                candidate.MaximumColumn);
        var occupiedArea =
            existingArea +
            candidateArea -
            intersection;
        var unionArea =
            RectangleArea(
                minimumRow,
                maximumRow,
                minimumColumn,
                maximumColumn);
        var inflationLimit =
            panelRelation
                ? 2.35
                : 1.50;

        if (unionArea >
            Math.Max(
                1,
                occupiedArea) *
            inflationLimit)
        {
            return false;
        }

        if (!CanUseInteractionBounds(
                panelRelation,
                minimumRow,
                maximumRow,
                minimumColumn,
                maximumColumn,
                Math.Max(
                    existingArea,
                    candidateArea)))
        {
            return false;
        }

        score =
            rowAlignment *
                300.0 +
            columnAlignment *
                300.0 -
            rowGap *
                20.0 -
            columnGap *
                20.0 -
            unionArea /
                (double)Math.Max(
                    1,
                    occupiedArea) *
                50.0;

        return true;
    }

    private DetectedRegion CreateImmediateInteractionBounds(
        DetectedRegion detected,
        bool isPanel)
    {
        var bounds =
            _screen.Bounds;
        var paddingPixels =
            isPanel
                ? 14.0
                : 18.0;
        var paddingColumns =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    paddingPixels *
                    _columns /
                    Math.Max(
                        1.0,
                        bounds.Width)));
        var paddingRows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    paddingPixels *
                    _rows /
                    Math.Max(
                        1.0,
                        bounds.Height)));
        var minimumColumns =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    88.0 *
                    _columns /
                    Math.Max(
                        1.0,
                        bounds.Width)));
        var minimumRows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    68.0 *
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

        // A low-contrast hover animation may start on only one edge of an
        // icon or close button. For compact controls, include a cursor-centred
        // guarantee immediately; the user never sees a half control while the
        // temporal completion continues in the background.
        if (!isPanel &&
            TryGetCursorCell(
                out var cursorRow,
                out var cursorColumn))
        {
            var halfRows =
                Math.Max(
                    1,
                    minimumRows /
                    2);
            var halfColumns =
                Math.Max(
                    1,
                    minimumColumns /
                    2);

            minimumRow =
                Math.Min(
                    minimumRow,
                    Math.Max(
                        0,
                        cursorRow -
                        halfRows));
            maximumRow =
                Math.Max(
                    maximumRow,
                    Math.Min(
                        _rows - 1,
                        cursorRow +
                        halfRows));
            minimumColumn =
                Math.Min(
                    minimumColumn,
                    Math.Max(
                        0,
                        cursorColumn -
                        halfColumns));
            maximumColumn =
                Math.Max(
                    maximumColumn,
                    Math.Min(
                        _columns - 1,
                        cursorColumn +
                        halfColumns));
        }

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

        if (currentSize >=
            targetSize)
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

        if (currentSize >=
            targetSize)
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
        else if (maximum ==
                 limit - 1)
        {
            minimum =
                Math.Max(
                    0,
                    limit -
                    targetSize);
        }
    }

    private void EnsureInteractionReferenceSnapshot(
        long now,
        long completionTicks)
    {
        if (_interactionReferenceFrame is null ||
            _interactionReferenceFrame.Length !=
                _previousFrame.Length)
        {
            _interactionReferenceFrame =
                new byte[
                    _previousFrame.Length];
            _interactionReferenceUntilTicks = 0;
        }

        if (now <=
            _interactionReferenceUntilTicks)
        {
            return;
        }

        Buffer.BlockCopy(
            _previousFrame,
            0,
            _interactionReferenceFrame,
            0,
            _previousFrame.Length);
        _interactionReferenceUntilTicks =
            now +
            completionTicks;
    }

    private bool CompleteInteractionReveals(
        byte[] current,
        long now)
    {
        if (_interactionReferenceFrame is null ||
            now >
                _interactionReferenceUntilTicks)
        {
            return false;
        }

        var changed = false;
        var completed = 0;

        foreach (var reveal in
                 _interactionRevealRegions
                     .Where(
                         region =>
                             now <=
                             region.CompletionUntilTicks)
                     .OrderByDescending(
                         region =>
                             region.LastActivityTicks))
        {
            if (completed >= 2)
            {
                break;
            }

            completed++;

            if (TryCompleteInteractionReveal(
                    reveal,
                    current,
                    _interactionReferenceFrame))
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
        byte[] current,
        byte[] reference)
    {
        EnsureInteractionBuffers();

        var bounds =
            _screen.Bounds;
        var expansionPixels =
            reveal.IsPanel
                ? 72.0
                : 40.0;
        var expansionColumns =
            Math.Max(
                2,
                (int)Math.Ceiling(
                    expansionPixels *
                    _columns /
                    Math.Max(
                        1.0,
                        bounds.Width)));
        var expansionRows =
            Math.Max(
                2,
                (int)Math.Ceiling(
                    expansionPixels *
                    _rows /
                    Math.Max(
                        1.0,
                        bounds.Height)));
        var searchMinimumRow =
            Math.Max(
                0,
                reveal.MinimumRow -
                expansionRows);
        var searchMaximumRow =
            Math.Min(
                _rows - 1,
                reveal.MaximumRow +
                expansionRows);
        var searchMinimumColumn =
            Math.Max(
                0,
                reveal.MinimumColumn -
                expansionColumns);
        var searchMaximumColumn =
            Math.Min(
                _columns - 1,
                reveal.MaximumColumn +
                expansionColumns);

        for (var row =
                 searchMinimumRow;
             row <=
                 searchMaximumRow;
             row++)
        {
            for (var column =
                     searchMinimumColumn;
                 column <=
                     searchMaximumColumn;
                 column++)
            {
                var index =
                    row *
                    _columns +
                    column;
                _interactionVisited![index] =
                    false;
                _interactionWeakMotion![index] =
                    CellChangedAgainstReference(
                        current,
                        reference,
                        row,
                        column,
                        _settings
                            .InteractionAssistWeakPixelThreshold);
            }
        }

        var head = 0;
        var tail = 0;
        const int seedMargin = 1;

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

                _interactionVisited[index] =
                    true;
                _interactionQueue![tail++] =
                    index;
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

        while (head <
               tail)
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

                    _interactionVisited[
                        neighbourIndex] =
                        true;
                    _interactionQueue![
                        tail++] =
                        neighbourIndex;
                }
            }
        }

        if (visitedCells == 0)
        {
            return false;
        }

        if (minimumRow ==
                reveal.MinimumRow &&
            maximumRow ==
                reveal.MaximumRow &&
            minimumColumn ==
                reveal.MinimumColumn &&
            maximumColumn ==
                reveal.MaximumColumn)
        {
            return false;
        }

        if (!CanUseInteractionBounds(
                reveal.IsPanel,
                minimumRow,
                maximumRow,
                minimumColumn,
                maximumColumn,
                reveal.Area))
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
             sampleY <
                _samplesPerCell;
             sampleY++)
        {
            var sourceRow =
                sampleTop +
                sampleY;
            var rowOffset =
                sourceRow *
                _sampleStride;

            for (var sampleX = 0;
                 sampleX <
                    _samplesPerCell;
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
        bool isPanel,
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn,
        int previousArea)
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
        var areaCells =
            RectangleArea(
                minimumRow,
                maximumRow,
                minimumColumn,
                maximumColumn);
        var growthLimit =
            isPanel
                ? 2.35
                : 1.95;

        if (areaCells >
            Math.Max(
                1,
                previousArea) *
            growthLimit)
        {
            return false;
        }

        if (isPanel)
        {
            return widthPixels <= 1_300.0 &&
                   heightPixels <= 1_050.0 &&
                   areaPixels <=
                       Math.Max(
                           520_000.0,
                           screenArea *
                           0.07);
        }

        return widthPixels <= 380.0 &&
               heightPixels <= 280.0 &&
               areaPixels <= 82_000.0;
    }

    private bool UpdatePointerControlReveal(
        long now)
    {
        if (!_enabled ||
            !_settings.InteractionAssistEnabled ||
            !TryGetPointerControlBounds(
                out var minimumRow,
                out var maximumRow,
                out var minimumColumn,
                out var maximumColumn,
                out var shellInteraction))
        {
            return false;
        }

        var foregroundIntroductionRemoved =
            shellInteraction &&
            _trackedRegions.RemoveAll(
                region =>
                    region.IsForegroundIntroduction) > 0;

        if (_pointerControlReveal is null)
        {
            _pointerControlReveal =
                new InteractionRevealRegion
                {
                    MinimumRow =
                        minimumRow,
                    MaximumRow =
                        maximumRow,
                    MinimumColumn =
                        minimumColumn,
                    MaximumColumn =
                        maximumColumn,
                    CreatedTicks = now,
                    LastActivityTicks = now,
                    CompletionUntilTicks = 0,
                    DimStep = 0,
                    IsPanel = false
                };
            return true;
        }

        var changed =
            foregroundIntroductionRemoved ||
            _pointerControlReveal.MinimumRow !=
                minimumRow ||
            _pointerControlReveal.MaximumRow !=
                maximumRow ||
            _pointerControlReveal.MinimumColumn !=
                minimumColumn ||
            _pointerControlReveal.MaximumColumn !=
                maximumColumn ||
            _pointerControlReveal.DimStep != 0;

        _pointerControlReveal.MinimumRow =
            minimumRow;
        _pointerControlReveal.MaximumRow =
            maximumRow;
        _pointerControlReveal.MinimumColumn =
            minimumColumn;
        _pointerControlReveal.MaximumColumn =
            maximumColumn;
        _pointerControlReveal.LastActivityTicks =
            now;
        _pointerControlReveal.DimStep = 0;
        return changed;
    }

    private bool TryGetPointerControlBounds(
        out int minimumRow,
        out int maximumRow,
        out int minimumColumn,
        out int maximumColumn,
        out bool shellInteraction)
    {
        minimumRow = 0;
        maximumRow = 0;
        minimumColumn = 0;
        maximumColumn = 0;
        shellInteraction = false;

        if (!NativeMethods.GetCursorPos(
                out var cursor))
        {
            return false;
        }

        var screenBounds =
            _screen.Bounds;

        if (!screenBounds.Contains(
                cursor.X,
                cursor.Y))
        {
            return false;
        }

        var workingArea =
            _screen.WorkingArea;
        var shellArea =
            !workingArea.Contains(
                cursor.X,
                cursor.Y) ||
            IsShellInteractionWindowAtPoint(
                cursor);
        shellInteraction =
            shellArea;
        var edgeActivation =
            cursor.X -
                screenBounds.Left <= 6 ||
            screenBounds.Right -
                cursor.X <= 7 ||
            cursor.Y -
                screenBounds.Top <= 6 ||
            screenBounds.Bottom -
                cursor.Y <= 7;
        var titleBar =
            IsPointerInForegroundTitleBar(
                cursor);

        if (!shellArea &&
            !edgeActivation &&
            !titleBar)
        {
            return false;
        }

        var halfWidthPixels =
            shellArea
                ? 52.0
                : 50.0;
        var halfHeightPixels =
            shellArea
                ? 44.0
                : 42.0;
        var halfColumns =
            Math.Max(
                2,
                (int)Math.Ceiling(
                    halfWidthPixels *
                    _columns /
                    Math.Max(
                        1.0,
                        screenBounds.Width)));
        var halfRows =
            Math.Max(
                2,
                (int)Math.Ceiling(
                    halfHeightPixels *
                    _rows /
                    Math.Max(
                        1.0,
                        screenBounds.Height)));
        var cursorColumn =
            Math.Clamp(
                (cursor.X -
                 screenBounds.Left) *
                _columns /
                Math.Max(
                    1,
                    screenBounds.Width),
                0,
                _columns - 1);
        var cursorRow =
            Math.Clamp(
                (cursor.Y -
                 screenBounds.Top) *
                _rows /
                Math.Max(
                    1,
                    screenBounds.Height),
                0,
                _rows - 1);

        minimumRow =
            Math.Max(
                0,
                cursorRow -
                halfRows);
        maximumRow =
            Math.Min(
                _rows - 1,
                cursorRow +
                halfRows);
        minimumColumn =
            Math.Max(
                0,
                cursorColumn -
                halfColumns);
        maximumColumn =
            Math.Min(
                _columns - 1,
                cursorColumn +
                halfColumns);

        ExpandToMinimumSize(
            ref minimumRow,
            ref maximumRow,
            halfRows *
                2 +
                1,
            _rows);
        ExpandToMinimumSize(
            ref minimumColumn,
            ref maximumColumn,
            halfColumns *
                2 +
                1,
            _columns);

        return true;
    }

    private bool IsPointerInForegroundTitleBar(
        NativeMethods.Point cursor)
    {
        var foregroundWindow =
            GetForegroundWindow();

        if (foregroundWindow ==
                IntPtr.Zero ||
            !GetWindowRect(
                foregroundWindow,
                out var rectangle))
        {
            return false;
        }

        if (cursor.X <
                rectangle.Left ||
            cursor.X >=
                rectangle.Right ||
            cursor.Y <
                rectangle.Top ||
            cursor.Y >=
                rectangle.Bottom)
        {
            return false;
        }

        const int titleHeight = 72;

        return cursor.Y -
               rectangle.Top <=
               titleHeight;
    }

    private static bool IsShellInteractionWindowAtPoint(
        NativeMethods.Point cursor)
    {
        var window =
            WindowFromPoint(
                cursor);

        if (window ==
            IntPtr.Zero)
        {
            return false;
        }

        var root =
            GetAncestor(
                window,
                GaRoot);

        if (root !=
            IntPtr.Zero)
        {
            window = root;
        }

        var className =
            new StringBuilder(
                128);

        if (GetClassName(
                window,
                className,
                className.Capacity) <= 0)
        {
            return false;
        }

        var value =
            className.ToString();

        return string.Equals(
                   value,
                   "Shell_TrayWnd",
                   StringComparison.Ordinal) ||
               string.Equals(
                   value,
                   "Shell_SecondaryTrayWnd",
                   StringComparison.Ordinal) ||
               value.Contains(
                   "TaskList",
                   StringComparison.OrdinalIgnoreCase) ||
               value.Contains(
                   "Taskbar",
                   StringComparison.OrdinalIgnoreCase) ||
               value.Contains(
                   "Thumbnail",
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool UpdateInteractionVisualStates(
        long now)
    {
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
                IsCursorInsideInteractionReveal(
                    reveal,
                    cursorRow,
                    cursorColumn,
                    1))
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

            if (UpdateInteractionFade(
                    reveal,
                    now,
                    holdTicks,
                    fadeTicks,
                    dimSteps,
                    out var remove))
            {
                changed = true;
            }

            if (remove)
            {
                _interactionRevealRegions.RemoveAt(
                    index);
            }
        }

        if (_pointerControlReveal is not null)
        {
            if (UpdateInteractionFade(
                    _pointerControlReveal,
                    now,
                    holdTicks,
                    fadeTicks,
                    dimSteps,
                    out var removePointer))
            {
                changed = true;
            }

            if (removePointer)
            {
                _pointerControlReveal =
                    null;
            }
        }

        return changed;
    }

    private static bool IsCursorInsideInteractionReveal(
        InteractionRevealRegion reveal,
        int row,
        int column,
        int margin)
    {
        return row >=
                   reveal.MinimumRow -
                   margin &&
               row <=
                   reveal.MaximumRow +
                   margin &&
               column >=
                   reveal.MinimumColumn -
                   margin &&
               column <=
                   reveal.MaximumColumn +
                   margin;
    }

    private static bool UpdateInteractionFade(
        InteractionRevealRegion reveal,
        long now,
        long holdTicks,
        long fadeTicks,
        int dimSteps,
        out bool remove)
    {
        remove = false;

        var elapsed =
            now -
            reveal.LastActivityTicks;

        if (elapsed <
            holdTicks)
        {
            if (reveal.DimStep == 0)
            {
                return false;
            }

            reveal.DimStep = 0;
            return true;
        }

        if (fadeTicks <= 0 ||
            elapsed >=
                holdTicks +
                fadeTicks)
        {
            remove = true;
            return true;
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

        if (targetStep ==
            reveal.DimStep)
        {
            return false;
        }

        reveal.DimStep =
            targetStep;
        return true;
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
        if (_pointerControlReveal is not null)
        {
            AppendInteractionMaskRegion(
                result,
                _pointerControlReveal,
                dimSteps,
                maximumOpacity);
        }

        foreach (var reveal in
                 _interactionRevealRegions)
        {
            AppendInteractionMaskRegion(
                result,
                reveal,
                dimSteps,
                maximumOpacity);
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

    private void AppendInteractionMaskRegion(
        List<MaskRegion> result,
        InteractionRevealRegion reveal,
        int dimSteps,
        double maximumOpacity)
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

    private void CoalesceProductionMaskRegions(
        List<MaskRegion> regions,
        double maximumOpacity)
    {
        if (regions.Count < 2)
        {
            return;
        }

        var merged = true;

        while (merged)
        {
            merged = false;

            for (var firstIndex = 0;
                 firstIndex <
                    regions.Count;
                 firstIndex++)
            {
                for (var secondIndex =
                         firstIndex + 1;
                     secondIndex <
                        regions.Count;
                     secondIndex++)
                {
                    if (!TryMergeProductionMaskRegions(
                            regions[firstIndex],
                            regions[secondIndex],
                            maximumOpacity,
                            out var combined))
                    {
                        continue;
                    }

                    regions[firstIndex] =
                        combined;
                    regions.RemoveAt(
                        secondIndex);
                    merged = true;
                    break;
                }

                if (merged)
                {
                    break;
                }
            }
        }
    }

    private bool TryMergeProductionMaskRegions(
        MaskRegion first,
        MaskRegion second,
        double maximumOpacity,
        out MaskRegion combined)
    {
        combined = first;

        if (first.Opacity >=
                maximumOpacity -
                0.0001 ||
            second.Opacity >=
                maximumOpacity -
                0.0001)
        {
            return false;
        }

        var opacityDifference =
            Math.Abs(
                first.Opacity -
                second.Opacity);
        var opacityTolerance =
            maximumOpacity /
            Math.Max(
                2,
                _settings.MotionZoneDimSteps) +
            0.0001;

        if (opacityDifference >
            opacityTolerance)
        {
            return false;
        }

        var firstBounds =
            first.NormalizedBounds;
        var secondBounds =
            second.NormalizedBounds;

        if (opacityDifference <= 0.0001 &&
            firstBounds.Contains(
                secondBounds))
        {
            combined = first;
            return true;
        }

        if (opacityDifference <= 0.0001 &&
            secondBounds.Contains(
                firstBounds))
        {
            combined = second;
            return true;
        }

        var screenBounds =
            _screen.Bounds;
        var firstLeft =
            firstBounds.Left *
            screenBounds.Width;
        var firstTop =
            firstBounds.Top *
            screenBounds.Height;
        var firstRight =
            firstBounds.Right *
            screenBounds.Width;
        var firstBottom =
            firstBounds.Bottom *
            screenBounds.Height;
        var secondLeft =
            secondBounds.Left *
            screenBounds.Width;
        var secondTop =
            secondBounds.Top *
            screenBounds.Height;
        var secondRight =
            secondBounds.Right *
            screenBounds.Width;
        var secondBottom =
            secondBounds.Bottom *
            screenBounds.Height;
        var firstWidth =
            Math.Max(
                1.0,
                firstRight -
                firstLeft);
        var firstHeight =
            Math.Max(
                1.0,
                firstBottom -
                firstTop);
        var secondWidth =
            Math.Max(
                1.0,
                secondRight -
                secondLeft);
        var secondHeight =
            Math.Max(
                1.0,
                secondBottom -
                secondTop);
        var horizontalOverlap =
            Math.Max(
                0.0,
                Math.Min(
                    firstRight,
                    secondRight) -
                Math.Max(
                    firstLeft,
                    secondLeft));
        var verticalOverlap =
            Math.Max(
                0.0,
                Math.Min(
                    firstBottom,
                    secondBottom) -
                Math.Max(
                    firstTop,
                    secondTop));
        var horizontalGap =
            Math.Max(
                0.0,
                Math.Max(
                    firstLeft,
                    secondLeft) -
                Math.Min(
                    firstRight,
                    secondRight));
        var verticalGap =
            Math.Max(
                0.0,
                Math.Max(
                    firstTop,
                    secondTop) -
                Math.Min(
                    firstBottom,
                    secondBottom));
        var verticalAlignment =
            verticalOverlap /
            Math.Max(
                1.0,
                Math.Min(
                    firstHeight,
                    secondHeight));
        var horizontalAlignment =
            horizontalOverlap /
            Math.Max(
                1.0,
                Math.Min(
                    firstWidth,
                    secondWidth));
        var tallRelation =
            Math.Max(
                firstHeight,
                secondHeight) >=
            120.0;
        var wideRelation =
            Math.Max(
                firstWidth,
                secondWidth) >=
            160.0;
        var allowedHorizontalGap =
            tallRelation
                ? 72.0
                : 14.0;
        var allowedVerticalGap =
            wideRelation
                ? 36.0
                : 12.0;
        var firstArea =
            firstWidth *
            firstHeight;
        var secondArea =
            secondWidth *
            secondHeight;
        var screenArea =
            Math.Max(
                1.0,
                (double)screenBounds.Width *
                screenBounds.Height);

        // A large introduction/window region may overlap other holes, but it
        // must never bridge a dark gap to them and become visually dominant.
        if (horizontalOverlap <= 0.0 &&
            verticalOverlap <= 0.0 &&
            (firstArea >
                 screenArea *
                 0.05 ||
             secondArea >
                 screenArea *
                 0.05))
        {
            return false;
        }

        var horizontallyRelated =
            verticalAlignment >=
                (tallRelation
                    ? 0.50
                    : 0.72) &&
            horizontalGap <=
                allowedHorizontalGap;
        var verticallyRelated =
            horizontalAlignment >=
                (wideRelation
                    ? 0.68
                    : 0.80) &&
            verticalGap <=
                allowedVerticalGap;

        if (!horizontallyRelated &&
            !verticallyRelated)
        {
            return false;
        }

        var unionLeft =
            Math.Min(
                firstLeft,
                secondLeft);
        var unionTop =
            Math.Min(
                firstTop,
                secondTop);
        var unionRight =
            Math.Max(
                firstRight,
                secondRight);
        var unionBottom =
            Math.Max(
                firstBottom,
                secondBottom);
        var unionWidth =
            unionRight -
            unionLeft;
        var unionHeight =
            unionBottom -
            unionTop;
        var unionArea =
            unionWidth *
            unionHeight;
        var intersectionArea =
            horizontalOverlap *
            verticalOverlap;
        var occupiedArea =
            firstWidth *
                firstHeight +
            secondWidth *
                secondHeight -
            intersectionArea;
        var inflationLimit =
            tallRelation ||
            wideRelation
                ? 2.35
                : 1.45;
        if (unionArea >
                Math.Max(
                    1.0,
                    occupiedArea) *
                inflationLimit ||
            unionWidth >
                1_300.0 ||
            unionHeight >
                1_200.0 ||
            unionArea >
                screenArea *
                0.08)
        {
            return false;
        }

        combined =
            new MaskRegion(
                new Rect(
                    unionLeft /
                    Math.Max(
                        1.0,
                        screenBounds.Width),
                    unionTop /
                    Math.Max(
                        1.0,
                        screenBounds.Height),
                    unionWidth /
                    Math.Max(
                        1.0,
                        screenBounds.Width),
                    unionHeight /
                    Math.Max(
                        1.0,
                        screenBounds.Height)),
                Math.Min(
                    first.Opacity,
                    second.Opacity));
        return true;
    }

    private bool IsPointInsideSupplementalReveal(
        int row,
        int column)
    {
        if (_pointerControlReveal is not null &&
            _pointerControlReveal.DimStep == 0 &&
            IsCursorInsideInteractionReveal(
                _pointerControlReveal,
                row,
                column,
                0))
        {
            return true;
        }

        foreach (var reveal in
                 _interactionRevealRegions)
        {
            if (reveal.DimStep == 0 &&
                IsCursorInsideInteractionReveal(
                    reveal,
                    row,
                    column,
                    0))
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