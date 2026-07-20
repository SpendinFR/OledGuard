using System.Diagnostics;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private sealed class Cell
    {
        public long StableSinceTicks;
        public long RevealUntilTicks;
        public float Alpha;
        public float TargetAlpha;
        public byte Luminance;
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
    private readonly bool[] _filledChangedCells;
    private readonly bool[] _mouseStrokeCells;
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

    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;
    private long _lastMouseMoveTicks;
    private bool _mouseStrokeActive;
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

        var cellCount = checked(_columns * _rows);
        _previous = new byte[checked(_sampleStride * _sampleHeight)];
        _cells = Enumerable.Range(0, cellCount).Select(_ => new Cell()).ToArray();
        _changedCells = new bool[cellCount];
        _filledChangedCells = new bool[cellCount];
        _mouseStrokeCells = new bool[cellCount];
        _renderAlpha = new float[cellCount];

        _overlay = new OverlayWindow(screen);
        _sampler = new ScreenSampler(bounds, _sampleWidth, _sampleHeight);

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
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
                cell.TargetAlpha = 0;
                cell.Alpha = 0;
            }

            Array.Clear(_mouseStrokeCells, 0, _mouseStrokeCells.Length);
            _mouseStrokeActive = false;
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            _lastMouseMoveTicks = 0;
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
                    _changedCells[index] = AnalyzeCell(current, row, column, _cells[index]);
                }
            }

            FillTinyHoles();

            for (var index = 0; index < _filledChangedCells.Length; index++)
            {
                if (!_filledChangedCells[index])
                {
                    continue;
                }

                RevealChangedCell(index, now);
            }

            Buffer.BlockCopy(current, 0, _previous, 0, current.Length);
        }
    }

    private bool AnalyzeCell(byte[] current, int cellRow, int cellColumn, Cell cell)
    {
        var startX = cellColumn * _samplesPerCell;
        var startY = cellRow * _samplesPerCell;
        var changedSamples = 0;
        var sampleCount = _samplesPerCell * _samplesPerCell;
        double differenceTotal = 0;
        long luminanceTotal = 0;

        for (var y = 0; y < _samplesPerCell; y++)
        {
            var rowOffset = (startY + y) * _sampleStride;
            for (var x = 0; x < _samplesPerCell; x++)
            {
                var offset = rowOffset + (startX + x) * 4;
                var blue = current[offset];
                var green = current[offset + 1];
                var red = current[offset + 2];
                var previousBlue = _previous[offset];
                var previousGreen = _previous[offset + 1];
                var previousRed = _previous[offset + 2];

                var difference =
                    (Math.Abs(blue - previousBlue) +
                     Math.Abs(green - previousGreen) +
                     Math.Abs(red - previousRed)) / 3.0;

                differenceTotal += difference;
                if (difference >= _settings.DifferenceThreshold)
                {
                    changedSamples++;
                }

                luminanceTotal += (red * 54L + green * 183L + blue * 19L) >> 8;
            }
        }

        cell.Luminance = (byte)Math.Clamp(luminanceTotal / sampleCount, 0, 255);
        var meanDifference = differenceTotal / sampleCount;
        var changedFraction = changedSamples / (double)sampleCount;

        return meanDifference >= _settings.DifferenceThreshold ||
               changedFraction >= _settings.ChangedSampleFraction;
    }

    private void FillTinyHoles()
    {
        Array.Copy(_changedCells, _filledChangedCells, _changedCells.Length);

        // Only fill a dark cell when it is surrounded by several changed cells.
        // This removes pinholes inside active text areas without expanding a lone
        // blinking point into a large visible island.
        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                if (_changedCells[index])
                {
                    continue;
                }

                var neighbours = 0;
                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    var neighbourRow = row + offsetY;
                    if (neighbourRow < 0 || neighbourRow >= _rows)
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
                        if (neighbourColumn < 0 || neighbourColumn >= _columns)
                        {
                            continue;
                        }

                        if (_changedCells[neighbourRow * _columns + neighbourColumn])
                        {
                            neighbours++;
                        }
                    }
                }

                if (neighbours >= _settings.HoleFillNeighbourCount)
                {
                    _filledChangedCells[index] = true;
                }
            }
        }
    }

    private void RevealChangedCell(int index, long now)
    {
        var cell = _cells[index];
        cell.StableSinceTicks = now;
        cell.RevealUntilTicks = Math.Max(
            cell.RevealUntilTicks,
            now + ToStopwatchTicks(_settings.ContentRevealHoldMilliseconds));

        if (cell.Alpha > 0.001f || cell.TargetAlpha > 0.001f)
        {
            _maskDirty = true;
        }

        cell.TargetAlpha = 0;
        cell.Alpha = 0; // Content appearing under black must be visible immediately.
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
        var bounds = _screen.Bounds;

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            ApplyMouseActivity(cursor, bounds, now);

            changed = _maskDirty;
            _maskDirty = false;
            var revealEverything = now < _revealAllUntilTicks;
            var staticDelayTicks = ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);

            for (var index = 0; index < _cells.Length; index++)
            {
                var cell = _cells[index];
                var revealed = revealEverything ||
                               _mouseStrokeCells[index] ||
                               now < cell.RevealUntilTicks;

                var oldTarget = cell.TargetAlpha;
                if (revealed || cell.Luminance <= _settings.MinimumLuminanceToMask)
                {
                    cell.TargetAlpha = 0;
                }
                else if (now - cell.StableSinceTicks >= staticDelayTicks)
                {
                    cell.TargetAlpha = 1;
                }
                else
                {
                    cell.TargetAlpha = 0;
                }

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
            }
        }

        if (changed)
        {
            PushMask();
        }
    }

    private void ApplyMouseActivity(NativeMethods.Point cursor, System.Drawing.Rectangle bounds, long now)
    {
        var cursorOnScreen = cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                             cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;

        if (!cursorOnScreen)
        {
            CommitMouseStroke(now);
            _lastCursorX = cursor.X;
            _lastCursorY = cursor.Y;
            return;
        }

        var moved = cursor.X != _lastCursorX || cursor.Y != _lastCursorY;
        if (moved)
        {
            _lastMouseMoveTicks = now;
            _mouseStrokeActive = true;
            MarkMouseSquare(cursor, bounds, now, includeInStroke: true);
            _lastCursorX = cursor.X;
            _lastCursorY = cursor.Y;
            _maskDirty = true;
        }
        else
        {
            // A stationary cursor keeps only its current square neighbourhood open.
            MarkMouseSquare(cursor, bounds, now, includeInStroke: false);
        }

        if (_mouseStrokeActive &&
            FromStopwatchTicks(now - _lastMouseMoveTicks) >= _settings.MouseStrokeIdleMilliseconds)
        {
            CommitMouseStroke(now);
        }
    }

    private void MarkMouseSquare(
        NativeMethods.Point cursor,
        System.Drawing.Rectangle bounds,
        long now,
        bool includeInStroke)
    {
        var radius = _settings.MouseRevealRadiusPixels;
        var minX = cursor.X - radius;
        var maxX = cursor.X + radius;
        var minY = cursor.Y - radius;
        var maxY = cursor.Y + radius;
        var revealUntil = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);

        for (var row = 0; row < _rows; row++)
        {
            var cellTop = bounds.Top + row * bounds.Height / _rows;
            var cellBottom = bounds.Top + (row + 1) * bounds.Height / _rows;
            if (cellBottom < minY || cellTop > maxY)
            {
                continue;
            }

            for (var column = 0; column < _columns; column++)
            {
                var cellLeft = bounds.Left + column * bounds.Width / _columns;
                var cellRight = bounds.Left + (column + 1) * bounds.Width / _columns;
                if (cellRight < minX || cellLeft > maxX)
                {
                    continue;
                }

                var index = row * _columns + column;
                var cell = _cells[index];
                cell.RevealUntilTicks = Math.Max(cell.RevealUntilTicks, revealUntil);

                if (includeInStroke)
                {
                    _mouseStrokeCells[index] = true;
                }

                if (cell.Alpha > 0.001f || cell.TargetAlpha > 0.001f)
                {
                    cell.TargetAlpha = 0;
                }
            }
        }
    }

    private void CommitMouseStroke(long now)
    {
        if (!_mouseStrokeActive)
        {
            return;
        }

        var commonExpiry = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);
        for (var index = 0; index < _mouseStrokeCells.Length; index++)
        {
            if (!_mouseStrokeCells[index])
            {
                continue;
            }

            _cells[index].RevealUntilTicks = commonExpiry;
            _mouseStrokeCells[index] = false;
        }

        _mouseStrokeActive = false;
        _maskDirty = true;
    }

    private void PushMask()
    {
        lock (_sync)
        {
            for (var index = 0; index < _cells.Length; index++)
            {
                _renderAlpha[index] = _enabled ? _cells[index].Alpha : 0;
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
