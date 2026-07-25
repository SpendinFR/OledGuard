using System.Windows;

namespace OledGuard;

internal sealed partial class MonitorSession
{
    private const int InteractionWeakPixelThreshold = 6;
    private const int InteractionCompletionMilliseconds = 60;
    private const int InteractionHoldMilliseconds = 3_000;
    private const int InteractionFadeMilliseconds = 300;

    private readonly record struct InteractionBounds(
        int MinimumRow,
        int MaximumRow,
        int MinimumColumn,
        int MaximumColumn)
    {
        public int Width => MaximumColumn - MinimumColumn + 1;
        public int Height => MaximumRow - MinimumRow + 1;
    }

    private sealed class InteractionRevealState
    {
        public InteractionBounds Bounds;
        public InteractionBounds InitialBounds;
        public InteractionBounds StrongSeedBounds;
        public bool HasStrongSeed;
        public bool IsPanel;
        public bool PointerControlled;
        public bool CompletionArmed;
        public long LastActivityTicks;
        public long LastCursorInsideTicks;
        public long CompletionUntilTicks;
        public double Opacity;
    }

    private InteractionRevealState? _interactionReveal;
    private byte[]? _interactionReferenceFrame;
    private bool[]? _interactionWeakMotion;
    private bool[]? _interactionVisited;
    private int[]? _interactionQueue;

    private void ResetInteractionCompletion() =>
        _interactionReveal = null;

    private bool UpdateInteractionCompletion(byte[] current, long now)
    {
        var changed = false;
        var hasPointerEnvelope = TryGetPriorityPointerEnvelope(out var pointerEnvelope);
        var hasStrongSeed = TrySelectStrongInteractionSeed(out var strongSeed, out var isPanel);

        if (hasStrongSeed || hasPointerEnvelope)
        {
            var strongBounds = hasStrongSeed
                ? new InteractionBounds(
                    strongSeed.MinimumRow,
                    strongSeed.MaximumRow,
                    strongSeed.MinimumColumn,
                    strongSeed.MaximumColumn)
                : pointerEnvelope;
            var candidate = hasStrongSeed
                ? CreateImmediateInteractionBounds(strongSeed, isPanel)
                : pointerEnvelope;

            if (hasPointerEnvelope && hasStrongSeed)
            {
                candidate = UnionInteractionBounds(candidate, pointerEnvelope);
            }

            changed |= ApplyInteractionCandidate(
                candidate,
                hasStrongSeed,
                strongBounds,
                isPanel,
                hasPointerEnvelope,
                now);

            if (hasStrongSeed)
            {
                changed |= ArmInteractionCompletionWindow(now);
            }
        }

        if (_interactionReveal is not null &&
            _interactionReveal.HasStrongSeed &&
            now <= _interactionReveal.CompletionUntilTicks)
        {
            changed |= CompleteConnectedWeakMotion(current, now);
        }

        return changed;
    }

    private bool TrySelectStrongInteractionSeed(out DetectedRegion selected, out bool isPanel)
    {
        selected = default;
        isPanel = false;

        var screen = _screen.Bounds;
        var hasCursor =
            NativeMethods.GetCursorPos(out var cursor) &&
            screen.Contains(cursor.X, cursor.Y);
        var cursorColumn = hasCursor
            ? (cursor.X - screen.Left) * _columns /
                (double)Math.Max(1, screen.Width)
            : 0.0;
        var cursorRow = hasCursor
            ? (cursor.Y - screen.Top) * _rows /
                (double)Math.Max(1, screen.Height)
            : 0.0;
        var found = false;
        var bestScore = double.PositiveInfinity;

        foreach (var detected in _detectedRegions)
        {
            var widthPixels = detected.Width * screen.Width /
                (double)Math.Max(1, _columns);
            var heightPixels = detected.Height * screen.Height /
                (double)Math.Max(1, _rows);
            var areaPixels = widthPixels * heightPixels;
            var minimumDimension = Math.Min(widthPixels, heightPixels);
            var maximumDimension = Math.Max(widthPixels, heightPixels);
            var compact = areaPixels <= 42_000.0 &&
                maximumDimension <= 340.0 && minimumDimension <= 200.0;
            var boundedPanel = areaPixels <= 260_000.0 &&
                widthPixels <= 720.0 && heightPixels <= 960.0 &&
                minimumDimension <= 420.0;

            var nearCursor =
                hasCursor &&
                IsCursorNearDetectedRegion(detected, 36.0);
            var keyboardPanel =
                boundedPanel &&
                !compact &&
                areaPixels >= 5_000.0 &&
                detected.MotionCells >= 6;

            if ((compact && !nearCursor) ||
                (!compact && !keyboardPanel))
            {
                continue;
            }

            var centerColumn = (detected.MinimumColumn + detected.MaximumColumn) / 2.0;
            var centerRow = (detected.MinimumRow + detected.MaximumRow) / 2.0;
            var distance = nearCursor
                ? Math.Abs(centerColumn - cursorColumn) +
                    Math.Abs(centerRow - cursorRow)
                : 2_000.0;
            var score = distance * 1000.0 + areaPixels;

            if (score >= bestScore)
            {
                continue;
            }

            selected = detected;
            isPanel = boundedPanel && !compact;
            bestScore = score;
            found = true;
        }

        return found;
    }

