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
        public long StableSinceTicks;
        public long RevealUntilTicks;
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
    private readonly byte[] _previous;
    private readonly bool[] _changedCells;
    private readonly float[] _rawRenderAlpha;
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

        _previous = new byte[checked(_sampleStride * _sampleHeight)];
        _cells = Enumerable.Range(0, checked(_columns * _rows)).Select(_ => new Cell()).ToArray();
        _changedCells = new bool[_cells.Length];
        _rawRenderAlpha = new float[_cells.Length];
        _renderAlpha = new float[_cells.Length];

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

            foreach (var cell in _cells)
            {
                cell.StableSinceTicks = now;
                cell.RevealUntilTicks = now;
                cell.WeakChangeStreak = 0;
                cell.TargetAlpha = 0;
                if (!enabled)
                {
                    cell.Alpha = 0;
                }
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
            foreach (var cell in _cells)
            {
                cell.TargetAlpha = 0;
            }
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
                    if (_cells.Any(cell => cell.Alpha > 0.05f || cell.TargetAlpha > 0.05f))
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
                foreach (var cell in _cells)
                {
                    cell.StableSinceTicks = now;
                }
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

            for (var index = 0; index < _changedCells.Length; index++)
            {
                if (!_changedCells[index])
                {
                    continue;
                }

                RevealChangedArea(index / _columns, index % _columns, now);
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

        // A single sampled pixel changing is usually a caret, tiny spinner,
        // sub-pixel dithering or compression noise. Requiring at least two changed
        // samples prevents those micro-animations from punching visible stains,
        // while text, scrolling and real UI changes still reveal immediately.
        var minimumChangedSamples = Math.Clamp(
            _settings.MinimumChangedSamplesPerCell,
            1,
            sampleCount);

        if (strongSamples >= minimumChangedSamples &&
            (maximumDifference >= _settings.StrongDifferenceThreshold * 1.8 ||
             meanDifference >= _settings.StrongDifferenceThreshold ||
             strongFraction >= _settings.StrongChangedSampleFraction))
        {
            return ChangeKind.Strong;
        }

        if (changedSamples >= minimumChangedSamples &&
            (meanDifference >= _settings.DifferenceThreshold ||
             changedFraction >= _settings.ChangedSampleFraction))
        {
            return ChangeKind.Weak;
        }

        return ChangeKind.None;
    }

    private void RevealChangedArea(int row, int column, long now)
    {
        var padding = _settings.ContentRevealPaddingCells;
        var revealUntil = now + ToStopwatchTicks(_settings.ContentRevealHoldMilliseconds);

        for (var y = Math.Max(0, row - padding); y <= Math.Min(_rows - 1, row + padding); y++)
        {
            for (var x = Math.Max(0, column - padding); x <= Math.Min(_columns - 1, column + padding); x++)
            {
                var cell = _cells[y * _columns + x];
                cell.StableSinceTicks = now;
                cell.RevealUntilTicks = Math.Max(cell.RevealUntilTicks, revealUntil);
                cell.WeakChangeStreak = 0;
                cell.TargetAlpha = 0;

                // A changing zone must appear at once, without waiting for the fade.
                if (cell.Alpha > 0)
                {
                    cell.Alpha = 0;
                    _maskDirty = true;
                }
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

            ApplyMouseReveal(cursor, bounds, now);

            changed = _maskDirty;
            _maskDirty = false;
            var revealEverything = now < _revealAllUntilTicks;
            var staticDelayTicks = ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);

            foreach (var cell in _cells)
            {
                var revealed = revealEverything || now < cell.RevealUntilTicks;
                var oldTarget = cell.TargetAlpha;

                cell.TargetAlpha = !revealed && now - cell.StableSinceTicks >= staticDelayTicks
                    ? 1f
                    : 0f;

                if (Math.Abs(oldTarget - cell.TargetAlpha) > 0.001f)
                {
                    changed = true;
                }

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

                if (Math.Abs(oldAlpha - cell.Alpha) > 0.002f)
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

    private void ApplyMouseReveal(NativeMethods.Point cursor, System.Drawing.Rectangle bounds, long now)
    {
        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            return;
        }

        var revealRadius = _settings.MouseRevealRadiusPixels;
        var revealRadiusSquared = revealRadius * revealRadius;
        var centerColumn = Math.Clamp((cursor.X - bounds.Left) * _columns / Math.Max(1, bounds.Width), 0, _columns - 1);
        var centerRow = Math.Clamp((cursor.Y - bounds.Top) * _rows / Math.Max(1, bounds.Height), 0, _rows - 1);
        var radiusColumns = (int)Math.Ceiling(revealRadius * _columns / (double)Math.Max(1, bounds.Width)) + 1;
        var radiusRows = (int)Math.Ceiling(revealRadius * _rows / (double)Math.Max(1, bounds.Height)) + 1;
        var revealUntil = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);

        for (var row = Math.Max(0, centerRow - radiusRows); row <= Math.Min(_rows - 1, centerRow + radiusRows); row++)
        {
            var cellTop = bounds.Top + row * bounds.Height / _rows;
            var cellBottom = bounds.Top + (row + 1) * bounds.Height / _rows;

            for (var column = Math.Max(0, centerColumn - radiusColumns); column <= Math.Min(_columns - 1, centerColumn + radiusColumns); column++)
            {
                var cellLeft = bounds.Left + column * bounds.Width / _columns;
                var cellRight = bounds.Left + (column + 1) * bounds.Width / _columns;
                var distanceX = cursor.X < cellLeft ? cellLeft - cursor.X : cursor.X > cellRight ? cursor.X - cellRight : 0;
                var distanceY = cursor.Y < cellTop ? cellTop - cursor.Y : cursor.Y > cellBottom ? cursor.Y - cellBottom : 0;

                if (distanceX * distanceX + distanceY * distanceY > revealRadiusSquared)
                {
                    continue;
                }

                var cell = _cells[row * _columns + column];
                cell.RevealUntilTicks = Math.Max(cell.RevealUntilTicks, revealUntil);
            }
        }
    }

    private void PushMask()
    {
        lock (_sync)
        {
            for (var index = 0; index < _cells.Length; index++)
            {
                _rawRenderAlpha[index] = _enabled ? _cells[index].Alpha : 0;
            }

            // Feather only outward from protected cells. Fully black cells stay
            // fully black, while borders and tiny isolated gaps become smoother.
            for (var row = 0; row < _rows; row++)
            {
                for (var column = 0; column < _columns; column++)
                {
                    var index = row * _columns + column;
                    var source = _rawRenderAlpha[index];
                    float weightedTotal = 0;
                    float weightTotal = 0;

                    for (var offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        var y = row + offsetY;
                        if (y < 0 || y >= _rows)
                        {
                            continue;
                        }

                        for (var offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            var x = column + offsetX;
                            if (x < 0 || x >= _columns)
                            {
                                continue;
                            }

                            var weight = offsetX == 0 && offsetY == 0
                                ? 4f
                                : offsetX == 0 || offsetY == 0 ? 2f : 1f;
                            weightedTotal += _rawRenderAlpha[y * _columns + x] * weight;
                            weightTotal += weight;
                        }
                    }

                    var feather = weightTotal > 0 ? weightedTotal / weightTotal : source;
                    var value = Math.Max(source, feather * 0.72f);
                    _renderAlpha[index] = value >= 0.995f ? 1f : Math.Clamp(value, 0f, 1f);
                }
            }
        }

        _overlay.SetMask(_renderAlpha, _columns, _rows);
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
