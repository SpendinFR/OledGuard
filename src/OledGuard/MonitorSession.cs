using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

        public long GeometryWindowStartTicks;
        public bool HasRecentBounds;
        public int RecentMinimumRow;
        public int RecentMaximumRow;
        public int RecentMinimumColumn;
        public int RecentMaximumColumn;
        public int RecentDetectionCount;
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
    private readonly long[] _motionCellLastTicks;
    private readonly long[] _motionCellWindowStartTicks;
    private readonly int[] _motionCellHits;
    private readonly bool[] _motionCellRecurring;

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
    private long _nextMotionCellExpiryTicks;
    private IntPtr _lastForegroundWindow;
    private string _lastForegroundTitle = string.Empty;
    private long _sceneSettleUntilTicks;
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
        _motionCellLastTicks = new long[cellCount];
        _motionCellWindowStartTicks = new long[cellCount];
        _motionCellHits = new int[cellCount];
        _motionCellRecurring = new bool[cellCount];

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
            _nextMotionCellExpiryTicks = 0;
            _lastForegroundWindow = IntPtr.Zero;
            _lastForegroundTitle = string.Empty;
            _sceneSettleUntilTicks = 0;
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
            ClearMotionCellState();
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
                    var foregroundTitle =
                        GetWindowTitle(
                            foregroundWindow);
                    AnalyzeCapture(
                        _sampler.Capture(),
                        foregroundWindow,
                        foregroundTitle);
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
        IntPtr foregroundWindow,
        string foregroundTitle)
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
                SetCaptureBaseline(
                    current,
                    foregroundWindow,
                    foregroundTitle);
                return;
            }

            var foregroundChanged =
                foregroundWindow != IntPtr.Zero &&
                _lastForegroundWindow != IntPtr.Zero &&
                foregroundWindow !=
                    _lastForegroundWindow;
            var titleChanged =
                !string.IsNullOrWhiteSpace(
                    foregroundTitle) &&
                !string.IsNullOrWhiteSpace(
                    _lastForegroundTitle) &&
                !string.Equals(
                    foregroundTitle,
                    _lastForegroundTitle,
                    StringComparison.Ordinal);

            UpdateForegroundIdentity(
                foregroundWindow,
                foregroundTitle);

            if (foregroundChanged ||
                titleChanged)
            {
                ResetSceneToBaseline(
                    current,
                    now);
                return;
            }

            if (_sceneSettleUntilTicks != 0 &&
                now < _sceneSettleUntilTicks)
            {
                CopyCurrentToPrevious(current);
                _maskDirty = true;
                return;
            }

            _sceneSettleUntilTicks = 0;

            DetectMotion(current);
            UpdateChangedCells(now);
            CopyCurrentToPrevious(current);
            _maskDirty = true;
        }
    }

    private void SetCaptureBaseline(
        byte[] current,
        IntPtr foregroundWindow,
        string foregroundTitle)
    {
        CopyCurrentToPrevious(current);
        _hasPrevious = true;
        _lastForegroundWindow =
            foregroundWindow;
        _lastForegroundTitle =
            foregroundTitle;
    }

    private void UpdateForegroundIdentity(
        IntPtr foregroundWindow,
        string foregroundTitle)
    {
        if (foregroundWindow != IntPtr.Zero)
        {
            _lastForegroundWindow =
                foregroundWindow;
        }

        if (!string.IsNullOrWhiteSpace(
                foregroundTitle))
        {
            _lastForegroundTitle =
                foregroundTitle;
        }
    }

    private void ResetSceneToBaseline(
        byte[] current,
        long now)
    {
        _trackedRegions.Clear();
        _detectedRegions.Clear();
        _visibleRectangles.Clear();
        ClearMotionCellState();

        _sceneSettleUntilTicks =
            now +
            ToStopwatchTicks(
                _settings.MotionZoneSceneSettleMilliseconds);

        CopyCurrentToPrevious(current);
        _maskDirty = true;
    }

    private void CopyCurrentToPrevious(
        byte[] current)
    {
        Buffer.BlockCopy(
            current,
            0,
            _previousFrame,
            0,
            current.Length);
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

    private void ClearMotionCellState()
    {
        Array.Clear(
            _motionCellLastTicks,
            0,
            _motionCellLastTicks.Length);
        Array.Clear(
            _motionCellWindowStartTicks,
            0,
            _motionCellWindowStartTicks.Length);
        Array.Clear(
            _motionCellHits,
            0,
            _motionCellHits.Length);
        Array.Clear(
            _motionCellRecurring,
            0,
            _motionCellRecurring.Length);
        _nextMotionCellExpiryTicks = 0;
    }

    private void UpdateChangedCells(long now)
    {
        var recurringWindowTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringWindowMilliseconds);
        var recurringMinimumSpanTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringMinimumSpanMilliseconds);
        var oneShotHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneOneShotHoldMilliseconds);
        var recurringHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringHoldMilliseconds);
        var recurringHits =
            _settings.MotionZoneRecurringHits;

        for (var index = 0;
             index < _rawMotion.Length;
             index++)
        {
            if (!_rawMotion[index])
            {
                continue;
            }

            var previous =
                _motionCellLastTicks[index];

            if (previous == 0 ||
                now - previous >
                    recurringWindowTicks)
            {
                _motionCellWindowStartTicks[index] =
                    now;
                _motionCellHits[index] = 1;
                _motionCellRecurring[index] = false;
            }
            else
            {
                _motionCellHits[index] =
                    Math.Min(
                        int.MaxValue,
                        _motionCellHits[index] + 1);

                if (!_motionCellRecurring[index] &&
                    _motionCellHits[index] >=
                        recurringHits &&
                    now -
                        _motionCellWindowStartTicks[index] >=
                        recurringMinimumSpanTicks)
                {
                    _motionCellRecurring[index] = true;
                }
            }

            _motionCellLastTicks[index] = now;

            var holdTicks =
                _motionCellRecurring[index]
                    ? recurringHoldTicks
                    : oneShotHoldTicks;
            var expiry =
                now +
                holdTicks;

            if (_nextMotionCellExpiryTicks == 0 ||
                expiry <
                    _nextMotionCellExpiryTicks)
            {
                _nextMotionCellExpiryTicks =
                    expiry;
            }
        }
    }

    private bool ExpireChangedCells(long now)
    {
        if (_nextMotionCellExpiryTicks == 0 ||
            now <
                _nextMotionCellExpiryTicks)
        {
            return false;
        }

        var changed = false;
        var nextExpiry = 0L;
        var oneShotHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneOneShotHoldMilliseconds);
        var recurringHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringHoldMilliseconds);

        for (var index = 0;
             index < _motionCellLastTicks.Length;
             index++)
        {
            var last =
                _motionCellLastTicks[index];

            if (last == 0)
            {
                continue;
            }

            var holdTicks =
                _motionCellRecurring[index]
                    ? recurringHoldTicks
                    : oneShotHoldTicks;
            var expiry =
                last +
                holdTicks;

            if (expiry <= now)
            {
                _motionCellLastTicks[index] = 0;
                _motionCellWindowStartTicks[index] = 0;
                _motionCellHits[index] = 0;
                _motionCellRecurring[index] = false;
                changed = true;
                continue;
            }

            if (nextExpiry == 0 ||
                expiry <
                    nextExpiry)
            {
                nextExpiry = expiry;
            }
        }

        _nextMotionCellExpiryTicks =
            nextExpiry;
        return changed;
    }

    private void ApplyChangedCellReveal(long now)
    {
        var oneShotHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneOneShotHoldMilliseconds);
        var recurringHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringHoldMilliseconds);

        for (var index = 0;
             index < _motionCellLastTicks.Length;
             index++)
        {
            var last =
                _motionCellLastTicks[index];

            if (last == 0)
            {
                continue;
            }

            var holdTicks =
                _motionCellRecurring[index]
                    ? recurringHoldTicks
                    : oneShotHoldTicks;

            if (now - last <
                holdTicks)
            {
                _renderAlpha[index] = 0f;
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
            return false;
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
        var geometryRefreshTicks =
            ToStopwatchTicks(
                _settings.MotionZoneGeometryRefreshMilliseconds);
        var trackingGap =
            _settings.MotionZoneTrackingGapCells;

        foreach (var detected in
                 _detectedRegions)
        {
            var detectedArea = Math.Max(
                1,
                detected.Area);
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

                GetEffectiveBounds(
                    tracked,
                    out var trackedMinimumRow,
                    out var trackedMaximumRow,
                    out var trackedMinimumColumn,
                    out var trackedMaximumColumn);

                if (!RectanglesNear(
                        trackedMinimumRow,
                        trackedMaximumRow,
                        trackedMinimumColumn,
                        trackedMaximumColumn,
                        detected.MinimumRow,
                        detected.MaximumRow,
                        detected.MinimumColumn,
                        detected.MaximumColumn,
                        trackingGap))
                {
                    continue;
                }

                var trackedArea = Math.Max(
                    1,
                    RectangleArea(
                        trackedMinimumRow,
                        trackedMaximumRow,
                        trackedMinimumColumn,
                        trackedMaximumColumn));
                var intersection =
                    IntersectionArea(
                        trackedMinimumRow,
                        trackedMaximumRow,
                        trackedMinimumColumn,
                        trackedMaximumColumn,
                        detected.MinimumRow,
                        detected.MaximumRow,
                        detected.MinimumColumn,
                        detected.MaximumColumn);
                var detectedInside =
                    intersection /
                    (double)detectedArea;
                var trackedInside =
                    intersection /
                    (double)trackedArea;
                var sizeRatio =
                    Math.Min(
                        trackedArea,
                        detectedArea) /
                    (double)Math.Max(
                        trackedArea,
                        detectedArea);
                var distance =
                    AxisGap(
                        trackedMinimumRow,
                        trackedMaximumRow,
                        detected.MinimumRow,
                        detected.MaximumRow) +
                    AxisGap(
                        trackedMinimumColumn,
                        trackedMaximumColumn,
                        detected.MinimumColumn,
                        detected.MaximumColumn);

                if (intersection == 0 &&
                    distance > trackingGap / 2 &&
                    sizeRatio < 0.35)
                {
                    continue;
                }

                var score =
                    intersection * 1000 +
                    (int)Math.Round(
                        detectedInside * 300.0) +
                    (int)Math.Round(
                        trackedInside * 120.0) +
                    (int)Math.Round(
                        sizeRatio * 80.0) -
                    distance * 25;

                if (score > bestScore)
                {
                    best = tracked;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                var created =
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
                        Recurring = false,
                        GeometryWindowStartTicks = now
                    };

                AccumulateRecentBounds(
                    created,
                    detected);
                _trackedRegions.Add(created);
                continue;
            }

            AccumulateRecentBounds(
                best,
                detected);
            RefreshTrackedRegion(
                best,
                now,
                recurringWindowTicks);
        }

        CommitRecentGeometry(
            now,
            geometryRefreshTicks);
    }

    private static void GetEffectiveBounds(
        TrackedRegion region,
        out int minimumRow,
        out int maximumRow,
        out int minimumColumn,
        out int maximumColumn)
    {
        minimumRow = region.MinimumRow;
        maximumRow = region.MaximumRow;
        minimumColumn =
            region.MinimumColumn;
        maximumColumn =
            region.MaximumColumn;

        if (!region.HasRecentBounds)
        {
            return;
        }

        minimumRow = Math.Min(
            minimumRow,
            region.RecentMinimumRow);
        maximumRow = Math.Max(
            maximumRow,
            region.RecentMaximumRow);
        minimumColumn = Math.Min(
            minimumColumn,
            region.RecentMinimumColumn);
        maximumColumn = Math.Max(
            maximumColumn,
            region.RecentMaximumColumn);
    }

    private static void AccumulateRecentBounds(
        TrackedRegion region,
        DetectedRegion detected)
    {
        if (!region.HasRecentBounds)
        {
            region.RecentMinimumRow =
                detected.MinimumRow;
            region.RecentMaximumRow =
                detected.MaximumRow;
            region.RecentMinimumColumn =
                detected.MinimumColumn;
            region.RecentMaximumColumn =
                detected.MaximumColumn;
            region.HasRecentBounds = true;
            region.RecentDetectionCount = 1;
            return;
        }

        region.RecentMinimumRow = Math.Min(
            region.RecentMinimumRow,
            detected.MinimumRow);
        region.RecentMaximumRow = Math.Max(
            region.RecentMaximumRow,
            detected.MaximumRow);
        region.RecentMinimumColumn = Math.Min(
            region.RecentMinimumColumn,
            detected.MinimumColumn);
        region.RecentMaximumColumn = Math.Max(
            region.RecentMaximumColumn,
            detected.MaximumColumn);
        region.RecentDetectionCount++;
    }

    private void CommitRecentGeometry(
        long now,
        long geometryRefreshTicks)
    {
        foreach (var region in
                 _trackedRegions)
        {
            if (region.GeometryWindowStartTicks == 0)
            {
                region.GeometryWindowStartTicks =
                    now;
            }

            if (now -
                region.GeometryWindowStartTicks <
                geometryRefreshTicks)
            {
                continue;
            }

            if (region.HasRecentBounds)
            {
                // Keep the complete active rectangle while any movement
                // continues inside it. Recent motion may enlarge the region,
                // but it can never replace it with a smaller fragment.
                region.MinimumRow = Math.Min(
                    region.MinimumRow,
                    region.RecentMinimumRow);
                region.MaximumRow = Math.Max(
                    region.MaximumRow,
                    region.RecentMaximumRow);
                region.MinimumColumn = Math.Min(
                    region.MinimumColumn,
                    region.RecentMinimumColumn);
                region.MaximumColumn = Math.Max(
                    region.MaximumColumn,
                    region.RecentMaximumColumn);
            }

            region.HasRecentBounds = false;
            region.RecentDetectionCount = 0;
            region.GeometryWindowStartTicks =
                now;
        }
    }

    private static void RefreshTrackedRegion(
        TrackedRegion region,
        long now,
        long recurringWindowTicks)
    {
        region.LastMotionTicks = now;
        region.LastHitCaptureTicks = now;

        if (region.WindowStartTicks == 0 ||
            now - region.WindowStartTicks >
                recurringWindowTicks)
        {
            region.WindowStartTicks = now;
            region.MotionHits = 2;
        }
        else
        {
            region.MotionHits = Math.Min(
                int.MaxValue,
                region.MotionHits + 1);
        }

        region.Recurring = true;
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
        var now =
            Stopwatch.GetTimestamp();
        var shouldPush = false;

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            var changedCellExpired =
                ExpireChangedCells(now);
            var revealAllExpired =
                _revealAllUntilTicks != 0 &&
                now >=
                    _revealAllUntilTicks;

            if (revealAllExpired)
            {
                _revealAllUntilTicks = 0;
            }

            shouldPush =
                _maskDirty ||
                changedCellExpired ||
                revealAllExpired;
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
            0,
            (int)Math.Floor(
                radiusPixels /
                Math.Max(1.0, cellWidth)));
        var halfRows = Math.Max(
            0,
            (int)Math.Floor(
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
            var now =
                Stopwatch.GetTimestamp();
            var revealAll =
                !_enabled ||
                (_revealAllUntilTicks != 0 &&
                 now <
                    _revealAllUntilTicks);

            if (revealAll)
            {
                Array.Clear(
                    _renderAlpha,
                    0,
                    _renderAlpha.Length);
            }
            else
            {
                Array.Fill(
                    _renderAlpha,
                    (float)_settings
                        .MaximumMaskOpacity);
                ApplyChangedCellReveal(now);
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
            0,
            (int)Math.Floor(
                radiusPixels /
                Math.Max(1.0, cellWidth)));
        var halfRows = Math.Max(
            0,
            (int)Math.Floor(
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

        var minimumArea = Math.Max(
            _settings.MotionZoneMinimumVisibleAreaCells,
            _settings.MotionZoneMinimumMotionCells);
        var thinMinimumArea = Math.Max(
            minimumArea,
            _settings.MotionZoneThinRegionMinimumAreaCells);

        foreach (var region in
                 _trackedRegions)
        {
            var width =
                region.MaximumColumn -
                region.MinimumColumn +
                1;
            var height =
                region.MaximumRow -
                region.MinimumRow +
                1;
            var area =
                width * height;

            if (area < minimumArea)
            {
                continue;
            }

            if ((width <= 1 ||
                 height <= 1) &&
                area < thinMinimumArea)
            {
                continue;
            }

            _visibleRectangles.Add(
                new DetectedRegion(
                    region.MinimumRow,
                    region.MaximumRow,
                    region.MinimumColumn,
                    region.MaximumColumn,
                    0));
        }

        var gap =
            _settings.MotionZoneRenderMergeGapCells;
        var merged = true;

        while (merged)
        {
            merged = false;

            for (var firstIndex = 0;
                 firstIndex <
                    _visibleRectangles.Count;
                 firstIndex++)
            {
                for (var secondIndex =
                         firstIndex + 1;
                     secondIndex <
                        _visibleRectangles.Count;
                     secondIndex++)
                {
                    var first =
                        _visibleRectangles[firstIndex];
                    var second =
                        _visibleRectangles[secondIndex];

                    if (!ShouldMergeVisibleRectangles(
                            first,
                            second,
                            gap))
                    {
                        continue;
                    }

                    _visibleRectangles[firstIndex] =
                        Union(first, second);
                    _visibleRectangles.RemoveAt(
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

    private static bool ShouldMergeVisibleRectangles(
        DetectedRegion first,
        DetectedRegion second,
        int maximumGap)
    {
        var rowOverlap = Math.Max(
            0,
            Math.Min(
                first.MaximumRow,
                second.MaximumRow) -
            Math.Max(
                first.MinimumRow,
                second.MinimumRow) + 1);
        var columnOverlap = Math.Max(
            0,
            Math.Min(
                first.MaximumColumn,
                second.MaximumColumn) -
            Math.Max(
                first.MinimumColumn,
                second.MinimumColumn) + 1);
        var rowGap = AxisGap(
            first.MinimumRow,
            first.MaximumRow,
            second.MinimumRow,
            second.MaximumRow);
        var columnGap = AxisGap(
            first.MinimumColumn,
            first.MaximumColumn,
            second.MinimumColumn,
            second.MaximumColumn);

        if (rowOverlap > 0 &&
            columnGap <= maximumGap)
        {
            return true;
        }

        if (columnOverlap > 0 &&
            rowGap <= maximumGap)
        {
            return true;
        }

        return rowGap <= 1 &&
               columnGap <= 1;
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

    private static int RectangleArea(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn) =>
        Math.Max(
            0,
            maximumRow -
            minimumRow +
            1) *
        Math.Max(
            0,
            maximumColumn -
            minimumColumn +
            1);

    private static int IntersectionArea(
        int firstMinimumRow,
        int firstMaximumRow,
        int firstMinimumColumn,
        int firstMaximumColumn,
        int secondMinimumRow,
        int secondMaximumRow,
        int secondMinimumColumn,
        int secondMaximumColumn)
    {
        var rows = Math.Max(
            0,
            Math.Min(
                firstMaximumRow,
                secondMaximumRow) -
            Math.Max(
                firstMinimumRow,
                secondMinimumRow) + 1);
        var columns = Math.Max(
            0,
            Math.Min(
                firstMaximumColumn,
                secondMaximumColumn) -
            Math.Max(
                firstMinimumColumn,
                secondMinimumColumn) + 1);

        return rows * columns;
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

    private static string GetWindowTitle(
        IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer =
            new StringBuilder(512);
        var length = GetWindowText(
            window,
            buffer,
            buffer.Capacity);

        return length > 0
            ? buffer.ToString()
            : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maximumCharacters);

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
