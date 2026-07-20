using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private const int InfiniteDistance = 1_000_000;
    private const int RestFadeMilliseconds = 650;

    private enum ChangeKind
    {
        None,
        Weak,
        Strong
    }

    private sealed class Cell
    {
        public long ContentActiveUntilTicks;
        public long MouseActiveUntilTicks;
        public float Alpha;
        public float TargetAlpha;
        public byte WeakChangeStreak;
        public bool Resting;
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
    private readonly byte[] _previous;
    private readonly bool[] _changedCells;
    private readonly bool[] _strongChangedCells;
    private readonly bool[] _visitedCells;
    private readonly bool[] _foregroundCells;
    private readonly bool[] _contentActiveCells;
    private readonly bool[] _mouseActiveCells;
    private readonly int[] _contentDistance;
    private readonly int[] _mouseDistance;
    private readonly int[] _componentQueue;
    private readonly float[] _renderAlpha;
    private readonly object _sync = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _hasPrevious;
    private bool _enabled;
    private bool _maskDirty;
    private long _revealAllUntilTicks;
    private long _windowRevealUntilTicks;
    private long _restEpochTicks;
    private long _lastAnimationTicks;
    private bool _disposed;

    private bool _hasForeground;
    private ForegroundWindowInfo _foreground;
    private int _foregroundMinColumn;
    private int _foregroundMaxColumn;
    private int _foregroundMinRow;
    private int _foregroundMaxRow;

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

        var cellCount = checked(_columns * _rows);
        _previous = new byte[checked(_sampleStride * _sampleHeight)];
        _cells = Enumerable.Range(0, cellCount).Select(_ => new Cell()).ToArray();
        _changedCells = new bool[cellCount];
        _strongChangedCells = new bool[cellCount];
        _visitedCells = new bool[cellCount];
        _foregroundCells = new bool[cellCount];
        _contentActiveCells = new bool[cellCount];
        _mouseActiveCells = new bool[cellCount];
        _contentDistance = new int[cellCount];
        _mouseDistance = new int[cellCount];
        _componentQueue = new int[cellCount];
        _renderAlpha = new float[cellCount];

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
            _restEpochTicks = now;
            _revealAllUntilTicks = 0;
            _windowRevealUntilTicks = 0;
            _hasForeground = false;
            _hasPrevious = false;

            foreach (var cell in _cells)
            {
                cell.ContentActiveUntilTicks = 0;
                cell.MouseActiveUntilTicks = 0;
                cell.WeakChangeStreak = 0;
                cell.Resting = false;
                cell.TargetAlpha = 0f;
                cell.Alpha = 0f;
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

            if (ActivateMeaningfulChangeComponents(now))
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

    private bool ActivateMeaningfulChangeComponents(long now)
    {
        Array.Clear(_visitedCells, 0, _visitedCells.Length);
        var anyAccepted = false;

        for (var startIndex = 0; startIndex < _changedCells.Length; startIndex++)
        {
            if (!_changedCells[startIndex] || _visitedCells[startIndex] || !_foregroundCells[startIndex])
            {
                continue;
            }

            var queueRead = 0;
            var queueWrite = 0;
            _componentQueue[queueWrite++] = startIndex;
            _visitedCells[startIndex] = true;

            var count = 0;
            var strongCount = 0;
            var minRow = _rows;
            var maxRow = 0;
            var minColumn = _columns;
            var maxColumn = 0;

            while (queueRead < queueWrite)
            {
                var index = _componentQueue[queueRead++];
                var row = index / _columns;
                var column = index % _columns;

                count++;
                if (_strongChangedCells[index])
                {
                    strongCount++;
                }

                minRow = Math.Min(minRow, row);
                maxRow = Math.Max(maxRow, row);
                minColumn = Math.Min(minColumn, column);
                maxColumn = Math.Max(maxColumn, column);

                for (var neighborRow = Math.Max(0, row - 1); neighborRow <= Math.Min(_rows - 1, row + 1); neighborRow++)
                {
                    for (var neighborColumn = Math.Max(0, column - 1); neighborColumn <= Math.Min(_columns - 1, column + 1); neighborColumn++)
                    {
                        var neighborIndex = neighborRow * _columns + neighborColumn;
                        if (_visitedCells[neighborIndex] || !_changedCells[neighborIndex] || !_foregroundCells[neighborIndex])
                        {
                            continue;
                        }

                        _visitedCells[neighborIndex] = true;
                        _componentQueue[queueWrite++] = neighborIndex;
                    }
                }
            }

            // A single blinking point is deliberately ignored, even if bright. A
            // tiny two-cell component is accepted when at least one cell is strong.
            var accepted = count >= _settings.MinimumActivityComponentCells &&
                (count > _settings.MinimumActivityComponentCells || strongCount > 0 || count >= 3);
            if (!accepted)
            {
                continue;
            }

            ActivateContentRectangle(minRow, maxRow, minColumn, maxColumn, now);
            anyAccepted = true;
        }

        return anyAccepted;
    }

    private void ActivateContentRectangle(int minRow, int maxRow, int minColumn, int maxColumn, long now)
    {
        var padding = _settings.ContentActivationPaddingCells;
        minRow = Math.Max(0, minRow - padding);
        maxRow = Math.Min(_rows - 1, maxRow + padding);
        minColumn = Math.Max(0, minColumn - padding);
        maxColumn = Math.Min(_columns - 1, maxColumn + padding);

        var activeUntil = now + ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);
        for (var row = minRow; row <= maxRow; row++)
        {
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var index = row * _columns + column;
                if (!_foregroundCells[index])
                {
                    continue;
                }

                var cell = _cells[index];
                cell.ContentActiveUntilTicks = Math.Max(cell.ContentActiveUntilTicks, activeUntil);
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
        var restCycleActive = false;
        var bounds = _screen.Bounds;

        lock (_sync)
        {
            if (!_enabled)
            {
                _animationTimer.Interval = TimeSpan.FromMilliseconds(250);
                return;
            }

            UpdateForegroundWindow(bounds, now);
            ApplyMouseActivity(cursor, bounds, now);
            restCycleActive = BuildTargets(now);

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
                    var fadeDuration = cell.Resting
                        ? Math.Min(_settings.DarkenFadeMilliseconds, RestFadeMilliseconds)
                        : _settings.DarkenFadeMilliseconds;
                    var step = (float)(elapsedMs / Math.Max(1, fadeDuration));
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

        _animationTimer.Interval = TimeSpan.FromMilliseconds(anyAnimating || restCycleActive ? 33 : 80);

        if (changed)
        {
            PushMask();
        }
    }

    private void UpdateForegroundWindow(Rectangle screenBounds, long now)
    {
        if (!ForegroundWindowInfo.TryGetForScreen(screenBounds, out var current))
        {
            if (_hasForeground)
            {
                _hasForeground = false;
                _windowRevealUntilTicks = 0;
                ClearContentActivity();
                _maskDirty = true;
            }

            Array.Clear(_foregroundCells, 0, _foregroundCells.Length);
            return;
        }

        var changedWindow = !_hasForeground || current.Handle != _foreground.Handle;
        var changedBounds = !_hasForeground || RectanglesDiffer(current.Bounds, _foreground.Bounds, 4);

        if (changedWindow || changedBounds)
        {
            ClearContentActivity();
            _windowRevealUntilTicks = now + ToStopwatchTicks(_settings.WindowRevealSeconds * 1000.0);
            _maskDirty = true;
        }

        _foreground = current;
        _hasForeground = true;
        UpdateForegroundCells(screenBounds);
    }

    private void ClearContentActivity()
    {
        foreach (var cell in _cells)
        {
            cell.ContentActiveUntilTicks = 0;
            cell.WeakChangeStreak = 0;
        }
    }

    private void UpdateForegroundCells(Rectangle screenBounds)
    {
        Array.Clear(_foregroundCells, 0, _foregroundCells.Length);
        _foregroundMinColumn = _columns;
        _foregroundMaxColumn = -1;
        _foregroundMinRow = _rows;
        _foregroundMaxRow = -1;

        if (!_hasForeground)
        {
            return;
        }

        for (var row = 0; row < _rows; row++)
        {
            var cellTop = screenBounds.Top + row * screenBounds.Height / _rows;
            var cellBottom = screenBounds.Top + (row + 1) * screenBounds.Height / _rows;

            for (var column = 0; column < _columns; column++)
            {
                var cellLeft = screenBounds.Left + column * screenBounds.Width / _columns;
                var cellRight = screenBounds.Left + (column + 1) * screenBounds.Width / _columns;
                var cellBounds = Rectangle.FromLTRB(cellLeft, cellTop, cellRight, cellBottom);
                if (!cellBounds.IntersectsWith(_foreground.Bounds))
                {
                    continue;
                }

                var index = row * _columns + column;
                _foregroundCells[index] = true;
                _foregroundMinColumn = Math.Min(_foregroundMinColumn, column);
                _foregroundMaxColumn = Math.Max(_foregroundMaxColumn, column);
                _foregroundMinRow = Math.Min(_foregroundMinRow, row);
                _foregroundMaxRow = Math.Max(_foregroundMaxRow, row);
            }
        }
    }

    private void ApplyMouseActivity(NativeMethods.Point cursor, Rectangle bounds, long now)
    {
        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            return;
        }

        var centerColumn = Math.Clamp((cursor.X - bounds.Left) * _columns / Math.Max(1, bounds.Width), 0, _columns - 1);
        var centerRow = Math.Clamp((cursor.Y - bounds.Top) * _rows / Math.Max(1, bounds.Height), 0, _rows - 1);
        var coreCells = Math.Max(0, (int)Math.Ceiling(_settings.MouseCoreRadiusPixels / (double)_settings.CellSizePixels));
        var activeUntil = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);

        for (var row = Math.Max(0, centerRow - coreCells); row <= Math.Min(_rows - 1, centerRow + coreCells); row++)
        {
            for (var column = Math.Max(0, centerColumn - coreCells); column <= Math.Min(_columns - 1, centerColumn + coreCells); column++)
            {
                var cell = _cells[row * _columns + column];
                cell.MouseActiveUntilTicks = Math.Max(cell.MouseActiveUntilTicks, activeUntil);
            }
        }
    }

    private bool BuildTargets(long now)
    {
        var revealEverything = now < _revealAllUntilTicks;
        var revealWindow = _hasForeground && now < _windowRevealUntilTicks;

        for (var index = 0; index < _cells.Length; index++)
        {
            _contentActiveCells[index] = revealEverything ||
                (_foregroundCells[index] && (revealWindow || now < _cells[index].ContentActiveUntilTicks));
            _mouseActiveCells[index] = revealEverything || now < _cells[index].MouseActiveUntilTicks;
        }

        BuildChebyshevDistance(_contentActiveCells, _contentDistance);
        BuildChebyshevDistance(_mouseActiveCells, _mouseDistance);

        var restCycleActive = TryGetRestSweep(now, out var sweepCenter);
        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                float contentAlpha = 1f;

                if (revealEverything)
                {
                    contentAlpha = 0f;
                }
                else if (_foregroundCells[index])
                {
                    contentAlpha = revealWindow
                        ? 0f
                        : AlphaFromDistance(
                            _contentDistance[index],
                            _settings.ContentCoreCells,
                            _settings.ContentFeatherCells);
                }

                var mouseAlpha = AlphaFromDistance(
                    _mouseDistance[index],
                    0,
                    _settings.MouseFeatherCells);

                var baseAlpha = Math.Min(contentAlpha, mouseAlpha);
                var restAlpha = !revealEverything && restCycleActive
                    ? GetRestAlpha(row, column, sweepCenter, baseAlpha)
                    : 0f;
                var target = Math.Max(baseAlpha, restAlpha);
                var resting = restAlpha > baseAlpha + 0.001f;

                var cell = _cells[index];
                cell.Resting = resting;
                if (Math.Abs(cell.TargetAlpha - target) > 0.001f)
                {
                    cell.TargetAlpha = target;
                    _maskDirty = true;
                }
            }
        }

        return restCycleActive;
    }

    private void BuildChebyshevDistance(bool[] active, int[] distance)
    {
        for (var index = 0; index < active.Length; index++)
        {
            distance[index] = active[index] ? 0 : InfiniteDistance;
        }

        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                var value = distance[index];
                if (column > 0)
                {
                    value = Math.Min(value, distance[index - 1] + 1);
                }
                if (row > 0)
                {
                    value = Math.Min(value, distance[index - _columns] + 1);
                    if (column > 0)
                    {
                        value = Math.Min(value, distance[index - _columns - 1] + 1);
                    }
                    if (column + 1 < _columns)
                    {
                        value = Math.Min(value, distance[index - _columns + 1] + 1);
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
                    value = Math.Min(value, distance[index + 1] + 1);
                }
                if (row + 1 < _rows)
                {
                    value = Math.Min(value, distance[index + _columns] + 1);
                    if (column > 0)
                    {
                        value = Math.Min(value, distance[index + _columns - 1] + 1);
                    }
                    if (column + 1 < _columns)
                    {
                        value = Math.Min(value, distance[index + _columns + 1] + 1);
                    }
                }
                distance[index] = value;
            }
        }
    }

    private float AlphaFromDistance(int distance, int coreCells, int featherCells)
    {
        if (distance >= InfiniteDistance / 2)
        {
            return 1f;
        }

        if (distance <= coreCells)
        {
            return 0f;
        }

        if (distance >= coreCells + featherCells)
        {
            return 1f;
        }

        var t = (distance - coreCells) / (float)Math.Max(1, featherCells);
        var smooth = SmoothStep(t);
        var levels = Math.Max(2, _settings.GradientSteps - 1);
        return MathF.Round(smooth * levels) / levels;
    }

    private bool TryGetRestSweep(long now, out float sweepCenter)
    {
        sweepCenter = 0;
        if (!_settings.RestCycleEnabled || !_hasForeground || _foregroundMaxColumn < _foregroundMinColumn)
        {
            return false;
        }

        var elapsedMs = FromStopwatchTicks(now - _restEpochTicks);
        var intervalMs = _settings.RestCycleIntervalSeconds * 1000.0;
        if (elapsedMs < intervalMs)
        {
            return false;
        }

        var phaseMs = elapsedMs % intervalMs;
        if (phaseMs >= _settings.RestCycleDurationMilliseconds)
        {
            return false;
        }

        var progress = (float)(phaseMs / _settings.RestCycleDurationMilliseconds);
        var band = _settings.RestCycleBandCells;
        var start = _foregroundMinColumn - band;
        var end = _foregroundMaxColumn + band;
        sweepCenter = start + (end - start) * progress;
        return true;
    }

    private float GetRestAlpha(int row, int column, float sweepCenter, float baseAlpha)
    {
        var index = row * _columns + column;
        if (!_foregroundCells[index] || baseAlpha >= 0.995f)
        {
            return 0f;
        }

        // A slight diagonal offset gives the monochrome sweep a clean futuristic
        // look without spatial blur or coloured pixels.
        var diagonalColumn = column + (row - _foregroundMinRow) * 0.12f;
        var distance = Math.Abs(diagonalColumn - sweepCenter);
        var band = Math.Max(1, _settings.RestCycleBandCells);
        if (distance >= band)
        {
            return 0f;
        }

        var t = 1f - distance / band;
        var stepped = MathF.Round(SmoothStep(t) * 4f) / 4f;
        return (float)(_settings.RestCycleStrength * stepped);
    }

    private void PushMask()
    {
        lock (_sync)
        {
            for (var index = 0; index < _cells.Length; index++)
            {
                var value = _enabled ? _cells[index].Alpha : 0f;
                _renderAlpha[index] = value <= 0.003f
                    ? 0f
                    : value >= 0.997f
                        ? 1f
                        : value;
            }
        }

        _overlay.SetMask(_renderAlpha, _columns, _rows);
    }

    private static bool RectanglesDiffer(Rectangle left, Rectangle right, int tolerance) =>
        Math.Abs(left.Left - right.Left) > tolerance ||
        Math.Abs(left.Top - right.Top) > tolerance ||
        Math.Abs(left.Right - right.Right) > tolerance ||
        Math.Abs(left.Bottom - right.Bottom) > tolerance;

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
