using System.Diagnostics;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private sealed class Cell
    {
        public long LastMotionTicks;
        public int StableEvidence;
        public byte Luminance;
        public float Alpha;
        public float TargetAlpha;
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
    private readonly byte[] _shortReference;
    private readonly byte[] _mediumReference;
    private readonly byte[] _longReference;
    private readonly byte[] _dimReference;
    private readonly bool[] _hasDimReference;
    private readonly byte[] _dimReferenceChangeStreak;
    private readonly bool[] _wasEverDimmed;
    private readonly bool[] _foregroundWindowMask;
    private readonly bool[] _foregroundTransitionMask;
    private readonly bool[] _rawStatic;
    private readonly bool[] _changedNow;
    private readonly bool[] _maskA;
    private readonly bool[] _maskB;
    private readonly bool[] _finalDimMask;
    private readonly bool[] _previousDimMask;
    private readonly bool[] _visited;
    private readonly int[] _queue;
    private readonly float[] _cellAlpha;
    private readonly int _renderColumns;
    private readonly int _renderRows;
    private readonly float[] _renderAlpha;
    private readonly long[] _mouseUntilTicks;
    private readonly bool[] _mouseActiveCells;
    private readonly float[] _mouseDistance;
    private readonly float[] _mouseRevealStrength;
    private readonly object _sync = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _hasReferences;
    private bool _enabled;
    private bool _maskDirty;
    private long _shortReferenceTicks;
    private long _mediumReferenceTicks;
    private long _longReferenceTicks;
    private long _revealAllUntilTicks;
    private long _lastAnimationTicks;

    private IntPtr _foregroundWindowHandle;
    private System.Drawing.Rectangle _foregroundWindowBounds =
        System.Drawing.Rectangle.Empty;
    private bool _foregroundStateInitialized;
    private int _foregroundTransitionCapturesRemaining;
    private long _foregroundWindowLastMotionTicks;

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
        _columns = Math.Max(1, (int)Math.Ceiling(bounds.Width / (double)settings.DetectionCellSizePixels));
        _rows = Math.Max(1, (int)Math.Ceiling(bounds.Height / (double)settings.DetectionCellSizePixels));
        _samplesPerCell = settings.SamplesPerCell;
        _sampleWidth = _columns * _samplesPerCell;
        _sampleHeight = _rows * _samplesPerCell;
        _sampleStride = checked(_sampleWidth * 4);

        // Static analysis stays exactly as build 10. Rendering and mouse detection
        // use a separate fine grid so 128 px analysis blocks do not make a huge cursor.
        const int renderCellPixels = 16;
        _renderColumns = Math.Max(
            _columns * 2,
            (int)Math.Ceiling(bounds.Width / (double)renderCellPixels));
        _renderRows = Math.Max(
            _rows * 2,
            (int)Math.Ceiling(bounds.Height / (double)renderCellPixels));

        var cellCount = checked(_columns * _rows);
        var sampleBytes = checked(_sampleStride * _sampleHeight);
        _cells = Enumerable.Range(0, cellCount).Select(_ => new Cell()).ToArray();
        _previous = new byte[sampleBytes];
        _shortReference = new byte[sampleBytes];
        _mediumReference = new byte[sampleBytes];
        _longReference = new byte[sampleBytes];
        _dimReference = new byte[sampleBytes];
        _hasDimReference = new bool[cellCount];
        _dimReferenceChangeStreak = new byte[cellCount];
        _wasEverDimmed = new bool[cellCount];
        _foregroundWindowMask = new bool[cellCount];
        _foregroundTransitionMask = new bool[cellCount];
        _rawStatic = new bool[cellCount];
        _changedNow = new bool[cellCount];
        _maskA = new bool[cellCount];
        _maskB = new bool[cellCount];
        _finalDimMask = new bool[cellCount];
        _previousDimMask = new bool[cellCount];
        _visited = new bool[cellCount];
        _queue = new int[cellCount];
        _cellAlpha = new float[cellCount];

        var renderCount = checked(_renderColumns * _renderRows);
        _renderAlpha = new float[renderCount];
        _mouseUntilTicks = new long[renderCount];
        _mouseActiveCells = new bool[renderCount];
        _mouseDistance = new float[renderCount];
        _mouseRevealStrength = new float[renderCount];

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
            _revealAllUntilTicks = enabled
                ? now + ToStopwatchTicks(Math.Min(10_000, _settings.StaticDelaySeconds * 1000.0))
                : now;
            _hasReferences = false;
            _shortReferenceTicks = 0;
            _mediumReferenceTicks = 0;
            _longReferenceTicks = 0;

            Array.Clear(_dimReference, 0, _dimReference.Length);
            Array.Clear(_hasDimReference, 0, _hasDimReference.Length);
            Array.Clear(_dimReferenceChangeStreak, 0, _dimReferenceChangeStreak.Length);
            Array.Clear(_wasEverDimmed, 0, _wasEverDimmed.Length);
            Array.Clear(_foregroundWindowMask, 0, _foregroundWindowMask.Length);
            Array.Clear(_foregroundTransitionMask, 0, _foregroundTransitionMask.Length);
            Array.Clear(_rawStatic, 0, _rawStatic.Length);
            Array.Clear(_changedNow, 0, _changedNow.Length);
            Array.Clear(_maskA, 0, _maskA.Length);
            Array.Clear(_maskB, 0, _maskB.Length);
            Array.Clear(_finalDimMask, 0, _finalDimMask.Length);
            Array.Clear(_previousDimMask, 0, _previousDimMask.Length);
            Array.Clear(_mouseUntilTicks, 0, _mouseUntilTicks.Length);
            Array.Clear(_mouseActiveCells, 0, _mouseActiveCells.Length);
            Array.Clear(_mouseDistance, 0, _mouseDistance.Length);
            Array.Clear(_mouseRevealStrength, 0, _mouseRevealStrength.Length);

            _foregroundWindowHandle = IntPtr.Zero;
            _foregroundWindowBounds = System.Drawing.Rectangle.Empty;
            _foregroundStateInitialized = false;
            _foregroundTransitionCapturesRemaining = 0;
            _foregroundWindowLastMotionTicks = 0;

            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            _lastMouseMoveTicks = 0;
            _mouseStrokeActive = false;

            foreach (var cell in _cells)
            {
                cell.LastMotionTicks = now;
                cell.StableEvidence = 0;
                cell.Luminance = 0;
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
                    if (_cells.Any(static cell => cell.Alpha > 0.02f || cell.TargetAlpha > 0.02f))
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

            UpdateForegroundWindowState(now);

            if (!_hasReferences)
            {
                Buffer.BlockCopy(current, 0, _previous, 0, current.Length);
                Buffer.BlockCopy(current, 0, _shortReference, 0, current.Length);
                Buffer.BlockCopy(current, 0, _mediumReference, 0, current.Length);
                Buffer.BlockCopy(current, 0, _longReference, 0, current.Length);
                _hasReferences = true;
                _shortReferenceTicks = now;
                _mediumReferenceTicks = now;
                _longReferenceTicks = now;
                foreach (var cell in _cells)
                {
                    cell.LastMotionTicks = now;
                    cell.StableEvidence = 0;
                }
                return;
            }

            var staticDelayTicks = ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);
            Array.Clear(_changedNow, 0, _changedNow.Length);

            for (var row = 0; row < _rows; row++)
            {
                for (var column = 0; column < _columns; column++)
                {
                    var index = row * _columns + column;
                    var cell = _cells[index];
                    var immediateChanged = CompareCell(current, _previous, row, column, out var luminance);
                    var shortChanged = CompareCell(current, _shortReference, row, column, out _);
                    var mediumChanged = CompareCell(current, _mediumReference, row, column, out _);
                    var longChanged = CompareCell(current, _longReference, row, column, out _);
                    cell.Luminance = luminance;

                    _changedNow[index] = immediateChanged || shortChanged;

                    if (_finalDimMask[index] && _hasDimReference[index])
                    {
                        var changedFromProtectedImage = CompareCell(
                            current,
                            _dimReference,
                            row,
                            column,
                            out _);

                        if (changedFromProtectedImage)
                        {
                            _dimReferenceChangeStreak[index] = (byte)Math.Min(
                                byte.MaxValue,
                                _dimReferenceChangeStreak[index] + 1);
                        }
                        else
                        {
                            _dimReferenceChangeStreak[index] = 0;
                        }
                    }
                    else
                    {
                        _dimReferenceChangeStreak[index] = 0;
                    }

                    if (_changedNow[index])
                    {
                        cell.LastMotionTicks = now;
                        cell.StableEvidence = 0;
                    }
                    else if (!mediumChanged && !longChanged)
                    {
                        if (cell.StableEvidence < int.MaxValue)
                        {
                            cell.StableEvidence++;
                        }
                    }
                    else
                    {
                        cell.StableEvidence = 0;
                    }

                    _rawStatic[index] =
                        cell.StableEvidence >= _settings.StableConfirmationSamples &&
                        now - cell.LastMotionTicks >= staticDelayTicks;
                }
            }

            UpdateForegroundMotionState(now);
            BuildCleanDimMask(now);
            UpdateDimReferences(current);

            if (_foregroundTransitionCapturesRemaining > 0)
            {
                _foregroundTransitionCapturesRemaining--;
            }

            Buffer.BlockCopy(current, 0, _previous, 0, current.Length);

            if (now - _shortReferenceTicks >= ToStopwatchTicks(_settings.ShortReferenceSeconds * 1000.0))
            {
                Buffer.BlockCopy(current, 0, _shortReference, 0, current.Length);
                _shortReferenceTicks = now;
            }

            if (now - _mediumReferenceTicks >= ToStopwatchTicks(_settings.MediumReferenceSeconds * 1000.0))
            {
                Buffer.BlockCopy(current, 0, _mediumReference, 0, current.Length);
                _mediumReferenceTicks = now;
            }

            if (now - _longReferenceTicks >= ToStopwatchTicks(_settings.LongReferenceSeconds * 1000.0))
            {
                Buffer.BlockCopy(current, 0, _longReference, 0, current.Length);
                _longReferenceTicks = now;
            }

            _maskDirty = true;
        }
    }

    private bool CompareCell(byte[] current, byte[] reference, int cellRow, int cellColumn, out byte luminance)
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
                var difference = (
                    Math.Abs(blue - reference[offset]) +
                    Math.Abs(green - reference[offset + 1]) +
                    Math.Abs(red - reference[offset + 2])) / 3.0;

                differenceTotal += difference;
                if (difference >= _settings.DifferenceThreshold)
                {
                    changedSamples++;
                }

                luminanceTotal += (red * 54L + green * 183L + blue * 19L) >> 8;
            }
        }

        luminance = (byte)Math.Clamp(luminanceTotal / sampleCount, 0, 255);
        var meanDifference = differenceTotal / sampleCount;
        var changedFraction = changedSamples / (double)sampleCount;
        return meanDifference >= _settings.DifferenceThreshold ||
               changedFraction >= _settings.ChangedSampleFraction;
    }

    private void BuildCleanDimMask(long now)
    {
        Array.Copy(_finalDimMask, _previousDimMask, _finalDimMask.Length);

        // Normal build 10 candidates are cleaned exactly as before.
        Array.Copy(_rawStatic, _maskA, _rawStatic.Length);
        var source = _maskA;
        var destination = _maskB;

        for (var pass = 0; pass < _settings.MajorityFilterPasses; pass++)
        {
            ApplyBidirectionalMajority(source, destination);
            (source, destination) = (destination, source);
        }

        Array.Copy(source, _finalDimMask, source.Length);
        RemoveSmallDimIslands(_finalDimMask);
        FillSmallBrightHoles(_finalDimMask);
        SuppressDarkRegions(_finalDimMask);

        var fastReapplyTicks = ToStopwatchTicks(
            _settings.PreviouslyDimmedReapplySeconds * 1000.0);
        var foregroundRecentlyActive =
            IsForegroundWindowRecentlyActive(now, fastReapplyTicks);

        // Fast return is built as a separate exact historical mask. It is never
        // allowed in a moving foreground window or near recent local activity.
        Array.Clear(_maskA, 0, _maskA.Length);

        for (var index = 0; index < _maskA.Length; index++)
        {
            if (_previousDimMask[index] ||
                !_wasEverDimmed[index] ||
                (foregroundRecentlyActive && _foregroundWindowMask[index]) ||
                HasRecentActivityNearby(index, now, fastReapplyTicks))
            {
                continue;
            }

            _maskA[index] = true;
        }

        RemoveSmallDimIslands(_maskA);
        SuppressDarkRegions(_maskA);

        for (var index = 0; index < _finalDimMask.Length; index++)
        {
            if (_maskA[index])
            {
                _finalDimMask[index] = true;
            }
        }

        // Preserve the old mask cell by cell. During a foreground-window
        // transition, differences outside the actual changed window rectangle
        // are ignored and rebased instead of propagating across the screen.
        for (var index = 0; index < _finalDimMask.Length; index++)
        {
            if (!_previousDimMask[index])
            {
                continue;
            }

            if (_foregroundTransitionCapturesRemaining > 0 &&
                !_foregroundTransitionMask[index])
            {
                _finalDimMask[index] = true;
                _dimReferenceChangeStreak[index] = 0;
                continue;
            }

            var protectedImageChanged =
                _hasDimReference[index] &&
                _dimReferenceChangeStreak[index] >= 2;

            _finalDimMask[index] = !protectedImageChanged;
        }

        // Deliberately no majority, hole filling, island removal or luminance
        // suppression here. Those operations are valid for new candidates, but
        // applying them after retention lets one opened window erode or spread
        // an already stable old region.
    }

    private bool HasRecentActivityNearby(
        int index,
        long now,
        long quietTicks)
    {
        var centerRow = index / _columns;
        var centerColumn = index % _columns;
        const int radius = 2;

        for (var row = Math.Max(0, centerRow - radius);
             row <= Math.Min(_rows - 1, centerRow + radius);
             row++)
        {
            for (var column = Math.Max(0, centerColumn - radius);
                 column <= Math.Min(_columns - 1, centerColumn + radius);
                 column++)
            {
                var neighbour = row * _columns + column;
                if (_changedNow[neighbour] ||
                    now - _cells[neighbour].LastMotionTicks < quietTicks)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void UpdateForegroundMotionState(long now)
    {
        for (var index = 0; index < _changedNow.Length; index++)
        {
            if (_foregroundWindowMask[index] && _changedNow[index])
            {
                _foregroundWindowLastMotionTicks = now;
                return;
            }
        }
    }

    private bool IsForegroundWindowRecentlyActive(
        long now,
        long quietTicks)
    {
        if (_foregroundWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        return _foregroundTransitionCapturesRemaining > 0 ||
               (_foregroundWindowLastMotionTicks > 0 &&
                now - _foregroundWindowLastMotionTicks < quietTicks);
    }

    private void UpdateForegroundWindowState(long now)
    {
        var hasForeground = TryGetForegroundWindowBounds(
            out var newHandle,
            out var newBounds);

        Array.Clear(
            _foregroundWindowMask,
            0,
            _foregroundWindowMask.Length);

        if (hasForeground)
        {
            MarkRectangleOnMask(
                newBounds,
                _foregroundWindowMask,
                marginCells: 0);
        }

        if (!_foregroundStateInitialized)
        {
            _foregroundStateInitialized = true;
            _foregroundWindowHandle = newHandle;
            _foregroundWindowBounds = newBounds;
            _foregroundWindowLastMotionTicks = now;
            return;
        }

        var changed =
            newHandle != _foregroundWindowHandle ||
            RectangleChanged(_foregroundWindowBounds, newBounds);

        if (changed)
        {
            BuildForegroundTransitionMask(
                _foregroundWindowHandle,
                _foregroundWindowBounds,
                newHandle,
                newBounds);

            var captureInterval = Math.Max(
                100,
                _settings.MaskedSamplingMilliseconds);
            _foregroundTransitionCapturesRemaining = Math.Max(
                4,
                (int)Math.Ceiling(3000.0 / captureInterval));
            _foregroundWindowLastMotionTicks = now;
        }
        else if (_foregroundTransitionCapturesRemaining <= 0)
        {
            Array.Clear(
                _foregroundTransitionMask,
                0,
                _foregroundTransitionMask.Length);
        }

        _foregroundWindowHandle = newHandle;
        _foregroundWindowBounds = newBounds;
    }

    private void BuildForegroundTransitionMask(
        IntPtr oldHandle,
        System.Drawing.Rectangle oldBounds,
        IntPtr newHandle,
        System.Drawing.Rectangle newBounds)
    {
        Array.Clear(
            _foregroundTransitionMask,
            0,
            _foregroundTransitionMask.Length);

        if (oldBounds.IsEmpty)
        {
            MarkRectangleOnMask(
                newBounds,
                _foregroundTransitionMask,
                marginCells: 0);
            return;
        }

        if (newBounds.IsEmpty)
        {
            MarkRectangleOnMask(
                oldBounds,
                _foregroundTransitionMask,
                marginCells: 0);
            return;
        }

        // Opening or closing a smaller child-like window changes the smaller
        // rectangle, not the whole large background window behind it.
        if (oldHandle != newHandle &&
            ContainsWithTolerance(oldBounds, newBounds, 12))
        {
            MarkRectangleOnMask(
                newBounds,
                _foregroundTransitionMask,
                marginCells: 0);
            return;
        }

        if (oldHandle != newHandle &&
            ContainsWithTolerance(newBounds, oldBounds, 12))
        {
            MarkRectangleOnMask(
                oldBounds,
                _foregroundTransitionMask,
                marginCells: 0);
            return;
        }

        // Real window switches, moves and resizes may affect both old and new
        // positions, so only their union is allowed to refresh.
        MarkRectangleOnMask(
            oldBounds,
            _foregroundTransitionMask,
            marginCells: 0);
        MarkRectangleOnMask(
            newBounds,
            _foregroundTransitionMask,
            marginCells: 0);
    }

    private bool TryGetForegroundWindowBounds(
        out IntPtr window,
        out System.Drawing.Rectangle bounds)
    {
        window = NativeMethods.GetForegroundWindow();
        bounds = System.Drawing.Rectangle.Empty;

        if (window == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.Rect nativeRect;
        var found = false;

        try
        {
            found = NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmaExtendedFrameBounds,
                out nativeRect,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Rect>()) == 0;
        }
        catch
        {
            nativeRect = default;
        }

        if (!found)
        {
            found = NativeMethods.GetWindowRect(window, out nativeRect);
        }

        if (!found ||
            nativeRect.Right <= nativeRect.Left ||
            nativeRect.Bottom <= nativeRect.Top)
        {
            return false;
        }

        var raw = System.Drawing.Rectangle.FromLTRB(
            nativeRect.Left,
            nativeRect.Top,
            nativeRect.Right,
            nativeRect.Bottom);
        bounds = System.Drawing.Rectangle.Intersect(
            raw,
            _screen.Bounds);

        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void MarkRectangleOnMask(
        System.Drawing.Rectangle rectangle,
        bool[] mask,
        int marginCells)
    {
        if (rectangle.IsEmpty)
        {
            return;
        }

        var screen = _screen.Bounds;
        var clipped = System.Drawing.Rectangle.Intersect(
            rectangle,
            screen);

        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return;
        }

        var firstColumn = Math.Clamp(
            (clipped.Left - screen.Left) * _columns /
                Math.Max(1, screen.Width) - marginCells,
            0,
            _columns - 1);
        var lastColumn = Math.Clamp(
            (int)Math.Ceiling(
                (clipped.Right - screen.Left) *
                _columns /
                (double)Math.Max(1, screen.Width)) - 1 + marginCells,
            0,
            _columns - 1);
        var firstRow = Math.Clamp(
            (clipped.Top - screen.Top) * _rows /
                Math.Max(1, screen.Height) - marginCells,
            0,
            _rows - 1);
        var lastRow = Math.Clamp(
            (int)Math.Ceiling(
                (clipped.Bottom - screen.Top) *
                _rows /
                (double)Math.Max(1, screen.Height)) - 1 + marginCells,
            0,
            _rows - 1);

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn;
                 column <= lastColumn;
                 column++)
            {
                mask[row * _columns + column] = true;
            }
        }
    }

    private static bool ContainsWithTolerance(
        System.Drawing.Rectangle outer,
        System.Drawing.Rectangle inner,
        int tolerance)
    {
        return inner.Left >= outer.Left - tolerance &&
               inner.Top >= outer.Top - tolerance &&
               inner.Right <= outer.Right + tolerance &&
               inner.Bottom <= outer.Bottom + tolerance;
    }

    private static bool RectangleChanged(
        System.Drawing.Rectangle first,
        System.Drawing.Rectangle second)
    {
        const int tolerance = 8;

        if (first.IsEmpty != second.IsEmpty)
        {
            return true;
        }

        if (first.IsEmpty)
        {
            return false;
        }

        return Math.Abs(first.Left - second.Left) > tolerance ||
               Math.Abs(first.Top - second.Top) > tolerance ||
               Math.Abs(first.Right - second.Right) > tolerance ||
               Math.Abs(first.Bottom - second.Bottom) > tolerance;
    }
    private void UpdateDimReferences(byte[] current)
    {
        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;

                if (_finalDimMask[index])
                {
                    _wasEverDimmed[index] = true;

                    var rebaseOutsideTransition =
                        _foregroundTransitionCapturesRemaining > 0 &&
                        !_foregroundTransitionMask[index];

                    if (!_hasDimReference[index] ||
                        rebaseOutsideTransition)
                    {
                        CopyCellSamples(current, _dimReference, row, column);
                        _hasDimReference[index] = true;
                        _dimReferenceChangeStreak[index] = 0;
                    }
                    continue;
                }

                var confirmedProtectedChange =
                    _previousDimMask[index] &&
                    _hasDimReference[index] &&
                    _dimReferenceChangeStreak[index] >= 2;

                if (!_previousDimMask[index] || confirmedProtectedChange)
                {
                    _hasDimReference[index] = false;
                    _dimReferenceChangeStreak[index] = 0;
                }
            }
        }
    }

    private void CopyCellSamples(
        byte[] source,
        byte[] destination,
        int cellRow,
        int cellColumn)
    {
        var startX = cellColumn * _samplesPerCell;
        var startY = cellRow * _samplesPerCell;
        var rowBytes = checked(_samplesPerCell * 4);

        for (var y = 0; y < _samplesPerCell; y++)
        {
            var offset = (startY + y) * _sampleStride + startX * 4;
            Buffer.BlockCopy(source, offset, destination, offset, rowBytes);
        }
    }

    private void ApplyBidirectionalMajority(bool[] source, bool[] destination)
    {
        var dimThreshold = _settings.MajorityDimThreshold;
        var brightThreshold = 9 - dimThreshold;

        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var dimCount = 0;
                var sampleCount = 0;

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

                        sampleCount++;
                        if (source[neighbourRow * _columns + neighbourColumn])
                        {
                            dimCount++;
                        }
                    }
                }

                var scaledDimThreshold = Math.Max(1, (int)Math.Ceiling(sampleCount * dimThreshold / 9.0));
                var scaledBrightThreshold = Math.Max(0, (int)Math.Floor(sampleCount * brightThreshold / 9.0));
                var index = row * _columns + column;

                if (dimCount >= scaledDimThreshold)
                {
                    destination[index] = true;
                }
                else if (dimCount <= scaledBrightThreshold)
                {
                    destination[index] = false;
                }
                else
                {
                    destination[index] = source[index];
                }
            }
        }
    }

    private void RemoveSmallDimIslands(bool[] mask)
    {
        if (_settings.MinimumDimRegionCells <= 1)
        {
            return;
        }

        Array.Clear(_visited, 0, _visited.Length);
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || _visited[start])
            {
                continue;
            }

            var count = CollectComponent(mask, start, true, out _);
            if (count < _settings.MinimumDimRegionCells)
            {
                for (var index = 0; index < count; index++)
                {
                    mask[_queue[index]] = false;
                }
            }
        }
    }

    private void FillSmallBrightHoles(bool[] mask)
    {
        if (_settings.MaximumBrightHoleCells <= 0)
        {
            return;
        }

        Array.Clear(_visited, 0, _visited.Length);
        for (var start = 0; start < mask.Length; start++)
        {
            if (mask[start] || _visited[start])
            {
                continue;
            }

            var count = CollectComponent(mask, start, false, out var touchesEdge);
            if (!touchesEdge && count <= _settings.MaximumBrightHoleCells)
            {
                for (var index = 0; index < count; index++)
                {
                    mask[_queue[index]] = true;
                }
            }
        }
    }

    private void SuppressDarkRegions(bool[] mask)
    {
        if (_settings.MinimumLuminanceToDim <= 0)
        {
            return;
        }

        Array.Clear(_visited, 0, _visited.Length);
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || _visited[start])
            {
                continue;
            }

            var count = CollectComponent(mask, start, true, out _);
            long luminanceTotal = 0;
            for (var index = 0; index < count; index++)
            {
                luminanceTotal += _cells[_queue[index]].Luminance;
            }

            var averageLuminance = count == 0 ? 0 : luminanceTotal / count;
            if (averageLuminance <= _settings.MinimumLuminanceToDim)
            {
                for (var index = 0; index < count; index++)
                {
                    mask[_queue[index]] = false;
                }
            }
        }
    }

    private int CollectComponent(bool[] mask, int start, bool value, out bool touchesEdge)
    {
        var head = 0;
        var tail = 0;
        _queue[tail++] = start;
        _visited[start] = true;
        touchesEdge = false;

        while (head < tail)
        {
            var index = _queue[head++];
            var row = index / _columns;
            var column = index % _columns;
            touchesEdge |= row == 0 || column == 0 || row == _rows - 1 || column == _columns - 1;

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

                    var neighbourIndex = neighbourRow * _columns + neighbourColumn;
                    if (!_visited[neighbourIndex] && mask[neighbourIndex] == value)
                    {
                        _visited[neighbourIndex] = true;
                        _queue[tail++] = neighbourIndex;
                    }
                }
            }
        }

        return tail;
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = _lastAnimationTicks == 0
            ? _animationTimer.Interval.TotalMilliseconds
            : FromStopwatchTicks(now - _lastAnimationTicks);
        _lastAnimationTicks = now;

        NativeMethods.GetCursorPos(out var cursor);
        var bounds = _screen.Bounds;
        var changed = false;
        var anyAnimating = false;

        lock (_sync)
        {
            if (!_enabled)
            {
                _animationTimer.Interval = TimeSpan.FromMilliseconds(250);
                return;
            }

            ApplyMouseActivity(cursor, bounds, now);
            if (UpdateMouseRevealStrength(now, elapsedMs, out var mouseAnimating))
            {
                changed = true;
            }
            anyAnimating |= mouseAnimating;

            var revealEverything = now < _revealAllUntilTicks;
            var maximumOpacity = (float)_settings.MaximumMaskOpacity;
            Array.Clear(_visited, 0, _visited.Length);

            // Every connected dim region receives one shared alpha value.
            for (var start = 0; start < _finalDimMask.Length; start++)
            {
                if (!_finalDimMask[start] || _visited[start])
                {
                    continue;
                }

                var count = CollectComponent(_finalDimMask, start, true, out _);
                var regionAlpha = 0f;
                for (var item = 0; item < count; item++)
                {
                    regionAlpha = Math.Max(regionAlpha, _cells[_queue[item]].Alpha);
                }

                var target = revealEverything ? 0f : maximumOpacity;
                var duration = target < regionAlpha
                    ? _settings.RevealFadeMilliseconds
                    : _settings.DarkenFadeMilliseconds;
                var step = (float)(elapsedMs / Math.Max(1, duration));
                var nextAlpha = target < regionAlpha
                    ? Math.Max(target, regionAlpha - step)
                    : Math.Min(target, regionAlpha + step);

                for (var item = 0; item < count; item++)
                {
                    var cell = _cells[_queue[item]];
                    if (Math.Abs(cell.TargetAlpha - target) > 0.001f ||
                        Math.Abs(cell.Alpha - nextAlpha) > 0.0005f)
                    {
                        changed = true;
                    }

                    cell.TargetAlpha = target;
                    cell.Alpha = nextAlpha;
                }

                if (Math.Abs(nextAlpha - target) > 0.001f)
                {
                    anyAnimating = true;
                }
            }

            // Cells outside the current dim mask reveal exactly as in build 10.
            for (var index = 0; index < _cells.Length; index++)
            {
                if (_finalDimMask[index])
                {
                    continue;
                }

                var cell = _cells[index];
                var oldAlpha = cell.Alpha;
                cell.TargetAlpha = 0f;

                if (cell.Alpha > 0f)
                {
                    var step = (float)(elapsedMs / Math.Max(1, _settings.RevealFadeMilliseconds));
                    cell.Alpha = Math.Max(0f, cell.Alpha - step);
                }

                if (Math.Abs(oldAlpha - cell.Alpha) > 0.0005f)
                {
                    changed = true;
                }
                if (cell.Alpha > 0.001f)
                {
                    anyAnimating = true;
                }
            }

            changed |= _maskDirty;
            _maskDirty = false;
        }

        _animationTimer.Interval = TimeSpan.FromMilliseconds(anyAnimating ? 33 : 50);
        if (changed)
        {
            PushMask();
        }
    }

    private void ApplyMouseActivity(
        NativeMethods.Point cursor,
        System.Drawing.Rectangle bounds,
        long now)
    {
        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            _mouseStrokeActive = false;
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            return;
        }

        var centerColumn = Math.Clamp(
            (cursor.X - bounds.Left) * _renderColumns / Math.Max(1, bounds.Width),
            0,
            _renderColumns - 1);
        var centerRow = Math.Clamp(
            (cursor.Y - bounds.Top) * _renderRows / Math.Max(1, bounds.Height),
            0,
            _renderRows - 1);

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

            var renderCellPixels = GetRenderCellPixels(bounds);
            var paddingCells = (int)Math.Ceiling(
                _settings.MouseRevealRadiusPixels / renderCellPixels);
            var activeUntil = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);

            ActivateMouseRectangle(
                Math.Max(0, _mouseStrokeMinRow - paddingCells),
                Math.Min(_renderRows - 1, _mouseStrokeMaxRow + paddingCells),
                Math.Max(0, _mouseStrokeMinColumn - paddingCells),
                Math.Min(_renderColumns - 1, _mouseStrokeMaxColumn + paddingCells),
                activeUntil);

            _maskDirty = true;
        }
        else if (_mouseStrokeActive && now - _lastMouseMoveTicks > idleBreakTicks)
        {
            _mouseStrokeActive = false;
        }

        var hoverPaddingCells = Math.Max(
            0,
            (int)Math.Ceiling(_settings.MouseHoverRadiusPixels / GetRenderCellPixels(bounds)));
        var hoverUntil = now + ToStopwatchTicks(_settings.MouseHoverRefreshMilliseconds);

        ActivateMouseRectangle(
            Math.Max(0, centerRow - hoverPaddingCells),
            Math.Min(_renderRows - 1, centerRow + hoverPaddingCells),
            Math.Max(0, centerColumn - hoverPaddingCells),
            Math.Min(_renderColumns - 1, centerColumn + hoverPaddingCells),
            hoverUntil);
    }

    private void ActivateMouseRectangle(
        int minRow,
        int maxRow,
        int minColumn,
        int maxColumn,
        long activeUntil)
    {
        for (var row = minRow; row <= maxRow; row++)
        {
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var index = row * _renderColumns + column;
                _mouseUntilTicks[index] = Math.Max(_mouseUntilTicks[index], activeUntil);
            }
        }
    }

    private bool UpdateMouseRevealStrength(
        long now,
        double elapsedMs,
        out bool anyAnimating)
    {
        for (var index = 0; index < _mouseActiveCells.Length; index++)
        {
            _mouseActiveCells[index] = now < _mouseUntilTicks[index];
        }

        BuildMouseDistanceField();
        var changed = false;
        anyAnimating = false;
        var bounds = _screen.Bounds;
        var renderCellPixels = GetRenderCellPixels(bounds);

        for (var index = 0; index < _mouseRevealStrength.Length; index++)
        {
            var maskAlpha = MouseMaskAlphaFromDistance(
                _mouseDistance[index],
                renderCellPixels,
                _settings.MouseFeatherRadiusPixels);
            var targetReveal = 1f - maskAlpha;
            var current = _mouseRevealStrength[index];
            var duration = targetReveal > current
                ? _settings.MouseRevealFadeMilliseconds
                : _settings.MouseReturnFadeMilliseconds;
            var blend = CalculateBlendFactor(elapsedMs, duration);
            var next = Lerp(current, targetReveal, blend);

            if (Math.Abs(next - targetReveal) < 0.001f)
            {
                next = targetReveal;
            }
            if (Math.Abs(next - current) > 0.0005f)
            {
                changed = true;
            }
            if (Math.Abs(next - targetReveal) > 0.001f)
            {
                anyAnimating = true;
            }

            _mouseRevealStrength[index] = next;
        }

        return changed;
    }

    private void BuildMouseDistanceField()
    {
        const float infinity = 1_000_000f;
        const float diagonal = 1.08f;

        for (var index = 0; index < _mouseDistance.Length; index++)
        {
            _mouseDistance[index] = _mouseActiveCells[index] ? 0f : infinity;
        }

        for (var row = 0; row < _renderRows; row++)
        {
            for (var column = 0; column < _renderColumns; column++)
            {
                var index = row * _renderColumns + column;
                var value = _mouseDistance[index];

                if (column > 0)
                {
                    value = Math.Min(value, _mouseDistance[index - 1] + 1f);
                }
                if (row > 0)
                {
                    value = Math.Min(value, _mouseDistance[index - _renderColumns] + 1f);
                    if (column > 0)
                    {
                        value = Math.Min(value, _mouseDistance[index - _renderColumns - 1] + diagonal);
                    }
                    if (column + 1 < _renderColumns)
                    {
                        value = Math.Min(value, _mouseDistance[index - _renderColumns + 1] + diagonal);
                    }
                }

                _mouseDistance[index] = value;
            }
        }

        for (var row = _renderRows - 1; row >= 0; row--)
        {
            for (var column = _renderColumns - 1; column >= 0; column--)
            {
                var index = row * _renderColumns + column;
                var value = _mouseDistance[index];

                if (column + 1 < _renderColumns)
                {
                    value = Math.Min(value, _mouseDistance[index + 1] + 1f);
                }
                if (row + 1 < _renderRows)
                {
                    value = Math.Min(value, _mouseDistance[index + _renderColumns] + 1f);
                    if (column > 0)
                    {
                        value = Math.Min(value, _mouseDistance[index + _renderColumns - 1] + diagonal);
                    }
                    if (column + 1 < _renderColumns)
                    {
                        value = Math.Min(value, _mouseDistance[index + _renderColumns + 1] + diagonal);
                    }
                }

                _mouseDistance[index] = value;
            }
        }
    }

    private static float MouseMaskAlphaFromDistance(
        float distanceCells,
        double renderCellPixels,
        int featherPixels)
    {
        if (distanceCells >= 999_999f)
        {
            return 1f;
        }
        if (distanceCells <= 0f)
        {
            return 0f;
        }

        var distancePixels = distanceCells * Math.Max(1.0, renderCellPixels);
        if (distancePixels >= featherPixels)
        {
            return 1f;
        }

        return SmoothStep((float)(distancePixels / Math.Max(1.0, featherPixels)));
    }

    private double GetRenderCellPixels(System.Drawing.Rectangle bounds) =>
        Math.Max(
            1.0,
            Math.Min(
                bounds.Width / (double)_renderColumns,
                bounds.Height / (double)_renderRows));

    private void PushMask()
    {
        lock (_sync)
        {
            for (var index = 0; index < _cells.Length; index++)
            {
                _cellAlpha[index] = _enabled ? _cells[index].Alpha : 0f;
            }

            var now = Stopwatch.GetTimestamp();
            var fastReapplyTicks = ToStopwatchTicks(
                _settings.PreviouslyDimmedReapplySeconds * 1000.0);
            var revealForeground =
                IsForegroundWindowRecentlyActive(
                    now,
                    fastReapplyTicks);
            var foregroundBounds = _foregroundWindowBounds;
            var screenBounds = _screen.Bounds;

            for (var renderRow = 0; renderRow < _renderRows; renderRow++)
            {
                var sourceRow = Math.Clamp(
                    renderRow * _rows / _renderRows,
                    0,
                    _rows - 1);
                var screenY = screenBounds.Top +
                    (renderRow + 0.5) *
                    screenBounds.Height /
                    _renderRows;

                for (var renderColumn = 0;
                     renderColumn < _renderColumns;
                     renderColumn++)
                {
                    var sourceColumn = Math.Clamp(
                        renderColumn * _columns / _renderColumns,
                        0,
                        _columns - 1);

                    var value =
                        _cellAlpha[sourceRow * _columns + sourceColumn];

                    var renderIndex =
                        renderRow * _renderColumns + renderColumn;
                    value *= 1f - _mouseRevealStrength[renderIndex];

                    if (revealForeground &&
                        !foregroundBounds.IsEmpty)
                    {
                        var screenX = screenBounds.Left +
                            (renderColumn + 0.5) *
                            screenBounds.Width /
                            _renderColumns;

                        if (foregroundBounds.Contains(
                            (int)screenX,
                            (int)screenY))
                        {
                            value = 0f;
                        }
                    }

                    if (value <= 0.003f)
                    {
                        value = 0f;
                    }

                    _renderAlpha[renderIndex] = value;
                }
            }
        }

        _overlay.SetMask(
            _renderAlpha,
            _renderColumns,
            _renderRows);
    }
    private static float CalculateBlendFactor(
        double elapsedMilliseconds,
        int durationMilliseconds)
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
