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
        public long ActiveUntilTicks;
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
    private readonly bool[] _strongChangedCells;
    private readonly bool[] _visitedChanges;
    private readonly int[] _componentQueue;
    private readonly bool[] _activeCells;
    private readonly bool[] _closedActiveCells;
    private readonly float[] _distance;
    private readonly float[] _cellAlpha;
    private readonly float[] _renderAlpha;
    private readonly object _sync = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _hasPrevious;
    private bool _enabled;
    private bool _maskDirty;
    private long _revealAllUntilTicks;
    private long _lastAnimationTicks;
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

        // Render directly from the compact activity grid. Nearest-neighbour scaling
        // keeps the shapes crisp and square; temporal alpha still fades smoothly.
        _renderColumns = _columns;
        _renderRows = _rows;

        var cellCount = checked(_columns * _rows);
        _previous = new byte[checked(_sampleStride * _sampleHeight)];
        _cells = Enumerable.Range(0, cellCount).Select(_ => new Cell()).ToArray();
        _changedCells = new bool[cellCount];
        _strongChangedCells = new bool[cellCount];
        _visitedChanges = new bool[cellCount];
        _componentQueue = new int[cellCount];
        _activeCells = new bool[cellCount];
        _closedActiveCells = new bool[cellCount];
        _distance = new float[cellCount];
        _cellAlpha = new float[cellCount];
        _renderAlpha = new float[checked(_renderColumns * _renderRows)];

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

            foreach (var cell in _cells)
            {
                cell.ActiveUntilTicks = enabled ? initialUntil : now;
                cell.WeakChangeStreak = 0;
                cell.TargetAlpha = 0;
                cell.Alpha = 0;
            }

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
            Array.Clear(_strongChangedCells, 0, _strongChangedCells.Length);

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
                            _strongChangedCells[index] = true;
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

            FilterSmallChangeClusters();

            var anyChanged = false;
            for (var index = 0; index < _changedCells.Length; index++)
            {
                if (!_changedCells[index])
                {
                    continue;
                }

                anyChanged = true;
                ActivateChangedArea(index / _columns, index % _columns, now);
            }

            if (anyChanged)
            {
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

        // A blinking point or a one-sample rendering fluctuation must not open a
        // large visible island. It is ignored before temporal confirmation.
        if (changedSamples < _settings.MinimumChangedSamplesPerCell &&
            strongSamples < _settings.MinimumChangedSamplesPerCell)
        {
            return ChangeKind.None;
        }

        var meanDifference = differenceTotal / sampleCount;
        var changedFraction = changedSamples / (double)sampleCount;
        var strongFraction = strongSamples / (double)sampleCount;

        if (meanDifference >= _settings.StrongDifferenceThreshold ||
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

    private void FilterSmallChangeClusters()
    {
        Array.Clear(_visitedChanges, 0, _visitedChanges.Length);
        var minimumCells = _settings.MinimumActivityClusterCells;

        for (var startIndex = 0; startIndex < _changedCells.Length; startIndex++)
        {
            if (!_changedCells[startIndex] || _visitedChanges[startIndex])
            {
                continue;
            }

            var head = 0;
            var tail = 0;
            var hasStrongCell = false;
            _componentQueue[tail++] = startIndex;
            _visitedChanges[startIndex] = true;

            while (head < tail)
            {
                var index = _componentQueue[head++];
                hasStrongCell |= _strongChangedCells[index];
                var row = index / _columns;
                var column = index % _columns;

                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    var y = row + offsetY;
                    if (y < 0 || y >= _rows)
                    {
                        continue;
                    }

                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        var x = column + offsetX;
                        if (x < 0 || x >= _columns)
                        {
                            continue;
                        }

                        var neighbour = y * _columns + x;
                        if (_changedCells[neighbour] && !_visitedChanges[neighbour])
                        {
                            _visitedChanges[neighbour] = true;
                            _componentQueue[tail++] = neighbour;
                        }
                    }
                }
            }

            // Isolated micro-components are deliberately ignored. A genuinely
            // useful update normally changes neighbouring cells; mouse activity
            // still reveals tiny controls when the user actually interacts.
            var keep = tail >= minimumCells;
            if (!keep && hasStrongCell && minimumCells <= 1)
            {
                keep = true;
            }

            if (!keep)
            {
                for (var i = 0; i < tail; i++)
                {
                    _changedCells[_componentQueue[i]] = false;
                    _strongChangedCells[_componentQueue[i]] = false;
                }
            }
        }
    }

    private void ActivateChangedArea(int row, int column, long now)
    {
        var padding = _settings.ContentActivationPaddingCells;
        var activeUntil = now + ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);

        for (var y = Math.Max(0, row - padding); y <= Math.Min(_rows - 1, row + padding); y++)
        {
            for (var x = Math.Max(0, column - padding); x <= Math.Min(_columns - 1, column + padding); x++)
            {
                var cell = _cells[y * _columns + x];
                cell.ActiveUntilTicks = Math.Max(cell.ActiveUntilTicks, activeUntil);
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

                if (cell.TargetAlpha < cell.Alpha)
                {
                    var step = (float)(elapsedMs / Math.Max(1, _settings.RevealFadeMilliseconds));
                    cell.Alpha = Math.Max(cell.TargetAlpha, cell.Alpha - step);
                }
                else if (cell.TargetAlpha > cell.Alpha)
                {
                    var step = (float)(elapsedMs / Math.Max(1, _settings.DarkenFadeMilliseconds));
                    cell.Alpha = Math.Min(cell.TargetAlpha, cell.Alpha + step);
                }

                if (Math.Abs(oldAlpha - cell.Alpha) > 0.001f)
                {
                    changed = true;
                }

                if (Math.Abs(cell.TargetAlpha - cell.Alpha) > 0.002f)
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
            return;
        }

        var radius = _settings.MouseRevealRadiusPixels;
        var left = cursor.X - radius;
        var right = cursor.X + radius;
        var top = cursor.Y - radius;
        var bottom = cursor.Y + radius;
        var firstColumn = Math.Clamp((left - bounds.Left) * _columns / Math.Max(1, bounds.Width), 0, _columns - 1);
        var lastColumn = Math.Clamp((right - bounds.Left) * _columns / Math.Max(1, bounds.Width), 0, _columns - 1);
        var firstRow = Math.Clamp((top - bounds.Top) * _rows / Math.Max(1, bounds.Height), 0, _rows - 1);
        var lastRow = Math.Clamp((bottom - bounds.Top) * _rows / Math.Max(1, bounds.Height), 0, _rows - 1);
        var activeUntil = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var cell = _cells[row * _columns + column];
                cell.ActiveUntilTicks = Math.Max(cell.ActiveUntilTicks, activeUntil);
            }
        }
    }

    private void BuildSpatialTargets(long now)
    {
        var revealEverything = now < _revealAllUntilTicks;

        for (var index = 0; index < _cells.Length; index++)
        {
            _activeCells[index] = revealEverything || now < _cells[index].ActiveUntilTicks;
        }

        CloseSmallActivityGaps();
        BuildDistanceField();

        var core = _settings.ActivityCoreRadiusPixels;
        var feather = Math.Max(1, _settings.ActivityFeatherRadiusPixels);
        var cellPixels = Math.Max(1, _settings.CellSizePixels);

        for (var index = 0; index < _cells.Length; index++)
        {
            var distancePixels = _distance[index] * cellPixels;
            float target;

            if (distancePixels <= core)
            {
                target = 0f;
            }
            else if (distancePixels >= core + feather)
            {
                target = 1f;
            }
            else
            {
                var t = (distancePixels - core) / feather;
                var smooth = SmoothStep(t);
                var steps = Math.Max(2, _settings.SpatialFadeSteps);
                target = MathF.Round(smooth * steps) / steps;
            }

            if (Math.Abs(_cells[index].TargetAlpha - target) > 0.001f)
            {
                _cells[index].TargetAlpha = target;
                _maskDirty = true;
            }
        }
    }

    private void CloseSmallActivityGaps()
    {
        Array.Copy(_activeCells, _closedActiveCells, _activeCells.Length);

        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                if (_activeCells[index])
                {
                    continue;
                }

                var neighbours = 0;
                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    var y = row + offsetY;
                    if (y < 0 || y >= _rows)
                    {
                        continue;
                    }

                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        var x = column + offsetX;
                        if (x >= 0 && x < _columns && _activeCells[y * _columns + x])
                        {
                            neighbours++;
                        }
                    }
                }

                var horizontalBridge = column > 0 && column + 1 < _columns &&
                    _activeCells[index - 1] && _activeCells[index + 1];
                var verticalBridge = row > 0 && row + 1 < _rows &&
                    _activeCells[index - _columns] && _activeCells[index + _columns];

                if (neighbours >= 4 || horizontalBridge || verticalBridge)
                {
                    _closedActiveCells[index] = true;
                }
            }
        }
    }

    private void BuildDistanceField()
    {
        const float infinity = 1_000_000f;
        const float diagonal = 1f;

        for (var index = 0; index < _distance.Length; index++)
        {
            _distance[index] = _closedActiveCells[index] ? 0f : infinity;
        }

        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                var value = _distance[index];

                if (column > 0)
                {
                    value = Math.Min(value, _distance[index - 1] + 1f);
                }
                if (row > 0)
                {
                    value = Math.Min(value, _distance[index - _columns] + 1f);
                    if (column > 0)
                    {
                        value = Math.Min(value, _distance[index - _columns - 1] + diagonal);
                    }
                    if (column + 1 < _columns)
                    {
                        value = Math.Min(value, _distance[index - _columns + 1] + diagonal);
                    }
                }

                _distance[index] = value;
            }
        }

        for (var row = _rows - 1; row >= 0; row--)
        {
            for (var column = _columns - 1; column >= 0; column--)
            {
                var index = row * _columns + column;
                var value = _distance[index];

                if (column + 1 < _columns)
                {
                    value = Math.Min(value, _distance[index + 1] + 1f);
                }
                if (row + 1 < _rows)
                {
                    value = Math.Min(value, _distance[index + _columns] + 1f);
                    if (column > 0)
                    {
                        value = Math.Min(value, _distance[index + _columns - 1] + diagonal);
                    }
                    if (column + 1 < _columns)
                    {
                        value = Math.Min(value, _distance[index + _columns + 1] + diagonal);
                    }
                }

                _distance[index] = value;
            }
        }
    }

    private void PushMask()
    {
        lock (_sync)
        {
            for (var index = 0; index < _cells.Length; index++)
            {
                var value = _enabled ? _cells[index].Alpha : 0f;
                if (value <= 0.003f)
                {
                    value = 0f;
                }
                else if (value >= 0.997f)
                {
                    value = 1f;
                }

                _cellAlpha[index] = value;
                _renderAlpha[index] = value;
            }
        }

        _overlay.SetMask(_renderAlpha, _renderColumns, _renderRows);
    }

    private static float SmoothStep(float value)
    {
        var t = Math.Clamp(value, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

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
