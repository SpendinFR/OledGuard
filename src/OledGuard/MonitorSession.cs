using System.Diagnostics;
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
        int MotionCells);

    private sealed class TrackedRegion
    {
        public int Id;
        public int MinimumRow;
        public int MaximumRow;
        public int MinimumColumn;
        public int MaximumColumn;
        public long WindowStartTicks;
        public long LastMotionTicks;
        public long LastHitCaptureTicks;
        public int MotionHits;
        public int MotionCellsThisCapture;
        public float Reveal;
        public bool ClosingAnimation;
        public long ClosingAnimationStartTicks;
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

    private readonly List<DetectedRegion> _detectedRegions = new();
    private readonly List<TrackedRegion> _trackedRegions = new();

    private readonly object _sync = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _enabled;
    private bool _hasPrevious;
    private bool _maskDirty;
    private bool _disposed;
    private long _lastRenderTicks;
    private long _revealAllUntilTicks;
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;
    private bool _lastRevealAll;
    private int _nextRegionId = 1;

    private bool _startupSnakeActive;
    private long _startupSnakeStartTicks;

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

        _previousFrame =
            new byte[frameBytes];
        _rawMotion =
            new bool[cellCount];
        _expandedMotion =
            new bool[cellCount];
        _visited =
            new bool[cellCount];
        _queue =
            new int[cellCount];
        _renderAlpha =
            new float[cellCount];

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
        _captureLoop =
            Task.Run(CaptureLoopAsync);
        _renderTimer.Start();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _hasPrevious = false;
            _maskDirty = true;
            _lastRenderTicks = 0;
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            _lastRevealAll = false;
            _nextRegionId = 1;

            _detectedRegions.Clear();
            _trackedRegions.Clear();

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

            var now = Stopwatch.GetTimestamp();
            _revealAllUntilTicks = now;
            _startupSnakeActive =
                enabled &&
                _settings.PixelSnakeStartupEnabled;
            _startupSnakeStartTicks = now;
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
                    AnalyzeCapture(
                        _sampler.Capture());
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
                    1000,
                    _cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
    }

    private void AnalyzeCapture(byte[] current)
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
                return;
            }

            DetectMotion(current);
            ExpandMotion();
            BuildDetectedRegions();
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

                        if (difference >= pixelThreshold)
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
                            _visited[
                                neighbourIndex] = true;
                            _queue[tail++] =
                                neighbourIndex;
                        }
                    }
                }
            }

            if (rawMotionCells == 0)
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

    private void UpdateTrackedRegions(long now)
    {
        var recurringWindowTicks = ToStopwatchTicks(
            _settings.MotionZoneRecurringWindowMilliseconds);
        var trackingGap =
            _settings.MotionZoneTrackingGapCells;

        foreach (var tracked in _trackedRegions)
        {
            tracked.MotionCellsThisCapture = 0;
        }

        foreach (var detected in _detectedRegions)
        {
            TrackedRegion? best = null;
            var bestScore = int.MinValue;

            foreach (var tracked in _trackedRegions)
            {
                if (tracked.LastHitCaptureTicks == now)
                {
                    continue;
                }

                var rowGap = AxisGap(
                    tracked.MinimumRow,
                    tracked.MaximumRow,
                    detected.MinimumRow,
                    detected.MaximumRow);
                var columnGap = AxisGap(
                    tracked.MinimumColumn,
                    tracked.MaximumColumn,
                    detected.MinimumColumn,
                    detected.MaximumColumn);

                if (rowGap > trackingGap ||
                    columnGap > trackingGap)
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
                var score =
                    intersection * 100 -
                    (rowGap + columnGap) * 10;

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
                        Id = _nextRegionId++,
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
                        MotionCellsThisCapture =
                            detected.MotionCells,
                        Reveal = 0f
                    });
                continue;
            }

            best.MinimumRow = FollowMinimumBoundary(
                best.MinimumRow,
                detected.MinimumRow);
            best.MaximumRow = FollowMaximumBoundary(
                best.MaximumRow,
                detected.MaximumRow);
            best.MinimumColumn = FollowMinimumBoundary(
                best.MinimumColumn,
                detected.MinimumColumn);
            best.MaximumColumn = FollowMaximumBoundary(
                best.MaximumColumn,
                detected.MaximumColumn);
            best.LastMotionTicks = now;
            best.LastHitCaptureTicks = now;
            best.MotionCellsThisCapture =
                detected.MotionCells;
            best.ClosingAnimation = false;

            if (best.WindowStartTicks == 0 ||
                now - best.WindowStartTicks >
                    recurringWindowTicks)
            {
                best.WindowStartTicks = now;
                best.MotionHits = 1;
            }
            else
            {
                best.MotionHits = Math.Min(
                    int.MaxValue,
                    best.MotionHits + 1);
            }
        }
    }

    private bool UpdateTrackedRegionReveal(
        long now,
        double elapsedMilliseconds)
    {
        var changed = false;
        var oneShotHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneOneShotHoldMilliseconds);
        var recurringMinimumSpanTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringMinimumSpanMilliseconds);
        var recurringHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringHoldMilliseconds);
        var closingDurationTicks =
            ToStopwatchTicks(
                _settings.PixelSnakeRegionDurationMilliseconds);

        for (var index =
                 _trackedRegions.Count - 1;
             index >= 0;
             index--)
        {
            var region =
                _trackedRegions[index];

            var recurring =
                region.MotionHits >=
                    _settings.MotionZoneRecurringHits &&
                region.LastMotionTicks -
                    region.WindowStartTicks >=
                    recurringMinimumSpanTicks &&
                now - region.LastMotionTicks <=
                    recurringHoldTicks;
            var oneShot =
                now - region.LastMotionTicks <=
                    oneShotHoldTicks;
            var visible =
                recurring || oneShot;

            if (visible)
            {
                region.ClosingAnimation = false;

                var duration = Math.Max(
                    1,
                    _settings.MotionZoneRevealFadeMilliseconds);
                var step = (float)(
                    elapsedMilliseconds /
                    duration);
                var next = Math.Min(
                    1f,
                    region.Reveal + step);

                if (Math.Abs(
                        next -
                        region.Reveal) >
                    0.0005f)
                {
                    region.Reveal = next;
                    changed = true;
                }

                continue;
            }

            if (!region.ClosingAnimation &&
                region.Reveal > 0.001f)
            {
                region.ClosingAnimation = true;
                region.ClosingAnimationStartTicks = now;
                region.Reveal = 1f;
                changed = true;
            }

            if (region.ClosingAnimation)
            {
                if (now -
                    region.ClosingAnimationStartTicks >=
                    closingDurationTicks)
                {
                    region.ClosingAnimation = false;
                    region.Reveal = 0f;
                    changed = true;
                }

                continue;
            }

            var staleTicks =
                Math.Max(
                    oneShotHoldTicks,
                    recurringHoldTicks) +
                closingDurationTicks +
                ToStopwatchTicks(1000);

            if (region.Reveal <= 0.001f &&
                now - region.LastMotionTicks >
                    staleTicks)
            {
                _trackedRegions.RemoveAt(index);
                changed = true;
            }
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
            var elapsedMilliseconds =
                _lastRenderTicks == 0
                    ? _renderTimer.Interval
                        .TotalMilliseconds
                    : FromStopwatchTicks(
                        now - _lastRenderTicks);
            _lastRenderTicks = now;

            var changed =
                UpdateTrackedRegionReveal(
                    now,
                    elapsedMilliseconds);
            var revealAll =
                _enabled &&
                now < _revealAllUntilTicks;
            var cursorChanged =
                cursor.X != _lastCursorX ||
                cursor.Y != _lastCursorY;
            var animationActive =
                IsStartupSnakeActive(now) ||
                _trackedRegions.Any(
                    region =>
                        region.ClosingAnimation);

            shouldPush =
                changed ||
                _maskDirty ||
                cursorChanged ||
                animationActive ||
                revealAll != _lastRevealAll;

            _maskDirty = false;
            _lastCursorX = cursor.X;
            _lastCursorY = cursor.Y;
            _lastRevealAll = revealAll;
        }

        if (shouldPush)
        {
            PushMask();
        }
    }

    private void PushMask()
    {
        string statusText = string.Empty;
        double statusOpacity = 0.0;

        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            var revealAll =
                !_enabled ||
                now < _revealAllUntilTicks;
            var maximumOpacity =
                (float)_settings
                    .MaximumMaskOpacity;

            if (revealAll)
            {
                Array.Clear(
                    _renderAlpha,
                    0,
                    _renderAlpha.Length);
            }
            else if (IsStartupSnakeActive(now))
            {
                ApplyStartupPixelSnake(
                    now,
                    maximumOpacity,
                    out statusOpacity);
                statusText =
                    _settings.PixelSnakeStartupText;
            }
            else
            {
                Array.Fill(
                    _renderAlpha,
                    maximumOpacity);

                // A closing region starts fully visible. Real separated square
                // blocks then darken it from left to right and top to bottom.
                foreach (var region in _trackedRegions)
                {
                    if (!region.ClosingAnimation)
                    {
                        continue;
                    }

                    SetRectangleAlpha(
                        region.MinimumRow,
                        region.MaximumRow,
                        region.MinimumColumn,
                        region.MaximumColumn,
                        0f);

                    ApplyRegionPixelSnake(
                        region,
                        now,
                        maximumOpacity);
                }

                // Active regions always win over a closing region if they
                // overlap.
                foreach (var region in _trackedRegions)
                {
                    if (region.ClosingAnimation ||
                        region.Reveal <= 0.001f)
                    {
                        continue;
                    }

                    var alpha =
                        maximumOpacity *
                        (1f - region.Reveal);

                    FillRectangleWithMinimumAlpha(
                        region.MinimumRow,
                        region.MaximumRow,
                        region.MinimumColumn,
                        region.MaximumColumn,
                        alpha);
                }

                // Exact 4.2 mouse behavior: one current uniform block and no
                // trail or remembered stroke.
                ApplyCurrentMouseBlockReveal();
            }
        }

        _overlay.SetStatusText(
            statusText,
            statusOpacity);
        _overlay.SetMask(
            _renderAlpha,
            _columns,
            _rows);
    }

    private void ApplyStartupPixelSnake(
        long now,
        float maximumOpacity,
        out double textOpacity)
    {
        var duration = Math.Max(
            300,
            _settings.PixelSnakeStartupDurationMilliseconds);
        var progress = Math.Clamp(
            FromStopwatchTicks(
                now -
                _startupSnakeStartTicks) /
                duration,
            0.0,
            1.0);

        Array.Clear(
            _renderAlpha,
            0,
            _renderAlpha.Length);

        ApplyPixelSnake(
            0,
            _rows - 1,
            0,
            _columns - 1,
            progress,
            maximumOpacity,
            _settings.PixelSnakeBlockCells,
            _settings.PixelSnakeGapCells,
            _settings.PixelSnakeGradientBlocks);

        // The square gaps close only at the very end so the pixel structure is
        // clearly visible during most of the animation.
        if (progress > 0.88)
        {
            var finish = SmoothStep01(
                (progress - 0.88) /
                0.12);

            for (var index = 0;
                 index < _renderAlpha.Length;
                 index++)
            {
                _renderAlpha[index] = Math.Max(
                    _renderAlpha[index],
                    maximumOpacity *
                    (float)finish);
            }
        }

        if (progress < 0.18)
        {
            textOpacity =
                SmoothStep01(
                    progress / 0.18);
        }
        else if (progress < 0.72)
        {
            textOpacity = 1.0;
        }
        else
        {
            textOpacity =
                1.0 -
                SmoothStep01(
                    (progress - 0.72) /
                    0.28);
        }
    }

    private void ApplyRegionPixelSnake(
        TrackedRegion region,
        long now,
        float maximumOpacity)
    {
        var duration = Math.Max(
            150,
            _settings.PixelSnakeRegionDurationMilliseconds);
        var progress = Math.Clamp(
            FromStopwatchTicks(
                now -
                region.ClosingAnimationStartTicks) /
                duration,
            0.0,
            1.0);

        ApplyPixelSnake(
            region.MinimumRow,
            region.MaximumRow,
            region.MinimumColumn,
            region.MaximumColumn,
            progress,
            maximumOpacity,
            _settings.PixelSnakeBlockCells,
            _settings.PixelSnakeGapCells,
            _settings.PixelSnakeGradientBlocks);

        if (progress > 0.84)
        {
            var finish = SmoothStep01(
                (progress - 0.84) /
                0.16);
            var fillAlpha =
                maximumOpacity *
                (float)finish;

            FillRectangleWithMinimumFloorAlpha(
                region.MinimumRow,
                region.MaximumRow,
                region.MinimumColumn,
                region.MaximumColumn,
                fillAlpha);
        }
    }

    private void ApplyPixelSnake(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn,
        double progress,
        float maximumOpacity,
        int blockCells,
        int gapCells,
        int gradientBlocks)
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

        blockCells = Math.Max(
            2,
            blockCells);
        gapCells = Math.Clamp(
            gapCells,
            0,
            blockCells - 1);
        gradientBlocks = Math.Max(
            1,
            gradientBlocks);

        var visibleCells =
            Math.Max(
                1,
                blockCells -
                gapCells);
        var regionWidth =
            maximumColumn -
            minimumColumn +
            1;
        var regionHeight =
            maximumRow -
            minimumRow +
            1;
        var blockColumns = Math.Max(
            1,
            (int)Math.Ceiling(
                regionWidth /
                (double)blockCells));
        var blockRows = Math.Max(
            1,
            (int)Math.Ceiling(
                regionHeight /
                (double)blockCells));
        var totalBlocks =
            blockColumns *
            blockRows;
        var head =
            progress *
            (totalBlocks +
             gradientBlocks);

        for (var blockRow = 0;
             blockRow < blockRows;
             blockRow++)
        {
            for (var blockColumn = 0;
                 blockColumn < blockColumns;
                 blockColumn++)
            {
                // Always left to right. The next row starts again on the left.
                var order =
                    blockRow *
                    blockColumns +
                    blockColumn;
                var distance =
                    head -
                    order;

                if (distance <= 0)
                {
                    continue;
                }

                var strength =
                    distance >= gradientBlocks
                        ? 1.0
                        : SmoothStep01(
                            distance /
                            gradientBlocks);
                var alpha =
                    maximumOpacity *
                    (float)strength;

                var startRow =
                    minimumRow +
                    blockRow *
                    blockCells;
                var startColumn =
                    minimumColumn +
                    blockColumn *
                    blockCells;
                var endRow = Math.Min(
                    maximumRow,
                    startRow +
                    visibleCells -
                    1);
                var endColumn = Math.Min(
                    maximumColumn,
                    startColumn +
                    visibleCells -
                    1);

                SetRectangleAlphaMaximum(
                    startRow,
                    endRow,
                    startColumn,
                    endColumn,
                    alpha);
            }
        }
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
            12,
            _settings.MouseRevealRadiusPixels);
        var halfColumns = Math.Max(
            1,
            (int)Math.Ceiling(
                radiusPixels /
                Math.Max(
                    1.0,
                    cellWidth)));
        var halfRows = Math.Max(
            1,
            (int)Math.Ceiling(
                radiusPixels /
                Math.Max(
                    1.0,
                    cellHeight)));

        FillRectangleWithMinimumAlpha(
            centerRow - halfRows,
            centerRow + halfRows,
            centerColumn - halfColumns,
            centerColumn + halfColumns,
            0f);
    }

    private void FillRectangleWithMinimumAlpha(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn,
        float alpha)
    {
        ClampRectangle(
            ref minimumRow,
            ref maximumRow,
            ref minimumColumn,
            ref maximumColumn);

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
                _renderAlpha[index] = Math.Min(
                    _renderAlpha[index],
                    alpha);
            }
        }
    }

    private void FillRectangleWithMinimumFloorAlpha(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn,
        float alpha)
    {
        ClampRectangle(
            ref minimumRow,
            ref maximumRow,
            ref minimumColumn,
            ref maximumColumn);

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
                _renderAlpha[index] = Math.Max(
                    _renderAlpha[index],
                    alpha);
            }
        }
    }

    private void SetRectangleAlpha(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn,
        float alpha)
    {
        ClampRectangle(
            ref minimumRow,
            ref maximumRow,
            ref minimumColumn,
            ref maximumColumn);

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
                    column] = alpha;
            }
        }
    }

    private void SetRectangleAlphaMaximum(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn,
        float alpha)
    {
        ClampRectangle(
            ref minimumRow,
            ref maximumRow,
            ref minimumColumn,
            ref maximumColumn);

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
                _renderAlpha[index] = Math.Max(
                    _renderAlpha[index],
                    alpha);
            }
        }
    }

    private void ClampRectangle(
        ref int minimumRow,
        ref int maximumRow,
        ref int minimumColumn,
        ref int maximumColumn)
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
    }

    private bool IsStartupSnakeActive(long now)
    {
        if (!_startupSnakeActive)
        {
            return false;
        }

        var duration = Math.Max(
            300,
            _settings.PixelSnakeStartupDurationMilliseconds);

        if (FromStopwatchTicks(
                now -
                _startupSnakeStartTicks) >=
            duration)
        {
            _startupSnakeActive = false;
            _maskDirty = true;
            return false;
        }

        return true;
    }

    private static double SmoothStep01(double value)
    {
        var clamped = Math.Clamp(
            value,
            0.0,
            1.0);
        return clamped *
               clamped *
               (3.0 -
                2.0 *
                clamped);
    }

    private static int FollowMinimumBoundary(
        int current,
        int detected)
    {
        if (detected < current)
        {
            return detected;
        }

        return Math.Min(
            detected,
            current + 1);
    }

    private static int FollowMaximumBoundary(
        int current,
        int detected)
    {
        if (detected > current)
        {
            return detected;
        }

        return Math.Max(
            detected,
            current - 1);
    }

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

    private static double FromStopwatchTicks(
        long ticks) =>
        ticks *
        1000.0 /
        Stopwatch.Frequency;

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
