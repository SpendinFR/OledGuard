using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private readonly record struct DetectedRegion(
        int MinimumRow,
        int MaximumRow,
        int MinimumColumn,
        int MaximumColumn,
        int MotionCells)
    {
        public int Area =>
            (MaximumRow - MinimumRow + 1) *
            (MaximumColumn - MinimumColumn + 1);
    }

    private sealed class TrackedRegion
    {
        public int MinimumRow;
        public int MaximumRow;
        public int MinimumColumn;
        public int MaximumColumn;
        public long WindowStartTicks;
        public long LastMotionTicks;
        public long LastHitCaptureTicks;
        public int MotionHits;
        public bool Recurring;
    }

    private readonly FormsScreen _screen;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly ScreenSampler _sampler;

    private readonly int _columns;
    private readonly int _rows;
    private readonly int _samplesPerCell;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;

    private readonly byte[] _previousFrame;
    private readonly bool[] _rawMotion;
    private readonly bool[] _expandedMotion;
    private readonly bool[] _visited;
    private readonly int[] _queue;
    private readonly float[] _renderAlpha;
    private readonly long[] _mouseRevealUntilTicks;

    private readonly List<DetectedRegion> _detectedRegions = new();
    private readonly List<TrackedRegion> _trackedRegions = new();
    private readonly List<DetectedRegion> _visibleRectangles = new();

    private readonly object _sync = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _enabled;
    private bool _hasPrevious;
    private bool _maskDirty;
    private bool _disposed;
    private long _revealAllUntilTicks;
    private long _nextMouseExpiryTicks;
    private IntPtr _lastForegroundWindow;
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;

    private bool _hasMouseGridPosition;
    private int _lastMouseRow;
    private int _lastMouseColumn;

    public MonitorSession(
        FormsScreen screen,
        AppSettings settings)
    {
        _screen = screen;
        _settings = settings;

        var bounds = screen.Bounds;
        var requestedWidth = Math.Min(
            bounds.Width,
            settings.MotionZoneCaptureWidth);
        var requestedHeight = Math.Max(
            1,
            (int)Math.Round(
                bounds.Height *
                requestedWidth /
                (double)Math.Max(1, bounds.Width)));

        _samplesPerCell =
            settings.MotionZoneSamplesPerCell;
        _columns = Math.Max(
            1,
            (int)Math.Ceiling(
                requestedWidth /
                (double)_samplesPerCell));
        _rows = Math.Max(
            1,
            (int)Math.Ceiling(
                requestedHeight /
                (double)_samplesPerCell));

        _sampleWidth = checked(
            _columns * _samplesPerCell);
        _sampleHeight = checked(
            _rows * _samplesPerCell);
        _sampleStride = checked(
            _sampleWidth * 4);

        var cellCount = checked(
            _columns * _rows);
        var frameBytes = checked(
            _sampleStride * _sampleHeight);

        _previousFrame = new byte[frameBytes];
        _rawMotion = new bool[cellCount];
        _expandedMotion = new bool[cellCount];
        _visited = new bool[cellCount];
        _queue = new int[cellCount];
        _renderAlpha = new float[cellCount];
        _mouseRevealUntilTicks = new long[cellCount];

        _overlay = new OverlayWindow(screen);
        _sampler = new ScreenSampler(
            bounds,
            _sampleWidth,
            _sampleHeight);

        _renderTimer = new DispatcherTimer(
            DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += OnRenderTick;
    }

    public bool ExcludedFromCapture =>
        _overlay.ExcludedFromCapture;

    public void Start(bool enabled)
    {
        _overlay.EnsureVisible();
        SetEnabled(enabled);
        _captureLoop = Task.Run(CaptureLoopAsync);
        _renderTimer.Start();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _hasPrevious = false;
            _maskDirty = true;
            _revealAllUntilTicks = 0;
            _nextMouseExpiryTicks = 0;
            _lastForegroundWindow = IntPtr.Zero;
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            _hasMouseGridPosition = false;

            _detectedRegions.Clear();
            _trackedRegions.Clear();
            _visibleRectangles.Clear();

            Array.Clear(
                _previousFrame,
                0,
                _previousFrame.Length);
            Array.Clear(
                _rawMotion,
                0,
                _rawMotion.Length);
            Array.Clear(
                _expandedMotion,
                0,
                _expandedMotion.Length);
            Array.Clear(
                _visited,
                0,
                _visited.Length);
            Array.Clear(
                _mouseRevealUntilTicks,
                0,
                _mouseRevealUntilTicks.Length);
        }

        PushMask();
    }

    public void RevealAll(TimeSpan duration)
    {
        lock (_sync)
        {
            _revealAllUntilTicks =
                Stopwatch.GetTimestamp() +
                ToStopwatchTicks(
                    duration.TotalMilliseconds);
            _maskDirty = true;
        }
    }

    private async Task CaptureLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var shouldCapture = false;

                lock (_sync)
                {
                    shouldCapture = _enabled;
                }

                if (shouldCapture)
                {
                    var foregroundWindow =
                        GetForegroundWindow();
                    AnalyzeCapture(
                        _sampler.Capture(),
                        foregroundWindow);
                }

                await Task.Delay(
                    _settings.MotionZoneSamplingMilliseconds,
                    _cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(
                    500,
                    _cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
    }

    private void AnalyzeCapture(
        byte[] current,
        IntPtr foregroundWindow)
    {
        var now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            if (!_hasPrevious)
            {
                Buffer.BlockCopy(
                    current,
                    0,
                    _previousFrame,
                    0,
                    current.Length);
                _hasPrevious = true;
                _lastForegroundWindow =
                    foregroundWindow;
                return;
            }

            DetectMotion(current);
            ExpandMotion();
            BuildDetectedRegions();
            MergeNearbyDetectedRegions();

            var foregroundChanged =
                foregroundWindow != IntPtr.Zero &&
                _lastForegroundWindow != IntPtr.Zero &&
                foregroundWindow !=
                    _lastForegroundWindow;

            if (foregroundWindow != IntPtr.Zero)
            {
                _lastForegroundWindow =
                    foregroundWindow;
            }

            var sceneChanged =
                foregroundChanged ||
                IsLargeSceneChange();

            if (sceneChanged)
            {
                _trackedRegions.Clear();
            }

            UpdateTrackedRegions(now);

            Buffer.BlockCopy(
                current,
                0,
                _previousFrame,
                0,
                current.Length);

            _maskDirty = true;
        }
    }

    private void DetectMotion(byte[] current)
    {
        Array.Clear(
            _rawMotion,
            0,
            _rawMotion.Length);

        var pixelThreshold =
            _settings.MotionZonePixelThreshold;
        var sampleCount =
            _samplesPerCell *
            _samplesPerCell;
        var minimumChangedSamples = Math.Max(
            1,
            (int)Math.Ceiling(
                sampleCount *
                _settings.MotionZoneChangedFraction));

        for (var row = 0;
             row < _rows;
             row++)
        {
            var sampleStartY =
                row * _samplesPerCell;

            for (var column = 0;
                 column < _columns;
                 column++)
            {
                var sampleStartX =
                    column * _samplesPerCell;
                var changedSamples = 0;
                var maximumDifference = 0;
                var differenceTotal = 0;

                for (var y = 0;
                     y < _samplesPerCell;
                     y++)
                {
                    var rowOffset =
                        (sampleStartY + y) *
                        _sampleStride;

                    for (var x = 0;
                         x < _samplesPerCell;
                         x++)
                    {
                        var offset =
                            rowOffset +
                            (sampleStartX + x) * 4;

                        var blueDifference = Math.Abs(
                            current[offset] -
                            _previousFrame[offset]);
                        var greenDifference = Math.Abs(
                            current[offset + 1] -
                            _previousFrame[offset + 1]);
                        var redDifference = Math.Abs(
                            current[offset + 2] -
                            _previousFrame[offset + 2]);

                        var difference = Math.Max(
                            redDifference,
                            Math.Max(
                                greenDifference,
                                blueDifference));

                        differenceTotal += difference;
                        maximumDifference = Math.Max(
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

                _rawMotion[
                    row * _columns +
                    column] =
                    changedSamples >=
                        minimumChangedSamples ||
                    maximumDifference >=
                        pixelThreshold * 2 ||
                    meanDifference >=
                        pixelThreshold * 0.55;
            }
        }
    }

    private void ExpandMotion()
    {
        Array.Clear(
            _expandedMotion,
            0,
            _expandedMotion.Length);

        var radius =
            _settings.MotionZoneMergeRadiusCells;

        for (var row = 0;
             row < _rows;
             row++)
        {
            for (var column = 0;
                 column < _columns;
                 column++)
            {
                var index =
                    row * _columns +
                    column;

                if (!_rawMotion[index])
                {
                    continue;
                }

                for (var offsetY = -radius;
                     offsetY <= radius;
                     offsetY++)
                {
                    var targetRow =
                        row + offsetY;

                    if (targetRow < 0 ||
                        targetRow >= _rows)
                    {
                        continue;
                    }

                    for (var offsetX = -radius;
                         offsetX <= radius;
                         offsetX++)
                    {
                        var targetColumn =
                            column + offsetX;

                        if (targetColumn < 0 ||
                            targetColumn >= _columns)
                        {
                            continue;
                        }

                        _expandedMotion[
                            targetRow * _columns +
                            targetColumn] = true;
                    }
                }
            }
        }
    }

    private void BuildDetectedRegions()
    {
        _detectedRegions.Clear();

        Array.Clear(
            _visited,
            0,
            _visited.Length);

        var padding =
            _settings.MotionZonePaddingCells;
        var minimumMotionCells =
            _settings.MotionZoneMinimumMotionCells;

        for (var start = 0;
             start < _expandedMotion.Length;
             start++)
        {
            if (!_expandedMotion[start] ||
                _visited[start])
            {
                continue;
            }

            var head = 0;
            var tail = 0;
            _queue[tail++] = start;
            _visited[start] = true;

            var minimumRow =
                start / _columns;
            var maximumRow =
                minimumRow;
            var minimumColumn =
                start % _columns;
            var maximumColumn =
                minimumColumn;
            var rawMotionCells = 0;

            while (head < tail)
            {
                var index =
                    _queue[head++];
                var row =
                    index / _columns;
                var column =
                    index % _columns;

                minimumRow = Math.Min(
                    minimumRow,
                    row);
                maximumRow = Math.Max(
                    maximumRow,
                    row);
                minimumColumn = Math.Min(
                    minimumColumn,
                    column);
                maximumColumn = Math.Max(
                    maximumColumn,
                    column);

                if (_rawMotion[index])
                {
                    rawMotionCells++;
                }

                for (var offsetY = -1;
                     offsetY <= 1;
                     offsetY++)
                {
                    var neighbourRow =
                        row + offsetY;

                    if (neighbourRow < 0 ||
                        neighbourRow >= _rows)
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
                            column + offsetX;

                        if (neighbourColumn < 0 ||
                            neighbourColumn >= _columns)
                        {
                            continue;
                        }

                        var neighbourIndex =
                            neighbourRow *
                            _columns +
                            neighbourColumn;

                        if (!_visited[neighbourIndex] &&
                            _expandedMotion[
                                neighbourIndex])
                        {
                            _visited[neighbourIndex] =
                                true;
                            _queue[tail++] =
                                neighbourIndex;
                        }
                    }
                }
            }

            if (rawMotionCells <
                minimumMotionCells)
            {
                continue;
            }

            minimumRow = Math.Max(
                0,
                minimumRow - padding);
            maximumRow = Math.Min(
                _rows - 1,
                maximumRow + padding);
            minimumColumn = Math.Max(
                0,
                minimumColumn - padding);
            maximumColumn = Math.Min(
                _columns - 1,
                maximumColumn + padding);

            _detectedRegions.Add(
                new DetectedRegion(
                    minimumRow,
                    maximumRow,
                    minimumColumn,
                    maximumColumn,
                    rawMotionCells));
        }
    }

    private void MergeNearbyDetectedRegions()
    {
        var gap =
            _settings.MotionZoneRenderMergeGapCells;
        var merged = true;

        while (merged)
        {
            merged = false;

            for (var firstIndex = 0;
                 firstIndex <
                    _detectedRegions.Count;
                 firstIndex++)
            {
                for (var secondIndex =
                         firstIndex + 1;
                     secondIndex <
                        _detectedRegions.Count;
                     secondIndex++)
                {
                    var first =
                        _detectedRegions[firstIndex];
                    var second =
                        _detectedRegions[secondIndex];

                    if (!RectanglesNear(
                            first.MinimumRow,
                            first.MaximumRow,
                            first.MinimumColumn,
                            first.MaximumColumn,
                            second.MinimumRow,
                            second.MaximumRow,
                            second.MinimumColumn,
                            second.MaximumColumn,
                            gap))
                    {
                        continue;
                    }

                    _detectedRegions[firstIndex] =
                        Union(first, second);
                    _detectedRegions.RemoveAt(
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

    private bool IsLargeSceneChange()
    {
        var changedCells = 0;
        var changedInsideTrackedRegions = 0;

        for (var index = 0;
             index < _rawMotion.Length;
             index++)
        {
            if (!_rawMotion[index])
            {
                continue;
            }

            changedCells++;

            if (IsCellInsideTrackedRegion(
                    index / _columns,
                    index % _columns))
            {
                changedInsideTrackedRegions++;
            }
        }

        if (changedCells <
            _rawMotion.Length *
            _settings.MotionZoneSceneChangeFraction)
        {
            return false;
        }

        if (_trackedRegions.Count == 0)
        {
            return true;
        }

        var overlapFraction =
            changedInsideTrackedRegions /
            (double)Math.Max(1, changedCells);

        // A video, animation or typing region may change heavily every frame.
        // Keep it active when most new movement remains inside the existing
        // active block. A real page/window replacement changes mostly outside
        // the old block and therefore clears it immediately.
        return overlapFraction <
            Math.Min(
                _settings.MotionZoneSceneChangeOverlapFraction,
                0.20);
    }

    private bool IsCellInsideTrackedRegion(
        int row,
        int column)
    {
        foreach (var region in
                 _trackedRegions)
        {
            if (row >= region.MinimumRow &&
                row <= region.MaximumRow &&
                column >= region.MinimumColumn &&
                column <= region.MaximumColumn)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateTrackedRegions(long now)
    {
        var recurringWindowTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringWindowMilliseconds);
        var recurringMinimumSpanTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringMinimumSpanMilliseconds);
        var trackingGap =
            _settings.MotionZoneTrackingGapCells;

        foreach (var detected in
                 _detectedRegions)
        {
            TrackedRegion? best = null;
            var bestScore = int.MinValue;

            foreach (var tracked in
                     _trackedRegions)
            {
                if (tracked.LastHitCaptureTicks ==
                        now &&
                    !tracked.Recurring)
                {
                    continue;
                }

                if (!RectanglesNear(
                        tracked.MinimumRow,
                        tracked.MaximumRow,
                        tracked.MinimumColumn,
                        tracked.MaximumColumn,
                        detected.MinimumRow,
                        detected.MaximumRow,
                        detected.MinimumColumn,
                        detected.MaximumColumn,
                        trackingGap))
                {
                    continue;
                }

                var intersectionRows = Math.Max(
                    0,
                    Math.Min(
                        tracked.MaximumRow,
                        detected.MaximumRow) -
                    Math.Max(
                        tracked.MinimumRow,
                        detected.MinimumRow) + 1);
                var intersectionColumns = Math.Max(
                    0,
                    Math.Min(
                        tracked.MaximumColumn,
                        detected.MaximumColumn) -
                    Math.Max(
                        tracked.MinimumColumn,
                        detected.MinimumColumn) + 1);
                var intersection =
                    intersectionRows *
                    intersectionColumns;
                var distance =
                    AxisGap(
                        tracked.MinimumRow,
                        tracked.MaximumRow,
                        detected.MinimumRow,
                        detected.MaximumRow) +
                    AxisGap(
                        tracked.MinimumColumn,
                        tracked.MaximumColumn,
                        detected.MinimumColumn,
                        detected.MaximumColumn);
                var score =
                    intersection * 100 -
                    distance * 10;

                if (score > bestScore)
                {
                    best = tracked;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                _trackedRegions.Add(
                    new TrackedRegion
                    {
                        MinimumRow =
                            detected.MinimumRow,
                        MaximumRow =
                            detected.MaximumRow,
                        MinimumColumn =
                            detected.MinimumColumn,
                        MaximumColumn =
                            detected.MaximumColumn,
                        WindowStartTicks = now,
                        LastMotionTicks = now,
                        LastHitCaptureTicks = now,
                        MotionHits = 1,
                        Recurring = false
                    });
                continue;
            }

            // Preserve the complete active region. A smaller moving fragment
            // refreshes the existing rectangle but can never shrink it.
            best.MinimumRow = Math.Min(
                best.MinimumRow,
                detected.MinimumRow);
            best.MaximumRow = Math.Max(
                best.MaximumRow,
                detected.MaximumRow);
            best.MinimumColumn = Math.Min(
                best.MinimumColumn,
                detected.MinimumColumn);
            best.MaximumColumn = Math.Max(
                best.MaximumColumn,
                detected.MaximumColumn);
            best.LastMotionTicks = now;
            best.LastHitCaptureTicks = now;

            if (best.WindowStartTicks == 0 ||
                now - best.WindowStartTicks >
                    recurringWindowTicks)
            {
                best.WindowStartTicks = now;
                best.MotionHits = 2;
            }
            else
            {
                best.MotionHits = Math.Min(
                    int.MaxValue,
                    best.MotionHits + 1);
            }

            // This region has been detected on at least two captures.
            // It is active now, independently of the old timing classifier.
            best.Recurring = true;
        }
    }

    private bool RemoveExpiredRegions(long now)
    {
        var changed = false;
        var oneShotTicks =
            ToStopwatchTicks(
                _settings.MotionZoneOneShotHoldMilliseconds);
        var recurringTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringHoldMilliseconds);

        for (var index =
                 _trackedRegions.Count - 1;
             index >= 0;
             index--)
        {
            var region =
                _trackedRegions[index];
            var holdTicks =
                region.Recurring
                    ? recurringTicks
                    : oneShotTicks;

            if (now - region.LastMotionTicks <
                holdTicks)
            {
                continue;
            }

            _trackedRegions.RemoveAt(index);
            changed = true;
        }

        return changed;
    }

    private void OnRenderTick(
        object? sender,
        EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        NativeMethods.GetCursorPos(
            out var cursor);
        var shouldPush = false;

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            var cursorChanged =
                cursor.X != _lastCursorX ||
                cursor.Y != _lastCursorY;
            var regionExpired =
                RemoveExpiredRegions(now);
            var revealAllExpired =
                _revealAllUntilTicks != 0 &&
                now >= _revealAllUntilTicks;

            if (revealAllExpired)
            {
                _revealAllUntilTicks = 0;
            }

            shouldPush =
                _maskDirty ||
                cursorChanged ||
                regionExpired ||
                revealAllExpired;

            _lastCursorX = cursor.X;
            _lastCursorY = cursor.Y;
            _maskDirty = false;
        }

        if (shouldPush)
        {
            PushMask();
        }
    }

    private bool UpdateMouseTrail(long now)
    {
        if (!NativeMethods.GetCursorPos(
                out var cursor))
        {
            _hasMouseGridPosition = false;
            return false;
        }

        var bounds = _screen.Bounds;

        if (!bounds.Contains(
                cursor.X,
                cursor.Y))
        {
            _hasMouseGridPosition = false;
            return false;
        }

        var column = Math.Clamp(
            (cursor.X - bounds.Left) *
            _columns /
            Math.Max(1, bounds.Width),
            0,
            _columns - 1);
        var row = Math.Clamp(
            (cursor.Y - bounds.Top) *
            _rows /
            Math.Max(1, bounds.Height),
            0,
            _rows - 1);

        if (_hasMouseGridPosition &&
            row == _lastMouseRow &&
            column == _lastMouseColumn)
        {
            return false;
        }

        var revealUntil =
            now +
            ToStopwatchTicks(
                _settings.MouseRevealHoldMilliseconds);

        if (_hasMouseGridPosition)
        {
            StampMouseLine(
                _lastMouseRow,
                _lastMouseColumn,
                row,
                column,
                revealUntil);
        }
        else
        {
            StampMouseBlock(
                row,
                column,
                revealUntil);
        }

        _hasMouseGridPosition = true;
        _lastMouseRow = row;
        _lastMouseColumn = column;

        if (_nextMouseExpiryTicks == 0 ||
            revealUntil <
                _nextMouseExpiryTicks)
        {
            _nextMouseExpiryTicks =
                revealUntil;
        }

        return true;
    }

    private void StampMouseLine(
        int startRow,
        int startColumn,
        int endRow,
        int endColumn,
        long revealUntil)
    {
        var column = startColumn;
        var row = startRow;
        var deltaColumn = Math.Abs(
            endColumn - startColumn);
        var deltaRow = -Math.Abs(
            endRow - startRow);
        var stepColumn =
            startColumn < endColumn
                ? 1
                : -1;
        var stepRow =
            startRow < endRow
                ? 1
                : -1;
        var error =
            deltaColumn + deltaRow;

        while (true)
        {
            StampMouseBlock(
                row,
                column,
                revealUntil);

            if (row == endRow &&
                column == endColumn)
            {
                break;
            }

            var doubleError =
                2 * error;

            if (doubleError >= deltaRow)
            {
                error += deltaRow;
                column += stepColumn;
            }

            if (doubleError <= deltaColumn)
            {
                error += deltaColumn;
                row += stepRow;
            }
        }
    }

    private void StampMouseBlock(
        int centerRow,
        int centerColumn,
        long revealUntil)
    {
        var bounds = _screen.Bounds;
        var cellWidth =
            bounds.Width /
            (double)_columns;
        var cellHeight =
            bounds.Height /
            (double)_rows;
        var radiusPixels = Math.Max(
            4,
            _settings.MouseHoverRadiusPixels);
        var halfColumns = Math.Max(
            1,
            (int)Math.Ceiling(
                radiusPixels /
                Math.Max(1.0, cellWidth)));
        var halfRows = Math.Max(
            1,
            (int)Math.Ceiling(
                radiusPixels /
                Math.Max(1.0, cellHeight)));

        var minimumRow = Math.Max(
            0,
            centerRow - halfRows);
        var maximumRow = Math.Min(
            _rows - 1,
            centerRow + halfRows);
        var minimumColumn = Math.Max(
            0,
            centerColumn - halfColumns);
        var maximumColumn = Math.Min(
            _columns - 1,
            centerColumn + halfColumns);

        for (var row = minimumRow;
             row <= maximumRow;
             row++)
        {
            for (var column = minimumColumn;
                 column <= maximumColumn;
                 column++)
            {
                var index =
                    row * _columns +
                    column;

                if (_mouseRevealUntilTicks[index] <
                    revealUntil)
                {
                    _mouseRevealUntilTicks[index] =
                        revealUntil;
                }
            }
        }
    }

    private bool RemoveExpiredMouseCells(
        long now)
    {
        if (_nextMouseExpiryTicks == 0 ||
            now < _nextMouseExpiryTicks)
        {
            return false;
        }

        var changed = false;
        var nextExpiry = 0L;

        for (var index = 0;
             index <
                _mouseRevealUntilTicks.Length;
             index++)
        {
            var expiry =
                _mouseRevealUntilTicks[index];

            if (expiry == 0)
            {
                continue;
            }

            if (expiry <= now)
            {
                _mouseRevealUntilTicks[index] = 0;
                changed = true;
                continue;
            }

            if (nextExpiry == 0 ||
                expiry < nextExpiry)
            {
                nextExpiry = expiry;
            }
        }

        _nextMouseExpiryTicks =
            nextExpiry;
        return changed;
    }

    private void PushMask()
    {
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            var revealAll =
                !_enabled ||
                (_revealAllUntilTicks != 0 &&
                 now < _revealAllUntilTicks);

            if (revealAll)
            {
                Array.Clear(
                    _renderAlpha,
                    0,
                    _renderAlpha.Length);
            }
            else
            {
                var maximumOpacity =
                    (float)_settings
                        .MaximumMaskOpacity;

                Array.Fill(
                    _renderAlpha,
                    maximumOpacity);

                BuildVisibleRectangles();

                foreach (var rectangle in
                         _visibleRectangles)
                {
                    SetRectangleClear(
                        rectangle.MinimumRow,
                        rectangle.MaximumRow,
                        rectangle.MinimumColumn,
                        rectangle.MaximumColumn);
                }

                ApplyCurrentMouseBlockReveal();
            }
        }

        _overlay.SetMask(
            _renderAlpha,
            _columns,
            _rows);
    }

    private void ApplyCurrentMouseBlockReveal()
    {
        if (!NativeMethods.GetCursorPos(
                out var cursor))
        {
            return;
        }

        var bounds = _screen.Bounds;

        if (!bounds.Contains(
                cursor.X,
                cursor.Y))
        {
            return;
        }

        var centerColumn = Math.Clamp(
            (cursor.X - bounds.Left) *
            _columns /
            Math.Max(1, bounds.Width),
            0,
            _columns - 1);
        var centerRow = Math.Clamp(
            (cursor.Y - bounds.Top) *
            _rows /
            Math.Max(1, bounds.Height),
            0,
            _rows - 1);
        var cellWidth =
            bounds.Width /
            (double)_columns;
        var cellHeight =
            bounds.Height /
            (double)_rows;
        var radiusPixels = Math.Max(
            4,
            _settings.MouseHoverRadiusPixels);
        var halfColumns = Math.Max(
            1,
            (int)Math.Ceiling(
                radiusPixels /
                Math.Max(1.0, cellWidth)));
        var halfRows = Math.Max(
            1,
            (int)Math.Ceiling(
                radiusPixels /
                Math.Max(1.0, cellHeight)));

        SetRectangleClear(
            centerRow - halfRows,
            centerRow + halfRows,
            centerColumn - halfColumns,
            centerColumn + halfColumns);
    }

    private void BuildVisibleRectangles()
    {
        _visibleRectangles.Clear();

        foreach (var region in
                 _trackedRegions)
        {
            _visibleRectangles.Add(
                new DetectedRegion(
                    region.MinimumRow,
                    region.MaximumRow,
                    region.MinimumColumn,
                    region.MaximumColumn,
                    0));
        }
    }

    private void SetRectangleClear(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn)
    {
        minimumRow = Math.Clamp(
            minimumRow,
            0,
            _rows - 1);
        maximumRow = Math.Clamp(
            maximumRow,
            0,
            _rows - 1);
        minimumColumn = Math.Clamp(
            minimumColumn,
            0,
            _columns - 1);
        maximumColumn = Math.Clamp(
            maximumColumn,
            0,
            _columns - 1);

        for (var row = minimumRow;
             row <= maximumRow;
             row++)
        {
            for (var column = minimumColumn;
                 column <= maximumColumn;
                 column++)
            {
                _renderAlpha[
                    row * _columns +
                    column] = 0f;
            }
        }
    }

    private static DetectedRegion Union(
        DetectedRegion first,
        DetectedRegion second) =>
        new(
            Math.Min(
                first.MinimumRow,
                second.MinimumRow),
            Math.Max(
                first.MaximumRow,
                second.MaximumRow),
            Math.Min(
                first.MinimumColumn,
                second.MinimumColumn),
            Math.Max(
                first.MaximumColumn,
                second.MaximumColumn),
            first.MotionCells +
                second.MotionCells);

    private static bool RectanglesNear(
        int firstMinimumRow,
        int firstMaximumRow,
        int firstMinimumColumn,
        int firstMaximumColumn,
        int secondMinimumRow,
        int secondMaximumRow,
        int secondMinimumColumn,
        int secondMaximumColumn,
        int maximumGap) =>
        AxisGap(
            firstMinimumRow,
            firstMaximumRow,
            secondMinimumRow,
            secondMaximumRow) <= maximumGap &&
        AxisGap(
            firstMinimumColumn,
            firstMaximumColumn,
            secondMinimumColumn,
            secondMaximumColumn) <= maximumGap;

    private static int AxisGap(
        int firstMinimum,
        int firstMaximum,
        int secondMinimum,
        int secondMaximum)
    {
        if (firstMaximum <
            secondMinimum)
        {
            return secondMinimum -
                   firstMaximum -
                   1;
        }

        if (secondMaximum <
            firstMinimum)
        {
            return firstMinimum -
                   secondMaximum -
                   1;
        }

        return 0;
    }

    private static long ToStopwatchTicks(
        double milliseconds) =>
        (long)(
            milliseconds *
            Stopwatch.Frequency /
            1000.0);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _renderTimer.Stop();
        _sampler.Dispose();
        _overlay.Close();
        _cancellation.Dispose();
    }
}
