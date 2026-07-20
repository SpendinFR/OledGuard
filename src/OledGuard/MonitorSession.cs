using System.Diagnostics;
using System.Windows.Threading;
using DrawingRectangle = System.Drawing.Rectangle;
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
        _previous = new byte[_sampleStride * _sampleHeight];
        _cells = Enumerable.Range(0, _columns * _rows).Select(_ => new Cell()).ToArray();
        _changedCells = new bool[_cells.Length];
        _renderAlpha = new float[_cells.Length];
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
                if (!enabled)
                {
                    cell.Alpha = 0;
                }
            }
        }

        if (!enabled)
        {
            PushMask();
        }
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
                    _changedCells[index] = AnalyzeCell(current, row, column, _cells[index]);
                }
            }

            for (var index = 0; index < _changedCells.Length; index++)
            {
                if (!_changedCells[index])
                {
                    continue;
                }

                var row = index / _columns;
                var column = index % _columns;
                RevealChangedArea(row, column, now);
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

                var difference = (Math.Abs(blue - previousBlue) + Math.Abs(green - previousGreen) + Math.Abs(red - previousRed)) / 3.0;
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
        return meanDifference >= _settings.DifferenceThreshold || changedFraction >= _settings.ChangedSampleFraction;
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
                if (cell.TargetAlpha != 0 || cell.Alpha != 0)
                {
                    _maskDirty = true;
                }
                cell.TargetAlpha = 0;
                cell.Alpha = 0; // A newly changing zone must become visible immediately.
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
        var bounds = _screen.Bounds;
        var revealRadius = _settings.MouseRevealRadiusPixels;
        var revealRadiusSquared = revealRadius * revealRadius;

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            changed = _maskDirty;
            _maskDirty = false;
            var revealEverything = now < _revealAllUntilTicks;
            var staticDelayTicks = ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);

            for (var row = 0; row < _rows; row++)
            {
                var cellTop = bounds.Top + row * bounds.Height / _rows;
                var cellBottom = bounds.Top + (row + 1) * bounds.Height / _rows;

                for (var column = 0; column < _columns; column++)
                {
                    var index = row * _columns + column;
                    var cell = _cells[index];
                    var cellLeft = bounds.Left + column * bounds.Width / _columns;
                    var cellRight = bounds.Left + (column + 1) * bounds.Width / _columns;

                    var distanceX = cursor.X < cellLeft ? cellLeft - cursor.X : cursor.X > cellRight ? cursor.X - cellRight : 0;
                    var distanceY = cursor.Y < cellTop ? cellTop - cursor.Y : cursor.Y > cellBottom ? cursor.Y - cellBottom : 0;
                    var mouseNearby = distanceX * distanceX + distanceY * distanceY <= revealRadiusSquared;

                    if (mouseNearby)
                    {
                        cell.RevealUntilTicks = Math.Max(
                            cell.RevealUntilTicks,
                            now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds));
                    }

                    var revealed = revealEverything || now < cell.RevealUntilTicks;
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

                    if (Math.Abs(oldAlpha - cell.Alpha) > 0.003f)
                    {
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            PushMask();
        }
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
