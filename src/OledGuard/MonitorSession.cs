using System.Diagnostics;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;
using DrawingRectangle = System.Drawing.Rectangle;

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
        public long MouseUntilTicks;
        public float MouseStrength;
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
    private readonly int _zoneSpan;
    private readonly int _zoneColumns;
    private readonly int _zoneRows;
    private readonly long[] _zoneLastChangeTicks;
    private readonly byte[] _previous;
    private readonly bool[] _changedCells;
    private readonly float[] _cellAlpha;
    private readonly float[] _renderAlpha;
    private readonly object _sync = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    private Task? _captureLoop;
    private bool _hasPrevious;
    private bool _enabled;
    private bool _maskDirty;
    private bool _ownWindowForeground;
    private long _revealAllUntilTicks;
    private long _lastAnimationTicks;
    private long _sweepEpochTicks;
    private long _lastMouseStampTicks;
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;
    private IntPtr _activeWindowHandle;
    private DrawingRectangle _activeWindowLocalRect = DrawingRectangle.Empty;
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
        _renderColumns = _columns * 2;
        _renderRows = _rows * 2;

        _zoneSpan = settings.StaticZoneSpanCells;
        _zoneColumns = Math.Max(1, (int)Math.Ceiling(_columns / (double)_zoneSpan));
        _zoneRows = Math.Max(1, (int)Math.Ceiling(_rows / (double)_zoneSpan));

        var cellCount = checked(_columns * _rows);
        _cells = Enumerable.Range(0, cellCount).Select(_ => new Cell()).ToArray();
        _zoneLastChangeTicks = new long[checked(_zoneColumns * _zoneRows)];
        _previous = new byte[checked(_sampleStride * _sampleHeight)];
        _changedCells = new bool[cellCount];
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
            _revealAllUntilTicks = enabled ? now + ToStopwatchTicks(750) : now;
            _sweepEpochTicks = now;
            _hasPrevious = false;
            _activeWindowHandle = IntPtr.Zero;
            _activeWindowLocalRect = DrawingRectangle.Empty;
            _ownWindowForeground = false;

            Array.Fill(_zoneLastChangeTicks, now);
            foreach (var cell in _cells)
            {
                cell.MouseUntilTicks = now;
                cell.MouseStrength = 0f;
                cell.WeakChangeStreak = 0;
                cell.TargetAlpha = 0f;
                cell.Alpha = 0f;
            }

            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            _lastMouseStampTicks = 0;
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
                    shouldCapture = _enabled && !_ownWindowForeground && !_activeWindowLocalRect.IsEmpty;
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
                await Task.Delay(1500, _cancellation.Token).ConfigureAwait(false);
            }
        }
    }

    private void AnalyzeCapture(byte[] current)
    {
        var now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            if (!_enabled || _ownWindowForeground || _activeWindowLocalRect.IsEmpty)
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
                    if (!CellIntersectsActiveWindow(row, column))
                    {
                        cell.WeakChangeStreak = 0;
                        continue;
                    }

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

            var activityFound = false;
            for (var row = 0; row < _rows; row++)
            {
                for (var column = 0; column < _columns; column++)
                {
                    var index = row * _columns + column;
                    if (!_changedCells[index])
                    {
                        continue;
                    }

                    var neighbours = CountChangedNeighbours(row, column);
                    // Ignore isolated blinking carets and tiny spinner dots. Strong
                    // isolated changes are accepted only when they persist in a
                    // neighbouring cell on a later sample.
                    if (neighbours < 2)
                    {
                        continue;
                    }

                    var zoneIndex = GetZoneIndex(row, column);
                    _zoneLastChangeTicks[zoneIndex] = now;
                    activityFound = true;
                }
            }

            if (activityFound)
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
        var sweepAnimating = false;

        lock (_sync)
        {
            if (!_enabled)
            {
                _animationTimer.Interval = TimeSpan.FromMilliseconds(250);
                return;
            }

            UpdateForegroundWindow(now);
            ApplyMouseActivity(cursor, now);
            sweepAnimating = BuildTargets(now);

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

        _animationTimer.Interval = TimeSpan.FromMilliseconds(anyAnimating || sweepAnimating ? 33 : 80);

        if (changed || sweepAnimating)
        {
            PushMask();
        }
    }

    private void UpdateForegroundWindow(long now)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var ownForeground = false;
        var localRect = DrawingRectangle.Empty;
        var trackedHandle = foreground;

        if (foreground != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
            ownForeground = processId == _ownProcessId;

            if (ownForeground)
            {
                localRect = new DrawingRectangle(0, 0, _screen.Bounds.Width, _screen.Bounds.Height);
            }
            else if (TryGetUsefulWindowRect(foreground, out var foregroundRect))
            {
                var combined = foregroundRect;
                var rootOwner = NativeMethods.GetAncestor(foreground, NativeMethods.GaRootOwner);
                if (rootOwner != IntPtr.Zero && rootOwner != foreground &&
                    TryGetUsefulWindowRect(rootOwner, out var ownerRect))
                {
                    combined = Union(combined, ownerRect);
                    trackedHandle = rootOwner;
                }

                localRect = IntersectWithMonitor(combined);
            }
        }

        if (_activeWindowHandle == trackedHandle &&
            _activeWindowLocalRect == localRect &&
            _ownWindowForeground == ownForeground)
        {
            return;
        }

        _activeWindowHandle = trackedHandle;
        _activeWindowLocalRect = localRect;
        _ownWindowForeground = ownForeground;
        _hasPrevious = false;

        if (!localRect.IsEmpty)
        {
            ResetZonesInRectangle(localRect, now);
        }

        _maskDirty = true;
    }

    private bool TryGetUsefulWindowRect(IntPtr hwnd, out NativeMethods.Rect rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        var className = NativeMethods.GetWindowClassName(hwnd);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        try
        {
            if (NativeMethods.DwmGetWindowAttribute(
                    hwnd,
                    NativeMethods.DwmwaCloaked,
                    out int cloaked,
                    sizeof(int)) == 0 && cloaked != 0)
            {
                return false;
            }

            if (NativeMethods.DwmGetWindowAttribute(
                    hwnd,
                    NativeMethods.DwmwaExtendedFrameBounds,
                    out rect,
                    System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Rect>()) == 0 &&
                !rect.IsEmpty)
            {
                return true;
            }
        }
        catch
        {
            // DWM can be unavailable in unusual remote or compatibility sessions.
        }

        return NativeMethods.GetWindowRect(hwnd, out rect) && !rect.IsEmpty;
    }

    private DrawingRectangle IntersectWithMonitor(NativeMethods.Rect windowRect)
    {
        var monitor = _screen.Bounds;
        var window = DrawingRectangle.FromLTRB(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right,
            windowRect.Bottom);
        var intersection = DrawingRectangle.Intersect(monitor, window);
        if (intersection.IsEmpty)
        {
            return DrawingRectangle.Empty;
        }

        return new DrawingRectangle(
            intersection.Left - monitor.Left,
            intersection.Top - monitor.Top,
            intersection.Width,
            intersection.Height);
    }

    private void ResetZonesInRectangle(DrawingRectangle rectangle, long now)
    {
        for (var zoneRow = 0; zoneRow < _zoneRows; zoneRow++)
        {
            for (var zoneColumn = 0; zoneColumn < _zoneColumns; zoneColumn++)
            {
                var zoneRect = GetZoneRectangle(zoneRow, zoneColumn);
                if (zoneRect.IntersectsWith(rectangle))
                {
                    _zoneLastChangeTicks[zoneRow * _zoneColumns + zoneColumn] = now;
                }
            }
        }
    }

    private void ApplyMouseActivity(NativeMethods.Point cursor, long now)
    {
        var bounds = _screen.Bounds;
        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            return;
        }

        var moved = _lastCursorX == int.MinValue ||
            DistanceSquared(cursor.X, cursor.Y, _lastCursorX, _lastCursorY) >=
            _settings.MouseStampDistancePixels * _settings.MouseStampDistancePixels;
        var refreshDue = _lastMouseStampTicks == 0 ||
            now - _lastMouseStampTicks >= ToStopwatchTicks(250);

        if (!moved && !refreshDue)
        {
            return;
        }

        _lastCursorX = cursor.X;
        _lastCursorY = cursor.Y;
        _lastMouseStampTicks = now;
        StampMouseReveal(cursor.X - bounds.Left, cursor.Y - bounds.Top, now);
        _maskDirty = true;
    }

    private void StampMouseReveal(int localX, int localY, long now)
    {
        var core = _settings.MouseRevealRadiusPixels;
        var feather = _settings.MouseRevealFeatherPixels;
        var total = core + feather;
        var minColumn = Math.Max(0, (localX - total) / Math.Max(1, _settings.CellSizePixels));
        var maxColumn = Math.Min(_columns - 1, (localX + total) / Math.Max(1, _settings.CellSizePixels));
        var minRow = Math.Max(0, (localY - total) / Math.Max(1, _settings.CellSizePixels));
        var maxRow = Math.Min(_rows - 1, (localY + total) / Math.Max(1, _settings.CellSizePixels));
        var until = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);

        for (var row = minRow; row <= maxRow; row++)
        {
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var centerX = (column + 0.5f) * _screen.Bounds.Width / _columns;
                var centerY = (row + 0.5f) * _screen.Bounds.Height / _rows;
                var dx = centerX - localX;
                var dy = centerY - localY;
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance > total)
                {
                    continue;
                }

                var strength = distance <= core || feather <= 0
                    ? 1f
                    : 1f - SmoothStep((distance - core) / feather);
                var cell = _cells[row * _columns + column];
                if (now >= cell.MouseUntilTicks)
                {
                    cell.MouseStrength = 0f;
                }
                cell.MouseStrength = Math.Max(cell.MouseStrength, strength);
                cell.MouseUntilTicks = Math.Max(cell.MouseUntilTicks, until);
            }
        }
    }

    private bool BuildTargets(long now)
    {
        if (now < _revealAllUntilTicks || _ownWindowForeground)
        {
            foreach (var cell in _cells)
            {
                SetTarget(cell, 0f);
            }
            return false;
        }

        var sweepActive = IsSweepActive(now, out var sweepCenter, out var sweepHalfWidth);
        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                var cell = _cells[index];
                var centerX = (column + 0.5f) * _screen.Bounds.Width / _columns;
                var centerY = (row + 0.5f) * _screen.Bounds.Height / _rows;

                var target = ComputeWindowAndStaticAlpha(row, column, centerX, centerY, now);

                if (sweepActive && _activeWindowLocalRect.Contains((int)centerX, (int)centerY))
                {
                    var distance = Math.Abs(centerX - sweepCenter);
                    if (distance < sweepHalfWidth)
                    {
                        var normalized = 1f - (float)(distance / sweepHalfWidth);
                        var sweepAlpha = (float)_settings.RestSweepOpacity * SmoothStep(normalized);
                        target = Math.Max(target, sweepAlpha);
                    }
                }

                if (now < cell.MouseUntilTicks)
                {
                    target *= 1f - cell.MouseStrength;
                }
                else if (cell.MouseStrength > 0f)
                {
                    cell.MouseStrength = 0f;
                }

                SetTarget(cell, Math.Clamp(target, 0f, 1f));
            }
        }

        return sweepActive;
    }

    private float ComputeWindowAndStaticAlpha(
        int row,
        int column,
        float centerX,
        float centerY,
        long now)
    {
        if (_activeWindowLocalRect.IsEmpty)
        {
            return 1f;
        }

        if (_activeWindowLocalRect.Contains((int)centerX, (int)centerY))
        {
            var zoneIndex = GetZoneIndex(row, column);
            var lastChange = _zoneLastChangeTicks[zoneIndex];
            if (lastChange <= 0)
            {
                lastChange = now;
                _zoneLastChangeTicks[zoneIndex] = now;
            }

            var ageSeconds = FromStopwatchTicks(now - lastChange) / 1000.0;
            if (ageSeconds <= _settings.StaticDelaySeconds)
            {
                return 0f;
            }

            var progress = (float)Math.Clamp(
                (ageSeconds - _settings.StaticDelaySeconds) / _settings.StaticFadeSeconds,
                0.0,
                1.0);
            return (float)_settings.MaximumStaticOpacity * SmoothStep(progress);
        }

        var dx = centerX < _activeWindowLocalRect.Left
            ? _activeWindowLocalRect.Left - centerX
            : centerX > _activeWindowLocalRect.Right
                ? centerX - _activeWindowLocalRect.Right
                : 0f;
        var dy = centerY < _activeWindowLocalRect.Top
            ? _activeWindowLocalRect.Top - centerY
            : centerY > _activeWindowLocalRect.Bottom
                ? centerY - _activeWindowLocalRect.Bottom
                : 0f;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        if (_settings.WindowEdgeFeatherPixels <= 0)
        {
            return 1f;
        }

        return SmoothStep(distance / _settings.WindowEdgeFeatherPixels);
    }

    private bool IsSweepActive(long now, out double centerX, out double halfWidth)
    {
        centerX = 0;
        halfWidth = Math.Max(1, _settings.RestSweepWidthPixels / 2.0);
        if (!_settings.RestSweepEnabled || _activeWindowLocalRect.IsEmpty)
        {
            return false;
        }

        var elapsedSeconds = FromStopwatchTicks(now - _sweepEpochTicks) / 1000.0;
        var cycle = elapsedSeconds % _settings.RestSweepIntervalSeconds;
        if (cycle >= _settings.RestSweepDurationSeconds)
        {
            return false;
        }

        var progress = cycle / _settings.RestSweepDurationSeconds;
        centerX = _activeWindowLocalRect.Left - halfWidth +
            progress * (_activeWindowLocalRect.Width + halfWidth * 2.0);
        return true;
    }

    private void SetTarget(Cell cell, float target)
    {
        if (Math.Abs(cell.TargetAlpha - target) > 0.001f)
        {
            cell.TargetAlpha = target;
            _maskDirty = true;
        }
    }

    private int CountChangedNeighbours(int row, int column)
    {
        var count = 0;
        for (var offsetY = -1; offsetY <= 1; offsetY++)
        {
            var neighbourRow = row + offsetY;
            if (neighbourRow < 0 || neighbourRow >= _rows)
            {
                continue;
            }

            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                var neighbourColumn = column + offsetX;
                if (neighbourColumn < 0 || neighbourColumn >= _columns)
                {
                    continue;
                }

                if (_changedCells[neighbourRow * _columns + neighbourColumn])
                {
                    count++;
                }
            }
        }
        return count;
    }

    private bool CellIntersectsActiveWindow(int row, int column)
    {
        var left = column * _screen.Bounds.Width / _columns;
        var top = row * _screen.Bounds.Height / _rows;
        var right = (column + 1) * _screen.Bounds.Width / _columns;
        var bottom = (row + 1) * _screen.Bounds.Height / _rows;
        return DrawingRectangle.FromLTRB(left, top, right, bottom).IntersectsWith(_activeWindowLocalRect);
    }

    private int GetZoneIndex(int row, int column)
    {
        var zoneRow = Math.Min(_zoneRows - 1, row / _zoneSpan);
        var zoneColumn = Math.Min(_zoneColumns - 1, column / _zoneSpan);
        return zoneRow * _zoneColumns + zoneColumn;
    }

    private DrawingRectangle GetZoneRectangle(int zoneRow, int zoneColumn)
    {
        var minCellColumn = zoneColumn * _zoneSpan;
        var maxCellColumn = Math.Min(_columns, minCellColumn + _zoneSpan);
        var minCellRow = zoneRow * _zoneSpan;
        var maxCellRow = Math.Min(_rows, minCellRow + _zoneSpan);
        var left = minCellColumn * _screen.Bounds.Width / _columns;
        var right = maxCellColumn * _screen.Bounds.Width / _columns;
        var top = minCellRow * _screen.Bounds.Height / _rows;
        var bottom = maxCellRow * _screen.Bounds.Height / _rows;
        return DrawingRectangle.FromLTRB(left, top, right, bottom);
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

    private static NativeMethods.Rect Union(NativeMethods.Rect first, NativeMethods.Rect second) => new()
    {
        Left = Math.Min(first.Left, second.Left),
        Top = Math.Min(first.Top, second.Top),
        Right = Math.Max(first.Right, second.Right),
        Bottom = Math.Max(first.Bottom, second.Bottom)
    };

    private static int DistanceSquared(int x1, int y1, int x2, int y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    private static float CalculateBlendFactor(double elapsedMilliseconds, int durationMilliseconds)
    {
        if (durationMilliseconds <= 0)
        {
            return 1f;
        }

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