    private InteractionBounds CreateImmediateInteractionBounds(
        DetectedRegion detected,
        bool isPanel)
    {
        var detectedBounds = new InteractionBounds(
            detected.MinimumRow,
            detected.MaximumRow,
            detected.MinimumColumn,
            detected.MaximumColumn);

        if (isPanel || !NativeMethods.GetCursorPos(out var cursor))
        {
            return detectedBounds;
        }

        var screen = _screen.Bounds;
        var controlSize = Math.Clamp(58.0 * screen.Height / 1_080.0, 50.0, 88.0);
        var halfColumns = Math.Max(2, (int)Math.Ceiling(
            controlSize * _columns / Math.Max(1.0, screen.Width) / 2.0));
        var halfRows = Math.Max(2, (int)Math.Ceiling(
            controlSize * _rows / Math.Max(1.0, screen.Height) / 2.0));
        var cursorColumn = Math.Clamp(
            (cursor.X - screen.Left) * _columns / Math.Max(1, screen.Width),
            0,
            _columns - 1);
        var cursorRow = Math.Clamp(
            (cursor.Y - screen.Top) * _rows / Math.Max(1, screen.Height),
            0,
            _rows - 1);
        var cursorEnvelope = ClampInteractionBounds(new InteractionBounds(
            cursorRow - halfRows,
            cursorRow + halfRows,
            cursorColumn - halfColumns,
            cursorColumn + halfColumns));

        return UnionInteractionBounds(detectedBounds, cursorEnvelope);
    }

    private bool TryGetPriorityPointerEnvelope(out InteractionBounds envelope)
    {
        envelope = default;
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        var screen = _screen.Bounds;
        var workingArea = _screen.WorkingArea;
        if (!screen.Contains(cursor.X, cursor.Y))
        {
            return false;
        }

        var overTaskbar = !workingArea.Contains(cursor.X, cursor.Y);
        var overCaption = false;

        if (!overTaskbar &&
            _lastForegroundWindow != IntPtr.Zero &&
            GetWindowRect(_lastForegroundWindow, out var nativeRectangle))
        {
            var foreground = System.Drawing.Rectangle.Intersect(
                workingArea,
                System.Drawing.Rectangle.FromLTRB(
                    nativeRectangle.Left,
                    nativeRectangle.Top,
                    nativeRectangle.Right,
                    nativeRectangle.Bottom));
            var captionHeight = Math.Clamp(
                (int)Math.Round(56.0 * screen.Height / 1_080.0),
                44,
                86);
            overCaption = foreground.Width > 0 && foreground.Height > 0 &&
                cursor.X >= foreground.Left && cursor.X < foreground.Right &&
                cursor.Y >= foreground.Top &&
                cursor.Y < Math.Min(foreground.Bottom, foreground.Top + captionHeight);
        }

        if (!overTaskbar && !overCaption)
        {
            return false;
        }

        var appBarThickness = Math.Max(
            screen.Width - workingArea.Width,
            screen.Height - workingArea.Height);
        var width = Math.Clamp(
            Math.Max(62.0 * screen.Height / 1_080.0, appBarThickness * 1.10),
            54.0,
            96.0);
        var height = Math.Clamp(
            Math.Max(58.0 * screen.Height / 1_080.0, appBarThickness * 0.95),
            50.0,
            90.0);
        var pixelBounds = System.Drawing.Rectangle.FromLTRB(
            (int)Math.Floor(cursor.X - width / 2.0),
            (int)Math.Floor(cursor.Y - height / 2.0),
            (int)Math.Ceiling(cursor.X + width / 2.0),
            (int)Math.Ceiling(cursor.Y + height / 2.0));

        envelope = PixelRectangleToInteractionBounds(pixelBounds);
        return true;
    }

