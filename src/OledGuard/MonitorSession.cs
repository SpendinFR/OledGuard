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
    private readonly bool[] _rawStatic;
    private readonly bool[] _maskA;
    private readonly bool[] _maskB;
    private readonly bool[] _finalDimMask;
    private readonly bool[] _visited;
    private readonly int[] _queue;
    private readonly float[] _renderAlpha;
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

        var cellCount = checked(_columns * _rows);
        var sampleBytes = checked(_sampleStride * _sampleHeight);
        _cells = Enumerable.Range(0, cellCount).Select(_ => new Cell()).ToArray();
        _previous = new byte[sampleBytes];
        _shortReference = new byte[sampleBytes];
        _mediumReference = new byte[sampleBytes];
        _longReference = new byte[sampleBytes];
        _rawStatic = new bool[cellCount];
        _maskA = new bool[cellCount];
        _maskB = new bool[cellCount];
        _finalDimMask = new bool[cellCount];
        _visited = new bool[cellCount];
        _queue = new int[cellCount];
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
            _revealAllUntilTicks = enabled
                ? now + ToStopwatchTicks(Math.Min(10_000, _settings.StaticDelaySeconds * 1000.0))
                : now;
            _hasReferences = false;
            _shortReferenceTicks = 0;
            _mediumReferenceTicks = 0;
            _longReferenceTicks = 0;

            Array.Clear(_rawStatic, 0, _rawStatic.Length);
            Array.Clear(_maskA, 0, _maskA.Length);
            Array.Clear(_maskB, 0, _maskB.Length);
            Array.Clear(_finalDimMask, 0, _finalDimMask.Length);

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

                    if (immediateChanged || shortChanged)
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

            BuildCleanDimMask();
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

    private void BuildCleanDimMask()
    {
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
        var changed = false;
        var anyAnimating = false;

        lock (_sync)
        {
            if (!_enabled)
            {
                _animationTimer.Interval = TimeSpan.FromMilliseconds(250);
                return;
            }

            var revealEverything = now < _revealAllUntilTicks;
            var maximumOpacity = (float)_settings.MaximumMaskOpacity;

            for (var index = 0; index < _cells.Length; index++)
            {
                var cell = _cells[index];
                var target = revealEverything || !_finalDimMask[index] ? 0f : maximumOpacity;
                if (Math.Abs(cell.TargetAlpha - target) > 0.001f)
                {
                    cell.TargetAlpha = target;
                    _maskDirty = true;
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

                if (Math.Abs(oldAlpha - cell.Alpha) > 0.0005f)
                {
                    changed = true;
                }
                if (Math.Abs(cell.TargetAlpha - cell.Alpha) > 0.001f)
                {
                    anyAnimating = true;
                }
            }

            changed |= _maskDirty;
            _maskDirty = false;
        }

        _animationTimer.Interval = TimeSpan.FromMilliseconds(anyAnimating ? 33 : 100);
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
                _renderAlpha[index] = _enabled ? _cells[index].Alpha : 0f;
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
