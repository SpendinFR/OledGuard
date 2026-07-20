using System.Diagnostics;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private enum ChangeKind
    {
        None,
        Weak,
        Strong
    }

    private sealed class Cell
    {
        public long ContentUntilTicks;
        public long MicroUntilTicks;
        public long MouseUntilTicks;
        public float Alpha;
        public float TargetAlpha;
        public byte WeakChangeStreak;
    }

    private readonly FormsScreen _screen;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly ScreenSampler _sampler;
    private readonly Cell[] _cells;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int _samplesPerCell;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly int _renderColumns;
    private readonly int _renderRows;
    private readonly byte[] _previous;
    private readonly bool[] _changedCells;
    private readonly bool[] _visitedChanged;
    private readonly bool[] _contentActiveCells;
    private readonly bool[] _microActiveCells;
    private readonly bool[] _mouseActiveCells;
    private readonly float[] _contentDistance;
    private readonly float[] _microDistance;
    private readonly float[] _mouseDistance;
    private readonly float[] _cellAlpha;
    private readonly float[] _renderAlpha;
    private readonly int[] _componentQueue;
    private readonly object _sync = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _hasPrevious;
    private bool _enabled;
    private bool _maskDirty;
    private long _revealAllUntilTicks;
    private long _lastAnimationTicks;

    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;
    private long _lastMouseMoveTicks;
    private bool _mouseStrokeActive;
    private int _mouseStrokeMinRow;
    private int _mouseStrokeMaxRow;
    private int _mouseStrokeMinColumn;
    private int _mouseStrokeMaxColumn;

    private bool _disposed;

    public MonitorSession(FormsScreen screen, AppSettings settings)
    {
        _screen = screen;
        _settings = settings;

        var bounds = screen.Bounds;
        _columns = Math.Max(1, (int)Math.Ceiling(bounds.Width / (double)settings.CellSizePixels));
        _rows = Math.Max(1, (int)Math.Ceiling(bounds.Height / (double)settings.CellSizePixels));
        _samplesPerCell = settings.SamplesPerCell;
        _sampleWidth = _columns * _samplesPerCell;
        _sampleHeight = _rows * _samplesPerCell;
        _sampleStride = _sampleWidth * 4;

        // The activity grid remains tiny. A 2x render map keeps transitions smooth
        // while the activity geometry itself stays rectangular and stable.
        _renderColumns = _columns * 2;
        _renderRows = _rows * 2;

        var cellCount = checked(_columns * _rows);
        _previous = new byte[checked(_sampleStride * _sampleHeight)];
        _cells = Enumerable.Range(0, cellCount).Select(_ => new Cell()).ToArray();
        _changedCells = new bool[cellCount];
        _visitedChanged = new bool[cellCount];
        _contentActiveCells = new bool[cellCount];
        _microActiveCells = new bool[cellCount];
        _mouseActiveCells = new bool[cellCount];
        _contentDistance = new float[cellCount];
        _microDistance = new float[cellCount];
        _mouseDistance = new float[cellCount];
        _cellAlpha = new float[cellCount];
        _renderAlpha = new float[checked(_renderColumns * _renderRows)];
        _componentQueue = new int[cellCount];

        _overlay = new OverlayWindow(screen);
        _sampler = new ScreenSampler(bounds, _sampleWidth, _sampleHeight);

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _animationTimer.Tick += OnAnimationTick;
    }

    public bool ExcludedFromCapture => _overlay.ExcludedFromCapture;

    public void Start(bool enabled)
    {
        _overlay.EnsureVisible();
        SetEnabled(enabled);
        _captureLoop = Task.Run(CaptureLoopAsync);
        _animationTimer.Start();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            var now = Stopwatch.GetTimestamp();
            var initialUntil = now + ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);
            _revealAllUntilTicks = enabled ? initialUntil : now;

            foreach (var cell in _cells)
            {
                cell.ContentUntilTicks = now;
                cell.MicroUntilTicks = now;
                cell.MouseUntilTicks = now;
                cell.WeakChangeStreak = 0;
                cell.TargetAlpha = 0;
                cell.Alpha = 0;
            }

            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            _lastMouseMoveTicks = 0;
            _mouseStrokeActive = false;
            _maskDirty = true;
        }

        PushMask();
    }

    public void RevealAll(TimeSpan duration)
    {
        lock (_sync)
        {
            _revealAllUntilTicks = Stopwatch.GetTimestamp() + ToStopwatchTicks(duration.TotalMilliseconds);
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
                var delay = _settings.VisibleSamplingMilliseconds;

                lock (_sync)
                {
                    shouldCapture = _enabled;
                    if (_cells.Any(cell => cell.Alpha > 0.03f || cell.TargetAlpha > 0.03f))
                    {
                        delay = _settings.MaskedSamplingMilliseconds;
                    }
                }

                if (shouldCapture)
                {
                    AnalyzeCapture(_sampler.Capture());
                }

                await Task.Delay(delay, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(2000, _cancellation.Token).ConfigureAwait(false);
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
                Buffer.BlockCopy(current, 0, _previous, 0, current.Length);
                _hasPrevious = true;
                return;
            }

            Array.Clear(_changedCells, 0, _changedCells.Length);

            for (var row = 0; row < _rows; row++)
            {
                for (var column = 0; column < _columns; column++)
                {
                    var index = row * _columns + column;
                    var cell = _cells[index];
                    var kind = AnalyzeCell(current, row, column);

                    switch (kind)
                    {
                        case ChangeKind.Strong:
                            cell.WeakChangeStreak = 0;
                            _changedCells[index] = true;
                            break;

                        case ChangeKind.Weak:
                            cell.WeakChangeStreak = (byte)Math.Min(byte.MaxValue, cell.WeakChangeStreak + 1);
                            if (cell.WeakChangeStreak >= _settings.WeakChangeConfirmationSamples)
                            {
                                cell.WeakChangeStreak = 0;
                                _changedCells[index] = true;
                            }
                            break;

                        default:
                            cell.WeakChangeStreak = 0;
                            break;
                    }
                }
            }

            if (_changedCells.Any(changed => changed))
            {
                ActivateChangedComponents(now);
                _maskDirty = true;
            }

            Buffer.BlockCopy(current, 0, _previous, 0, current.Length);
        }
    }

    private ChangeKind AnalyzeCell(byte[] current, int cellRow, int cellColumn)
    {
        var startX = cellColumn * _samplesPerCell;
        var startY = cellRow * _samplesPerCell;
        var changedSamples = 0;
        var strongSamples = 0;
        var sampleCount = _samplesPerCell * _samplesPerCell;
        double differenceTotal = 0;
        double maximumDifference = 0;

        for (var y = 0; y < _samplesPerCell; y++)
        {
            var rowOffset = (startY + y) * _sampleStride;
            for (var x = 0; x < _samplesPerCell; x++)
            {
                var offset = rowOffset + (startX + x) * 4;
                var difference = (
                    Math.Abs(current[offset] - _previous[offset]) +
                    Math.Abs(current[offset + 1] - _previous[offset + 1]) +
                    Math.Abs(current[offset + 2] - _previous[offset + 2])) / 3.0;

                differenceTotal += difference;
                maximumDifference = Math.Max(maximumDifference, difference);

                if (difference >= _settings.DifferenceThreshold)
                {
                    changedSamples++;
                }

                if (difference >= _settings.StrongDifferenceThreshold)
                {
                    strongSamples++;
                }
            }
        }

        var meanDifference = differenceTotal / sampleCount;
        var changedFraction = changedSamples / (double)sampleCount;
        var strongFraction = strongSamples / (double)sampleCount;

        if (maximumDifference >= _settings.StrongDifferenceThreshold * 1.8 ||
            meanDifference >= _settings.StrongDifferenceThreshold ||
            strongFraction >= _settings.StrongChangedSampleFraction)
        {
            return ChangeKind.Strong;
        }

        if (meanDifference >= _settings.DifferenceThreshold ||
            changedFraction >= _settings.ChangedSampleFraction)
        {
            return ChangeKind.Weak;
        }

        return ChangeKind.None;
    }

    private void ActivateChangedComponents(long now)
    {
        Array.Clear(_visitedChanged, 0, _visitedChanged.Length);
        var mergeGap = _settings.ContentMergeGapCells;
        var activeUntil = now + ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);

        for (var startIndex = 0; startIndex < _changedCells.Length; startIndex++)
        {
            if (!_changedCells[startIndex] || _visitedChanged[startIndex])
            {
                continue;
            }

            var queueHead = 0;
            var queueTail = 0;
            _componentQueue[queueTail++] = startIndex;
            _visitedChanged[startIndex] = true;

            var startRow = startIndex / _columns;
            var startColumn = startIndex % _columns;
            var minRow = startRow;
            var maxRow = startRow;
            var minColumn = startColumn;
            var maxColumn = startColumn;
            var componentCount = 0;

            while (queueHead < queueTail)
            {
                var index = _componentQueue[queueHead++];
                var row = index / _columns;
                var column = index % _columns;
                componentCount++;

                minRow = Math.Min(minRow, row);
                maxRow = Math.Max(maxRow, row);
                minColumn = Math.Min(minColumn, column);
                maxColumn = Math.Max(maxColumn, column);

                for (var offsetY = -mergeGap; offsetY <= mergeGap; offsetY++)
                {
                    var neighbourRow = row + offsetY;
                    if (neighbourRow < 0 || neighbourRow >= _rows)
                    {
                        continue;
                    }

                    for (var offsetX = -mergeGap; offsetX <= mergeGap; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        var neighbourColumn = column + offsetX;
                        if (neighbourColumn < 0 || neighbourColumn >= _columns)
                        {
                            continue;
                        }

                        var neighbourIndex = neighbourRow * _columns + neighbourColumn;
                        if (_changedCells[neighbourIndex] && !_visitedChanged[neighbourIndex])
                        {
                            _visitedChanged[neighbourIndex] = true;
                            _componentQueue[queueTail++] = neighbourIndex;
                        }
                    }
                }
            }

            var isMicro = componentCount <= _settings.MicroChangeMaxCells &&
                maxRow - minRow <= 1 && maxColumn - minColumn <= 1;

            if (isMicro)
            {
                ActivateRectangle(
                    minRow,
                    maxRow,
                    minColumn,
                    maxColumn,
                    activeUntil,
                    static (cell, until) => cell.MicroUntilTicks = Math.Max(cell.MicroUntilTicks, until));
                continue;
            }

            var padding = _settings.ContentActivationPaddingCells;
            minRow = Math.Max(0, minRow - padding);
            maxRow = Math.Min(_rows - 1, maxRow + padding);
            minColumn = Math.Max(0, minColumn - padding);
            maxColumn = Math.Min(_columns - 1, maxColumn + padding);

            MergeWithActiveContent(ref minRow, ref maxRow, ref minColumn, ref maxColumn, now);

            ActivateRectangle(
                minRow,
                maxRow,
                minColumn,
                maxColumn,
                activeUntil,
                static (cell, until) => cell.ContentUntilTicks = Math.Max(cell.ContentUntilTicks, until));
        }
    }

    private void MergeWithActiveContent(
        ref int minRow,
        ref int maxRow,
        ref int minColumn,
        ref int maxColumn,
        long now)
    {
        var gap = _settings.ContentMergeGapCells;
        var expanded = true;

        while (expanded)
        {
            expanded = false;
            var searchMinRow = Math.Max(0, minRow - gap);
            var searchMaxRow = Math.Min(_rows - 1, maxRow + gap);
            var searchMinColumn = Math.Max(0, minColumn - gap);
            var searchMaxColumn = Math.Min(_columns - 1, maxColumn + gap);

            for (var row = searchMinRow; row <= searchMaxRow; row++)
            {
                for (var column = searchMinColumn; column <= searchMaxColumn; column++)
                {
                    if (_cells[row * _columns + column].ContentUntilTicks <= now)
                    {
                        continue;
                    }

                    var oldMinRow = minRow;
                    var oldMaxRow = maxRow;
                    var oldMinColumn = minColumn;
                    var oldMaxColumn = maxColumn;

                    minRow = Math.Min(minRow, row);
                    maxRow = Math.Max(maxRow, row);
                    minColumn = Math.Min(minColumn, column);
                    maxColumn = Math.Max(maxColumn, column);

                    if (minRow != oldMinRow || maxRow != oldMaxRow ||
                        minColumn != oldMinColumn || maxColumn != oldMaxColumn)
                    {
                        expanded = true;
                    }
                }
            }
        }
    }

    private void ActivateRectangle(
        int minRow,
        int maxRow,
        int minColumn,
        int maxColumn,
        long activeUntil,
        Action<Cell, long> activate)
    {
        for (var row = minRow; row <= maxRow; row++)
        {
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var cell = _cells[row * _columns + column];
                activate(cell, activeUntil);
                cell.WeakChangeStreak = 0;
            }
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = _lastAnimationTicks == 0
            ? _animationTimer.Interval.TotalMilliseconds
            : FromStopwatchTicks(now - _lastAnimationTicks);
        _lastAnimationTicks = now;

        NativeMethods.GetCursorPos(out var cursor);
        var changed = false;
        var anyAnimating = false;
        var bounds = _screen.Bounds;

        lock (_sync)
        {
            if (!_enabled)
            {
                _animationTimer.Interval = TimeSpan.FromMilliseconds(250);
                return;
            }

            ApplyMouseActivity(cursor, bounds, now);
            BuildSpatialTargets(now);

            changed = _maskDirty;
            _maskDirty = false;

            foreach (var cell in _cells)
            {
                var oldAlpha = cell.Alpha;
                var duration = cell.TargetAlpha < cell.Alpha
                    ? _settings.RevealFadeMilliseconds
                    : _settings.DarkenFadeMilliseconds;
                var blend = CalculateBlendFactor(elapsedMs, duration);
                cell.Alpha = Lerp(cell.Alpha, cell.TargetAlpha, blend);

                if (Math.Abs(cell.TargetAlpha - cell.Alpha) < 0.001f)
                {
                    cell.Alpha = cell.TargetAlpha;
                }

                if (Math.Abs(oldAlpha - cell.Alpha) > 0.0005f)
                {
                    changed = true;
                }

                if (Math.Abs(cell.TargetAlpha - cell.Alpha) > 0.001f)
                {
                    anyAnimating = true;
                }
            }
        }

        _animationTimer.Interval = TimeSpan.FromMilliseconds(anyAnimating ? 33 : 50);

        if (changed)
        {
            PushMask();
        }
    }

    private void ApplyMouseActivity(NativeMethods.Point cursor, System.Drawing.Rectangle bounds, long now)
    {
        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            _mouseStrokeActive = false;
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            return;
        }

        var centerColumn = Math.Clamp(
            (cursor.X - bounds.Left) * _columns / Math.Max(1, bounds.Width),
            0,
            _columns - 1);
        var centerRow = Math.Clamp(
            (cursor.Y - bounds.Top) * _rows / Math.Max(1, bounds.Height),
            0,
            _rows - 1);

        var moved = _lastCursorX == int.MinValue ||
            Math.Abs(cursor.X - _lastCursorX) >= 3 ||
            Math.Abs(cursor.Y - _lastCursorY) >= 3;

        var idleBreakTicks = ToStopwatchTicks(_settings.MouseStrokeIdleMilliseconds);

        if (moved)
        {
            if (!_mouseStrokeActive ||
                _lastMouseMoveTicks == 0 ||
                now - _lastMouseMoveTicks > idleBreakTicks)
            {
                _mouseStrokeMinRow = centerRow;
                _mouseStrokeMaxRow = centerRow;
                _mouseStrokeMinColumn = centerColumn;
                _mouseStrokeMaxColumn = centerColumn;
                _mouseStrokeActive = true;
            }
            else
            {
                _mouseStrokeMinRow = Math.Min(_mouseStrokeMinRow, centerRow);
                _mouseStrokeMaxRow = Math.Max(_mouseStrokeMaxRow, centerRow);
                _mouseStrokeMinColumn = Math.Min(_mouseStrokeMinColumn, centerColumn);
                _mouseStrokeMaxColumn = Math.Max(_mouseStrokeMaxColumn, centerColumn);
            }

            _lastCursorX = cursor.X;
            _lastCursorY = cursor.Y;
            _lastMouseMoveTicks = now;

            var paddingCells = (int)Math.Ceiling(
                _settings.MouseRevealRadiusPixels / (double)Math.Max(1, _settings.CellSizePixels));
            var minRow = Math.Max(0, _mouseStrokeMinRow - paddingCells);
            var maxRow = Math.Min(_rows - 1, _mouseStrokeMaxRow + paddingCells);
            var minColumn = Math.Max(0, _mouseStrokeMinColumn - paddingCells);
            var maxColumn = Math.Min(_columns - 1, _mouseStrokeMaxColumn + paddingCells);
            var activeUntil = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);

            // Every cell in the current mouse stroke receives the exact same expiry.
            // The whole rectangular path therefore darkens together instead of
            // disappearing as a time-staggered trail.
            ActivateRectangle(
                minRow,
                maxRow,
                minColumn,
                maxColumn,
                activeUntil,
                static (cell, until) => cell.MouseUntilTicks = Math.Max(cell.MouseUntilTicks, until));

            _maskDirty = true;
        }
        else if (_mouseStrokeActive && now - _lastMouseMoveTicks > idleBreakTicks)
        {
            _mouseStrokeActive = false;
        }

        // Keep only a compact square around a stationary pointer visible. This does
        // not refresh the entire previous path, so the old block still expires as one.
        var hoverPaddingCells = Math.Max(0, (int)Math.Ceiling(
            _settings.MouseHoverRadiusPixels / (double)Math.Max(1, _settings.CellSizePixels)));
        var hoverUntil = now + ToStopwatchTicks(_settings.MouseHoverRefreshMilliseconds);

        ActivateRectangle(
            Math.Max(0, centerRow - hoverPaddingCells),
            Math.Min(_rows - 1, centerRow + hoverPaddingCells),
            Math.Max(0, centerColumn - hoverPaddingCells),
            Math.Min(_columns - 1, centerColumn + hoverPaddingCells),
            hoverUntil,
            static (cell, until) => cell.MouseUntilTicks = Math.Max(cell.MouseUntilTicks, until));
    }

    private void BuildSpatialTargets(long now)
    {
        if (now < _revealAllUntilTicks)
        {
            foreach (var cell in _cells)
            {
                if (Math.Abs(cell.TargetAlpha) > 0.001f)
                {
                    cell.TargetAlpha = 0f;
                    _maskDirty = true;
                }
            }
            return;
        }

        for (var index = 0; index < _cells.Length; index++)
        {
            var cell = _cells[index];
            _contentActiveCells[index] = now < cell.ContentUntilTicks;
            _microActiveCells[index] = now < cell.MicroUntilTicks;
            _mouseActiveCells[index] = now < cell.MouseUntilTicks;
        }

        BuildDistanceField(_contentActiveCells, _contentDistance);
        BuildDistanceField(_microActiveCells, _microDistance);
        BuildDistanceField(_mouseActiveCells, _mouseDistance);

        for (var index = 0; index < _cells.Length; index++)
        {
            var contentAlpha = AlphaFromDistance(
                _contentDistance[index],
                _settings.ContentFeatherRadiusPixels);
            var microAlpha = AlphaFromDistance(
                _microDistance[index],
                _settings.MicroFeatherRadiusPixels);
            var mouseAlpha = AlphaFromDistance(
                _mouseDistance[index],
                _settings.MouseFeatherRadiusPixels);
            var target = Math.Min(contentAlpha, Math.Min(microAlpha, mouseAlpha));

            if (Math.Abs(_cells[index].TargetAlpha - target) > 0.001f)
            {
                _cells[index].TargetAlpha = target;
                _maskDirty = true;
            }
        }
    }

    private void BuildDistanceField(bool[] source, float[] distance)
    {
        const float infinity = 1_000_000f;

        // A diagonal cost close to one creates a soft square/squircle rather than
        // the circular halo produced by an Euclidean distance field.
        const float diagonal = 1.08f;

        for (var index = 0; index < distance.Length; index++)
        {
            distance[index] = source[index] ? 0f : infinity;
        }

        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                var value = distance[index];

                if (column > 0)
                {
                    value = Math.Min(value, distance[index - 1] + 1f);
                }
                if (row > 0)
                {
                    value = Math.Min(value, distance[index - _columns] + 1f);
                    if (column > 0)
                    {
                        value = Math.Min(value, distance[index - _columns - 1] + diagonal);
                    }
                    if (column + 1 < _columns)
                    {
                        value = Math.Min(value, distance[index - _columns + 1] + diagonal);
                    }
                }

                distance[index] = value;
            }
        }

        for (var row = _rows - 1; row >= 0; row--)
        {
            for (var column = _columns - 1; column >= 0; column--)
            {
                var index = row * _columns + column;
                var value = distance[index];

                if (column + 1 < _columns)
                {
                    value = Math.Min(value, distance[index + 1] + 1f);
                }
                if (row + 1 < _rows)
                {
                    value = Math.Min(value, distance[index + _columns] + 1f);
                    if (column > 0)
                    {
                        value = Math.Min(value, distance[index + _columns - 1] + diagonal);
                    }
                    if (column + 1 < _columns)
                    {
                        value = Math.Min(value, distance[index + _columns + 1] + diagonal);
                    }
                }

                distance[index] = value;
            }
        }
    }

    private float AlphaFromDistance(float distanceCells, int featherPixels)
    {
        if (distanceCells >= 999_999f)
        {
            return 1f;
        }

        if (distanceCells <= 0f)
        {
            return 0f;
        }

        var distancePixels = distanceCells * Math.Max(1, _settings.CellSizePixels);
        if (distancePixels >= featherPixels)
        {
            return 1f;
        }

        return SmoothStep(distancePixels / Math.Max(1f, featherPixels));
    }

    private void PushMask()
    {
        lock (_sync)
        {
            for (var index = 0; index < _cells.Length; index++)
            {
                _cellAlpha[index] = _enabled ? _cells[index].Alpha : 0f;
            }

            for (var renderRow = 0; renderRow < _renderRows; renderRow++)
            {
                var gridY = (renderRow + 0.5f) * _rows / _renderRows - 0.5f;
                var y0 = Math.Clamp((int)MathF.Floor(gridY), 0, _rows - 1);
                var y1 = Math.Min(_rows - 1, y0 + 1);
                var ty = Math.Clamp(gridY - y0, 0f, 1f);

                for (var renderColumn = 0; renderColumn < _renderColumns; renderColumn++)
                {
                    var gridX = (renderColumn + 0.5f) * _columns / _renderColumns - 0.5f;
                    var x0 = Math.Clamp((int)MathF.Floor(gridX), 0, _columns - 1);
                    var x1 = Math.Min(_columns - 1, x0 + 1);
                    var tx = Math.Clamp(gridX - x0, 0f, 1f);

                    var top = Lerp(_cellAlpha[y0 * _columns + x0], _cellAlpha[y0 * _columns + x1], tx);
                    var bottom = Lerp(_cellAlpha[y1 * _columns + x0], _cellAlpha[y1 * _columns + x1], tx);
                    var value = Lerp(top, bottom, ty);

                    if (value <= 0.003f)
                    {
                        value = 0f;
                    }
                    else if (value >= 0.997f)
                    {
                        value = 1f;
                    }

                    _renderAlpha[renderRow * _renderColumns + renderColumn] = value;
                }
            }
        }

        _overlay.SetMask(_renderAlpha, _renderColumns, _renderRows);
    }

    private static float CalculateBlendFactor(double elapsedMilliseconds, int durationMilliseconds)
    {
        if (durationMilliseconds <= 0)
        {
            return 1f;
        }

        // Reach 99% of the target over the configured duration. Because every
        // pixel is blended proportionally, the whole rectangle keeps its shape
        // while fading instead of eroding from the edges inward.
        var fraction = Math.Max(0.0, elapsedMilliseconds / durationMilliseconds);
        return 1f - MathF.Pow(0.01f, (float)fraction);
    }

    private static float SmoothStep(float value)
    {
        var t = Math.Clamp(value, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float start, float end, float amount) =>
        start + (end - start) * amount;

    private static long ToStopwatchTicks(double milliseconds) =>
        (long)(milliseconds * Stopwatch.Frequency / 1000.0);

    private static double FromStopwatchTicks(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _animationTimer.Stop();
        _sampler.Dispose();
        _overlay.Close();
        _cancellation.Dispose();
    }
}
