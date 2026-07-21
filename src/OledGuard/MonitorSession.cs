using System.Diagnostics;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
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
    private readonly bool[] _activeRegion;
    private readonly bool[] _visited;
    private readonly int[] _queue;

    private readonly long[] _lastMotionTicks;
    private readonly long[] _motionWindowStartTicks;
    private readonly byte[] _motionHits;
    private readonly float[] _reveal;
    private readonly float[] _renderAlpha;

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
        _activeRegion =
            new bool[cellCount];
        _visited =
            new bool[cellCount];
        _queue =
            new int[cellCount];

        _lastMotionTicks =
            new long[cellCount];
        _motionWindowStartTicks =
            new long[cellCount];
        _motionHits =
            new byte[cellCount];
        _reveal =
            new float[cellCount];
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
            Interval = TimeSpan.FromMilliseconds(33)
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
                _activeRegion,
                0,
                _activeRegion.Length);
            Array.Clear(
                _visited,
                0,
                _visited.Length);
            Array.Clear(
                _lastMotionTicks,
                0,
                _lastMotionTicks.Length);
            Array.Clear(
                _motionWindowStartTicks,
                0,
                _motionWindowStartTicks.Length);
            Array.Clear(
                _motionHits,
                0,
                _motionHits.Length);
            Array.Clear(
                _reveal,
                0,
                _reveal.Length);

            var now = Stopwatch.GetTimestamp();
            _revealAllUntilTicks = enabled
                ? now + ToStopwatchTicks(1000)
                : now;
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
            BuildCleanActivityRegions();
            UpdateMotionHistory(now);

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

    private void BuildCleanActivityRegions()
    {
        Array.Clear(
            _activeRegion,
            0,
            _activeRegion.Length);
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

            for (var row = minimumRow;
                 row <= maximumRow;
                 row++)
            {
                for (var column = minimumColumn;
                     column <= maximumColumn;
                     column++)
                {
                    _activeRegion[
                        row * _columns +
                        column] = true;
                }
            }
        }
    }

    private void UpdateMotionHistory(long now)
    {
        var windowTicks = ToStopwatchTicks(
            _settings.MotionZoneRecurringWindowMilliseconds);

        for (var index = 0;
             index < _activeRegion.Length;
             index++)
        {
            if (!_activeRegion[index])
            {
                continue;
            }

            var windowStart =
                _motionWindowStartTicks[index];

            if (windowStart == 0 ||
                now - windowStart >
                    windowTicks)
            {
                _motionWindowStartTicks[index] =
                    now;
                _motionHits[index] = 1;
            }
            else if (_motionHits[index] <
                     byte.MaxValue)
            {
                _motionHits[index]++;
            }

            _lastMotionTicks[index] = now;
        }
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

            var changed = UpdateReveal(
                now,
                elapsedMilliseconds);
            var revealAll =
                _enabled &&
                now < _revealAllUntilTicks;
            var cursorChanged =
                cursor.X != _lastCursorX ||
                cursor.Y != _lastCursorY;

            shouldPush =
                changed ||
                _maskDirty ||
                cursorChanged ||
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

    private bool UpdateReveal(
        long now,
        double elapsedMilliseconds)
    {
        if (!_enabled)
        {
            return false;
        }

        var changed = false;
        var oneShotHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneOneShotHoldMilliseconds);
        var recurringWindowTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringWindowMilliseconds);
        var recurringHoldTicks =
            ToStopwatchTicks(
                _settings.MotionZoneRecurringHoldMilliseconds);

        for (var index = 0;
             index < _reveal.Length;
             index++)
        {
            var lastMotion =
                _lastMotionTicks[index];
            var windowStart =
                _motionWindowStartTicks[index];

            if (windowStart > 0 &&
                now - windowStart >
                    recurringWindowTicks)
            {
                _motionWindowStartTicks[index] =
                    0;
                _motionHits[index] = 0;
                windowStart = 0;
            }

            var recurring =
                lastMotion > 0 &&
                windowStart > 0 &&
                _motionHits[index] >=
                    _settings.MotionZoneRecurringHits &&
                now - lastMotion <=
                    recurringHoldTicks;

            var oneShot =
                lastMotion > 0 &&
                now - lastMotion <=
                    oneShotHoldTicks;

            var target =
                recurring || oneShot
                    ? 1f
                    : 0f;
            var current =
                _reveal[index];

            var duration =
                target > current
                    ? _settings
                        .MotionZoneRevealFadeMilliseconds
                    : _settings
                        .MotionZoneReturnFadeMilliseconds;
            var step =
                duration <= 0
                    ? 1f
                    : (float)(
                        elapsedMilliseconds /
                        duration);
            var next =
                target > current
                    ? Math.Min(
                        target,
                        current + step)
                    : Math.Max(
                        target,
                        current - step);

            if (Math.Abs(
                    next - current) >
                0.0005f)
            {
                _reveal[index] = next;
                changed = true;
            }
        }

        return changed;
    }

    private void PushMask()
    {
        lock (_sync)
        {
            var revealAll =
                !_enabled ||
                Stopwatch.GetTimestamp() <
                    _revealAllUntilTicks;

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

                for (var index = 0;
                     index < _renderAlpha.Length;
                     index++)
                {
                    _renderAlpha[index] =
                        maximumOpacity *
                        (1f - _reveal[index]);
                }

                ApplyCurrentMouseReveal();
            }
        }

        _overlay.SetMask(
            _renderAlpha,
            _columns,
            _rows);
    }

    private void ApplyCurrentMouseReveal()
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

        var centerColumn =
            (cursor.X - bounds.Left) *
            _columns /
            (double)Math.Max(
                1,
                bounds.Width);
        var centerRow =
            (cursor.Y - bounds.Top) *
            _rows /
            (double)Math.Max(
                1,
                bounds.Height);
        var cellWidth =
            bounds.Width /
            (double)_columns;
        var cellHeight =
            bounds.Height /
            (double)_rows;
        var radiusPixels = Math.Max(
            12,
            _settings.MouseRevealRadiusPixels);
        var radiusColumns =
            radiusPixels /
            Math.Max(
                1.0,
                cellWidth);
        var radiusRows =
            radiusPixels /
            Math.Max(
                1.0,
                cellHeight);

        var minimumColumn = Math.Clamp(
            (int)Math.Floor(
                centerColumn -
                radiusColumns),
            0,
            _columns - 1);
        var maximumColumn = Math.Clamp(
            (int)Math.Ceiling(
                centerColumn +
                radiusColumns),
            0,
            _columns - 1);
        var minimumRow = Math.Clamp(
            (int)Math.Floor(
                centerRow -
                radiusRows),
            0,
            _rows - 1);
        var maximumRow = Math.Clamp(
            (int)Math.Ceiling(
                centerRow +
                radiusRows),
            0,
            _rows - 1);

        for (var row = minimumRow;
             row <= maximumRow;
             row++)
        {
            var y =
                (row + 0.5 -
                 centerRow) *
                cellHeight;

            for (var column = minimumColumn;
                 column <= maximumColumn;
                 column++)
            {
                var x =
                    (column + 0.5 -
                     centerColumn) *
                    cellWidth;
                var distance = Math.Sqrt(
                    x * x +
                    y * y);

                if (distance <= radiusPixels)
                {
                    _renderAlpha[
                        row * _columns +
                        column] = 0f;
                }
            }
        }
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