    private bool ApplyInteractionCandidate(
        InteractionBounds candidate,
        bool hasStrongSeed,
        InteractionBounds strongSeed,
        bool isPanel,
        bool pointerControlled,
        long now)
    {
        candidate = ClampInteractionBounds(candidate);

        if (_interactionReveal is null)
        {
            _interactionReveal = CreateInteractionState(
                candidate,
                hasStrongSeed,
                strongSeed,
                isPanel,
                pointerControlled,
                now);
            return true;
        }

        var state = _interactionReveal;
        if (pointerControlled)
        {
            var changed = !state.Bounds.Equals(candidate) ||
                state.Opacity > 0.0001 || !state.PointerControlled;
            state.Bounds = candidate;
            state.InitialBounds = candidate;
            state.PointerControlled = true;
            state.LastActivityTicks = now;
            state.LastCursorInsideTicks = now;
            state.Opacity = 0.0;

            if (hasStrongSeed)
            {
                state.StrongSeedBounds = strongSeed;
                state.HasStrongSeed = true;
                state.IsPanel = isPanel;
            }

            return changed;
        }

        var nearby = RectanglesNear(
            state.Bounds.MinimumRow,
            state.Bounds.MaximumRow,
            state.Bounds.MinimumColumn,
            state.Bounds.MaximumColumn,
            candidate.MinimumRow,
            candidate.MaximumRow,
            candidate.MinimumColumn,
            candidate.MaximumColumn,
            2);

        if (!nearby)
        {
            _interactionReveal = CreateInteractionState(
                candidate,
                hasStrongSeed,
                strongSeed,
                isPanel,
                false,
                now);
            return true;
        }

        var merged = UnionInteractionBounds(state.Bounds, candidate);
        if (!FitsInteractionCaps(merged, state.InitialBounds, state.IsPanel || isPanel))
        {
            merged = state.Bounds;
        }

        var changedBounds = !state.Bounds.Equals(merged);
        var changedOpacity = state.Opacity > 0.0001;
        state.Bounds = merged;
        state.LastActivityTicks = now;
        state.Opacity = 0.0;
        state.PointerControlled = false;
        state.IsPanel |= isPanel;

        if (hasStrongSeed)
        {
            state.StrongSeedBounds = strongSeed;
            state.HasStrongSeed = true;
        }

        return changedBounds || changedOpacity;
    }

    private static InteractionRevealState CreateInteractionState(
        InteractionBounds bounds,
        bool hasStrongSeed,
        InteractionBounds strongSeed,
        bool isPanel,
        bool pointerControlled,
        long now)
    {
        return new InteractionRevealState
        {
            Bounds = bounds,
            InitialBounds = bounds,
            StrongSeedBounds = strongSeed,
            HasStrongSeed = hasStrongSeed,
            IsPanel = isPanel,
            PointerControlled = pointerControlled,
            CompletionArmed = false,
            LastActivityTicks = now,
            LastCursorInsideTicks = now,
            CompletionUntilTicks = 0,
            Opacity = 0.0
        };
    }

    private bool ArmInteractionCompletionWindow(long now)
    {
        var state = _interactionReveal;
        if (state is null || !state.HasStrongSeed || state.CompletionArmed)
        {
            return false;
        }

        if (_interactionReferenceFrame is null ||
            _interactionReferenceFrame.Length != _previousFrame.Length)
        {
            _interactionReferenceFrame = new byte[_previousFrame.Length];
        }

        Buffer.BlockCopy(
            _previousFrame,
            0,
            _interactionReferenceFrame,
            0,
            _previousFrame.Length);
        state.CompletionArmed = true;
        state.CompletionUntilTicks = now +
            ToStopwatchTicks(InteractionCompletionMilliseconds);
        state.LastActivityTicks = now;
        state.Opacity = 0.0;
        return true;
    }

    private bool CompleteConnectedWeakMotion(byte[] current, long now)
    {
        var state = _interactionReveal;
        var reference = _interactionReferenceFrame;
        if (state is null || reference is null)
        {
            return false;
        }

        EnsureInteractionBuffers();
        var screen = _screen.Bounds;
        var growthColumns = Math.Max(2, (int)Math.Ceiling(
            30.0 * _columns / Math.Max(1.0, screen.Width)));
        var growthRows = Math.Max(2, (int)Math.Ceiling(
            30.0 * _rows / Math.Max(1.0, screen.Height)));
        var roi = ClampInteractionBounds(new InteractionBounds(
            state.Bounds.MinimumRow - growthRows,
            state.Bounds.MaximumRow + growthRows,
            state.Bounds.MinimumColumn - growthColumns,
            state.Bounds.MaximumColumn + growthColumns));

        for (var row = roi.MinimumRow; row <= roi.MaximumRow; row++)
        {
            for (var column = roi.MinimumColumn; column <= roi.MaximumColumn; column++)
            {
                var index = row * _columns + column;
                _interactionWeakMotion![index] =
                    IsWeakMotionCell(current, reference, row, column);
                _interactionVisited![index] = false;
            }
        }

        var seed = ClampInteractionBounds(new InteractionBounds(
            state.StrongSeedBounds.MinimumRow - 1,
            state.StrongSeedBounds.MaximumRow + 1,
            state.StrongSeedBounds.MinimumColumn - 1,
            state.StrongSeedBounds.MaximumColumn + 1));
        var head = 0;
        var tail = 0;

        for (var row = Math.Max(roi.MinimumRow, seed.MinimumRow);
             row <= Math.Min(roi.MaximumRow, seed.MaximumRow);
             row++)
        {
            for (var column = Math.Max(roi.MinimumColumn, seed.MinimumColumn);
                 column <= Math.Min(roi.MaximumColumn, seed.MaximumColumn);
                 column++)
            {
                var index = row * _columns + column;
                if (!_interactionWeakMotion![index] || _interactionVisited![index])
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

        var minimumRow = int.MaxValue;
        var maximumRow = int.MinValue;
        var minimumColumn = int.MaxValue;
        var maximumColumn = int.MinValue;

        while (head < tail)
        {
            var index = _interactionQueue![head++];
            var row = index / _columns;
            var column = index % _columns;
            minimumRow = Math.Min(minimumRow, row);
            maximumRow = Math.Max(maximumRow, row);
            minimumColumn = Math.Min(minimumColumn, column);
            maximumColumn = Math.Max(maximumColumn, column);

            for (var offsetY = -1; offsetY <= 1; offsetY++)
            {
                var neighbourRow = row + offsetY;
                if (neighbourRow < roi.MinimumRow || neighbourRow > roi.MaximumRow)
                {
                    continue;
                }

                for (var offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    var neighbourColumn = column + offsetX;
                    if (neighbourColumn < roi.MinimumColumn ||
                        neighbourColumn > roi.MaximumColumn)
                    {
                        continue;
                    }

                    var neighbour = neighbourRow * _columns + neighbourColumn;
                    if (_interactionVisited![neighbour] ||
                        !_interactionWeakMotion![neighbour])
                    {
                        continue;
                    }

                    _interactionVisited[neighbour] = true;
                    _interactionQueue![tail++] = neighbour;
                }
            }
        }

        var completed = UnionInteractionBounds(
            state.Bounds,
            new InteractionBounds(
                minimumRow,
                maximumRow,
                minimumColumn,
                maximumColumn));

        if (state.Bounds.Equals(completed) ||
            !FitsInteractionCaps(completed, state.InitialBounds, state.IsPanel))
        {
            return false;
        }

        state.Bounds = completed;
        state.LastActivityTicks = now;
        state.Opacity = 0.0;
        return true;
    }

    private bool IsWeakMotionCell(byte[] current, byte[] reference, int row, int column)
    {
        var sampleTop = row * _samplesPerCell;
        var sampleLeft = column * _samplesPerCell;
        var sampleCount = _samplesPerCell * _samplesPerCell;
        var minimumChangedSamples = Math.Max(1, (int)Math.Ceiling(
            sampleCount * _settings.MotionZoneChangedFraction));
        var changedSamples = 0;
        var maximumDifference = 0;
        var differenceTotal = 0;

        for (var sampleY = 0; sampleY < _samplesPerCell; sampleY++)
        {
            var rowOffset = (sampleTop + sampleY) * _sampleStride;
            for (var sampleX = 0; sampleX < _samplesPerCell; sampleX++)
            {
                var offset = rowOffset + (sampleLeft + sampleX) * 4;
                var difference = Math.Max(
                    Math.Abs(current[offset] - reference[offset]),
                    Math.Max(
                        Math.Abs(current[offset + 1] - reference[offset + 1]),
                        Math.Abs(current[offset + 2] - reference[offset + 2])));
                differenceTotal += difference;
                maximumDifference = Math.Max(maximumDifference, difference);
                if (difference >= InteractionWeakPixelThreshold)
                {
                    changedSamples++;
                }
            }
        }

        var meanDifference = differenceTotal / (double)sampleCount;
        return changedSamples >= minimumChangedSamples ||
            maximumDifference >= InteractionWeakPixelThreshold * 2 ||
            meanDifference >= InteractionWeakPixelThreshold * 0.60;
    }

    private bool UpdateInteractionVisualState(long now)
    {
        var changed = ReleaseForegroundIntroductionForTaskbar();
        var state = _interactionReveal;
        if (state is null)
        {
            return changed;
        }

        if (IsCursorInsideInteractionReveal())
        {
            state.LastCursorInsideTicks = now;
            if (state.Opacity > 0.0001)
            {
                state.Opacity = 0.0;
                changed = true;
            }
            return changed;
        }

        var activityTicks = Math.Max(state.LastActivityTicks, state.LastCursorInsideTicks);
        var holdUntil = activityTicks + ToStopwatchTicks(InteractionHoldMilliseconds);
        if (now < holdUntil)
        {
            if (state.Opacity > 0.0001)
            {
                state.Opacity = 0.0;
                changed = true;
            }
            return changed;
        }

        var fadeTicks = Math.Max(1L, ToStopwatchTicks(InteractionFadeMilliseconds));
        var fadeRatio = (now - holdUntil) / (double)fadeTicks;
        if (fadeRatio >= 1.0)
        {
            _interactionReveal = null;
            return true;
        }

        var targetOpacity = _settings.MaximumMaskOpacity *
            Math.Clamp(fadeRatio, 0.0, 1.0);
        if (Math.Abs(state.Opacity - targetOpacity) >= 0.002)
        {
            state.Opacity = targetOpacity;
            changed = true;
        }

        return changed;
    }

    private bool ReleaseForegroundIntroductionForTaskbar()
    {
        if (!NativeMethods.GetCursorPos(out var cursor) ||
            !_screen.Bounds.Contains(cursor.X, cursor.Y) ||
            _screen.WorkingArea.Contains(cursor.X, cursor.Y))
        {
            return false;
        }

        var removed = _trackedRegions.RemoveAll(
            region => region.IsForegroundIntroduction) > 0;
        if (removed)
        {
            _sceneSettleUntilTicks = 0;
        }
        return removed;
    }

    private bool IsCursorInsideInteractionReveal()
    {
        if (_interactionReveal is null || !NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        var screen = _screen.Bounds;
        if (!screen.Contains(cursor.X, cursor.Y))
        {
            return false;
        }

        var column = Math.Clamp(
            (cursor.X - screen.Left) * _columns / Math.Max(1, screen.Width),
            0,
            _columns - 1);
        var row = Math.Clamp(
            (cursor.Y - screen.Top) * _rows / Math.Max(1, screen.Height),
            0,
            _rows - 1);
        var bounds = _interactionReveal.Bounds;
        return row >= bounds.MinimumRow && row <= bounds.MaximumRow &&
            column >= bounds.MinimumColumn && column <= bounds.MaximumColumn;
    }

    private bool IsPointInsideInteractionReveal(double localX, double localY)
    {
        var state = _interactionReveal;
        if (state is null || state.Opacity > 0.0001)
        {
            return false;
        }

        var screen = _screen.Bounds;
        var column = Math.Clamp(
            (int)(localX * _columns / Math.Max(1, screen.Width)),
            0,
            _columns - 1);
        var row = Math.Clamp(
            (int)(localY * _rows / Math.Max(1, screen.Height)),
            0,
            _rows - 1);
        return row >= state.Bounds.MinimumRow && row <= state.Bounds.MaximumRow &&
            column >= state.Bounds.MinimumColumn && column <= state.Bounds.MaximumColumn;
    }

    private void AppendInteractionReveal(List<MaskRegion> result)
    {
        var state = _interactionReveal;
        if (state is null ||
            state.Opacity >= _settings.MaximumMaskOpacity - 0.0001)
        {
            return;
        }

        var bounds = state.Bounds;
        var left = bounds.MinimumColumn / (double)_columns;
        var top = bounds.MinimumRow / (double)_rows;
        var right = (bounds.MaximumColumn + 1) / (double)_columns;
        var bottom = (bounds.MaximumRow + 1) / (double)_rows;
        result.Add(new MaskRegion(
            new Rect(left, top, right - left, bottom - top),
            state.Opacity));
    }

    private void EnsureInteractionBuffers()
    {
        var length = _rawMotion.Length;
        if (_interactionWeakMotion is not null &&
            _interactionWeakMotion.Length == length)
        {
            return;
        }

        _interactionWeakMotion = new bool[length];
        _interactionVisited = new bool[length];
        _interactionQueue = new int[length];
    }

    private bool FitsInteractionCaps(
        InteractionBounds candidate,
        InteractionBounds initial,
        bool isPanel)
    {
        var screen = _screen.Bounds;
        var widthPixels = candidate.Width * screen.Width /
            (double)Math.Max(1, _columns);
        var heightPixels = candidate.Height * screen.Height /
            (double)Math.Max(1, _rows);
        var areaPixels = widthPixels * heightPixels;
        var initialArea = Math.Max(
            1.0,
            initial.Width * screen.Width / (double)Math.Max(1, _columns) *
            initial.Height * screen.Height / (double)Math.Max(1, _rows));
        var maximumWidth = isPanel ? 720.0 : 230.0;
        var maximumHeight = isPanel ? 960.0 : 190.0;
        var maximumArea = isPanel ? 280_000.0 : 44_000.0;
        var maximumGrowth = isPanel ? 1.75 : 2.40;

        return widthPixels <= maximumWidth &&
            heightPixels <= maximumHeight &&
            areaPixels <= maximumArea &&
            areaPixels <= initialArea * maximumGrowth;
    }

    private InteractionBounds PixelRectangleToInteractionBounds(
        System.Drawing.Rectangle pixelBounds)
    {
        var screen = _screen.Bounds;
        var clipped = System.Drawing.Rectangle.Intersect(screen, pixelBounds);
        var minimumColumn = Math.Clamp(
            (int)Math.Floor((clipped.Left - screen.Left) * _columns /
                (double)Math.Max(1, screen.Width)),
            0,
            _columns - 1);
        var maximumColumn = Math.Clamp(
            (int)Math.Ceiling((clipped.Right - screen.Left) * _columns /
                (double)Math.Max(1, screen.Width)) - 1,
            minimumColumn,
            _columns - 1);
        var minimumRow = Math.Clamp(
            (int)Math.Floor((clipped.Top - screen.Top) * _rows /
                (double)Math.Max(1, screen.Height)),
            0,
            _rows - 1);
        var maximumRow = Math.Clamp(
            (int)Math.Ceiling((clipped.Bottom - screen.Top) * _rows /
                (double)Math.Max(1, screen.Height)) - 1,
            minimumRow,
            _rows - 1);
        return new InteractionBounds(
            minimumRow,
            maximumRow,
            minimumColumn,
            maximumColumn);
    }

    private InteractionBounds ClampInteractionBounds(InteractionBounds bounds)
    {
        var minimumRow = Math.Clamp(bounds.MinimumRow, 0, _rows - 1);
        var maximumRow = Math.Clamp(bounds.MaximumRow, minimumRow, _rows - 1);
        var minimumColumn = Math.Clamp(bounds.MinimumColumn, 0, _columns - 1);
        var maximumColumn = Math.Clamp(bounds.MaximumColumn, minimumColumn, _columns - 1);
        return new InteractionBounds(
            minimumRow,
            maximumRow,
            minimumColumn,
            maximumColumn);
    }

    private static InteractionBounds UnionInteractionBounds(
        InteractionBounds first,
        InteractionBounds second) =>
        new(
            Math.Min(first.MinimumRow, second.MinimumRow),
            Math.Max(first.MaximumRow, second.MaximumRow),
            Math.Min(first.MinimumColumn, second.MinimumColumn),
            Math.Max(first.MaximumColumn, second.MaximumColumn));
}
