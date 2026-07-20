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
        public int MotionEvidence;
        public byte Luminance;
        public bool DimLatched;
        public float Alpha;
    }

    private sealed class RegionState
    {
        public float Alpha;
    }

    private readonly FormsScreen _screen;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly ScreenSampler _sampler;
    private readonly Cell[] _cells;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly long _effectiveShortReferenceTicks;
    private readonly long _effectiveMediumReferenceTicks;
    private readonly long _effectiveLongReferenceTicks;
    private readonly byte[] _previous;
    private readonly byte[] _shortReference;
    private readonly byte[] _mediumReference;
    private readonly byte[] _longReference;
    private readonly bool[] _immediateChanges;
    private readonly bool[] _shortChanges;
    private readonly bool[] _mediumChanges;
    private readonly bool[] _longChanges;
    private readonly bool[] _candidateStatic;
    private readonly bool[] _maskA;
    private readonly bool[] _maskB;
    private readonly bool[] _finalDimMask;
    private readonly bool[] _visited;
    private readonly int[] _queue;
    private readonly float[] _renderAlpha;
    private int[] _regionMap;
    private int[] _nextRegionMap;
    private List<RegionState> _regions = new();
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
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;
    private bool _lastRevealEverything;
    private bool _disposed;

    public MonitorSession(FormsScreen screen, AppSettings settings)
    {
        _screen = screen;
        _settings = settings;

        var bounds = screen.Bounds;
        var coarseColumns = Math.Max(1, (int)Math.Ceiling(bounds.Width / (double)settings.DetectionCellSizePixels));
        var coarseRows = Math.Max(1, (int)Math.Ceiling(bounds.Height / (double)settings.DetectionCellSizePixels));

        _columns = checked(coarseColumns * settings.SamplesPerCell);
        _rows = checked(coarseRows * settings.SamplesPerCell);
        _sampleWidth = _columns;
        _sampleHeight = _rows;
        _sampleStride = checked(_sampleWidth * 4);

        var delayMilliseconds = settings.StaticDelaySeconds * 1000.0;
        var shortCap = Math.Max(1000.0, delayMilliseconds * 0.25);
        var effectiveShort = Math.Min(settings.ShortReferenceSeconds * 1000.0, shortCap);
        var mediumCap = Math.Max(effectiveShort + 1000.0, delayMilliseconds * 0.50);
        var effectiveMedium = Math.Min(settings.MediumReferenceSeconds * 1000.0, mediumCap);
        var longCap = Math.Max(effectiveMedium + 1000.0, delayMilliseconds * 0.75);
        var effectiveLong = Math.Min(settings.LongReferenceSeconds * 1000.0, longCap);
        _effectiveShortReferenceTicks = ToStopwatchTicks(effectiveShort);
        _effectiveMediumReferenceTicks = ToStopwatchTicks(effectiveMedium);
        _effectiveLongReferenceTicks = ToStopwatchTicks(effectiveLong);

        var cellCount = checked(_columns * _rows);
        var sampleBytes = checked(_sampleStride * _sampleHeight);
        _cells = Enumerable.Range(0, cellCount).Select(_ => new Cell()).ToArray();
        _previous = new byte[sampleBytes];
        _shortReference = new byte[sampleBytes];
        _mediumReference = new byte[sampleBytes];
        _longReference = new byte[sampleBytes];
        _immediateChanges = new bool[cellCount];
        _shortChanges = new bool[cellCount];
        _mediumChanges = new bool[cellCount];
        _longChanges = new bool[cellCount];
        _candidateStatic = new bool[cellCount];
        _maskA = new bool[cellCount];
        _maskB = new bool[cellCount];
        _finalDimMask = new bool[cellCount];
        _visited = new bool[cellCount];
        _queue = new int[cellCount];
        _renderAlpha = new float[cellCount];
        _regionMap = new int[cellCount];
        _nextRegionMap = new int[cellCount];
        Array.Fill(_regionMap, -1);
        Array.Fill(_nextRegionMap, -1);

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
            _revealAllUntilTicks = enabled ? now + ToStopwatchTicks(750) : now;
            _hasReferences = false;
            _shortReferenceTicks = 0;
            _mediumReferenceTicks = 0;
            _longReferenceTicks = 0;
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            _lastRevealEverything = enabled;

            Array.Clear(_immediateChanges, 0, _immediateChanges.Length);
            Array.Clear(_shortChanges, 0, _shortChanges.Length);
            Array.Clear(_mediumChanges, 0, _mediumChanges.Length);
            Array.Clear(_longChanges, 0, _longChanges.Length);
            Array.Clear(_candidateStatic, 0, _candidateStatic.Length);
            Array.Clear(_maskA, 0, _maskA.Length);
            Array.Clear(_maskB, 0, _maskB.Length);
            Array.Clear(_finalDimMask, 0, _finalDimMask.Length);
            Array.Fill(_regionMap, -1);
            Array.Fill(_nextRegionMap, -1);
            _regions.Clear();

            foreach (var cell in _cells)
            {
                cell.LastMotionTicks = now;
                cell.StableEvidence = 0;
                cell.MotionEvidence = 0;
                cell.Luminance = 0;
                cell.DimLatched = false;
                cell.Alpha = 0f;
            }

            _maskDirty = true;
        }

        PushMask(Stopwatch.GetTimestamp(), default, false);
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
                    if (_regions.Count > 0 || _cells.Any(static cell => cell.Alpha > 0.02f))
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
                    cell.MotionEvidence = 0;
                }
                return;
            }

            for (var index = 0; index < _cells.Length; index++)
            {
                var offset = index * 4;
                var blue = current[offset];
                var green = current[offset + 1];
                var red = current[offset + 2];
                _cells[index].Luminance = (byte)((red * 54L + green * 183L + blue * 19L) >> 8);
                _immediateChanges[index] = PixelChanged(current, _previous, offset);
                _shortChanges[index] = PixelChanged(current, _shortReference, offset);
                _mediumChanges[index] = PixelChanged(current, _mediumReference, offset);
                _longChanges[index] = PixelChanged(current, _longReference, offset);
            }

            var staticDelayTicks = ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);
            for (var row = 0; row < _rows; row++)
            {
                for (var column = 0; column < _columns; column++)
                {
                    var index = row * _columns + column;
                    var cell = _cells[index];
                    var immediateChanged = HasSupportedChange(_immediateChanges, row, column);
                    var shortChanged = HasSupportedChange(_shortChanges, row, column);
                    var mediumChanged = HasSupportedChange(_mediumChanges, row, column);
                    var longChanged = HasSupportedChange(_longChanges, row, column);

                    if (immediateChanged)
                    {
                        if (cell.MotionEvidence < int.MaxValue)
                        {
                            cell.MotionEvidence++;
                        }
                    }
                    else
                    {
                        cell.MotionEvidence = 0;
                    }

                    if (cell.DimLatched)
                    {
                        if (cell.MotionEvidence >= _settings.MotionConfirmationSamples)
                        {
                            cell.DimLatched = false;
                            cell.LastMotionTicks = now;
                            cell.StableEvidence = 0;
                            cell.MotionEvidence = 0;
                        }

                        _candidateStatic[index] = false;
                        continue;
                    }

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

                    var brightEnough = _settings.MinimumLuminanceToDim <= 0 ||
                        cell.Luminance > _settings.MinimumLuminanceToDim;
                    _candidateStatic[index] =
                        brightEnough &&
                        cell.StableEvidence >= _settings.StableConfirmationSamples &&
                        now - cell.LastMotionTicks >= staticDelayTicks &&
                        !mediumChanged &&
                        !longChanged;
                }
            }

            BuildLatchedDimMask();
            RebuildUniformRegions();
            Buffer.BlockCopy(current, 0, _previous, 0, current.Length);

            if (now - _shortReferenceTicks >= _effectiveShortReferenceTicks)
            {
                Buffer.BlockCopy(current, 0, _shortReference, 0, current.Length);
                _shortReferenceTicks = now;
            }

            if (now - _mediumReferenceTicks >= _effectiveMediumReferenceTicks)
            {
                Buffer.BlockCopy(current, 0, _mediumReference, 0, current.Length);
                _mediumReferenceTicks = now;
            }

            if (now - _longReferenceTicks >= _effectiveLongReferenceTicks)
            {
                Buffer.BlockCopy(current, 0, _longReference, 0, current.Length);
                _longReferenceTicks = now;
            }

            _maskDirty = true;
        }
    }

    private bool PixelChanged(byte[] current, byte[] reference, int offset)
    {
        var difference = (
            Math.Abs(current[offset] - reference[offset]) +
            Math.Abs(current[offset + 1] - reference[offset + 1]) +
            Math.Abs(current[offset + 2] - reference[offset + 2])) / 3.0;
        return difference >= _settings.DifferenceThreshold;
    }

    private bool HasSupportedChange(bool[] changes, int row, int column)
    {
        var index = row * _columns + column;
        if (!changes[index])
        {
            return false;
        }

        var changedCount = 0;
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
                if (changes[neighbourRow * _columns + neighbourColumn])
                {
                    changedCount++;
                }
            }
        }

        var required = Math.Max(2, (int)Math.Ceiling(sampleCount * _settings.ChangedSampleFraction));
        return changedCount >= Math.Min(sampleCount, required);
    }

    private void BuildLatchedDimMask()
    {
        CleanMask(_candidateStatic, _maskA);
        for (var index = 0; index < _cells.Length; index++)
        {
            if (_maskA[index])
            {
                _cells[index].DimLatched = true;
            }

            _maskB[index] = _cells[index].DimLatched;
        }

        CleanMask(_maskB, _finalDimMask);
    }

    private void CleanMask(bool[] input, bool[] output)
    {
        Array.Copy(input, _maskA, input.Length);
        var source = _maskA;
        var destination = _maskB;

        for (var pass = 0; pass < _settings.MajorityFilterPasses; pass++)
        {
            ApplyBidirectionalMajority(source, destination);
            (source, destination) = (destination, source);
        }

        Array.Copy(source, output, source.Length);
        RemoveSmallDimIslands(output);
        FillSmallBrightHoles(output);
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

    private void RebuildUniformRegions()
    {
        Array.Fill(_nextRegionMap, -1);
        var nextRegions = new List<RegionState>();
        Array.Clear(_visited, 0, _visited.Length);

        for (var start = 0; start < _finalDimMask.Length; start++)
        {
            if (!_finalDimMask[start] || _visited[start])
            {
                continue;
            }

            var count = CollectComponent(_finalDimMask, start, true, out _);
            var initialAlpha = 0f;
            for (var index = 0; index < count; index++)
            {
                var cellIndex = _queue[index];
                initialAlpha = Math.Max(initialAlpha, _cells[cellIndex].Alpha);
                var oldRegion = _regionMap[cellIndex];
                if (oldRegion >= 0 && oldRegion < _regions.Count)
                {
                    initialAlpha = Math.Max(initialAlpha, _regions[oldRegion].Alpha);
                }
            }

            var newRegionId = nextRegions.Count;
            nextRegions.Add(new RegionState { Alpha = initialAlpha });
            for (var index = 0; index < count; index++)
            {
                _nextRegionMap[_queue[index]] = newRegionId;
            }
        }

        (_regionMap, _nextRegionMap) = (_nextRegionMap, _regionMap);
        _regions = nextRegions;
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
        var cursorMoved = cursor.X != _lastCursorX || cursor.Y != _lastCursorY;
        _lastCursorX = cursor.X;
        _lastCursorY = cursor.Y;

        var revealEverything = now < _revealAllUntilTicks;
        var revealStateChanged = revealEverything != _lastRevealEverything;
        _lastRevealEverything = revealEverything;
        var changed = cursorMoved || revealStateChanged;
        var anyAnimating = false;

        lock (_sync)
        {
            if (!_enabled)
            {
                _animationTimer.Interval = TimeSpan.FromMilliseconds(250);
                return;
            }

            var maximumOpacity = (float)_settings.MaximumMaskOpacity;
            foreach (var region in _regions)
            {
                var oldAlpha = region.Alpha;
                var step = (float)(elapsedMs / Math.Max(1, _settings.DarkenFadeMilliseconds));
                region.Alpha = Math.Min(maximumOpacity, region.Alpha + step);
                if (Math.Abs(oldAlpha - region.Alpha) > 0.0005f)
                {
                    changed = true;
                }
                if (region.Alpha < maximumOpacity - 0.001f)
                {
                    anyAnimating = true;
                }
            }

            for (var index = 0; index < _cells.Length; index++)
            {
                var cell = _cells[index];
                var oldAlpha = cell.Alpha;
                var regionId = _regionMap[index];

                if (regionId >= 0 && regionId < _regions.Count)
                {
                    cell.Alpha = _regions[regionId].Alpha;
                }
                else if (cell.Alpha > 0f)
                {
                    var step = (float)(elapsedMs / Math.Max(1, _settings.RevealFadeMilliseconds));
                    cell.Alpha = Math.Max(0f, cell.Alpha - step);
                }

                if (Math.Abs(oldAlpha - cell.Alpha) > 0.0005f)
                {
                    changed = true;
                }
                if (regionId < 0 && cell.Alpha > 0.001f)
                {
                    anyAnimating = true;
                }
            }

            changed |= _maskDirty;
            _maskDirty = false;
        }

        _animationTimer.Interval = TimeSpan.FromMilliseconds(
            _settings.MouseRevealEnabled || anyAnimating ? 33 : 100);
        if (changed)
        {
            PushMask(now, cursor, true);
        }
    }

    private void PushMask(long now, NativeMethods.Point cursor, bool hasCursor)
    {
        lock (_sync)
        {
            var revealEverything = now < _revealAllUntilTicks;
            var bounds = _screen.Bounds;
            var useMouse = hasCursor && _settings.MouseRevealEnabled &&
                (_settings.MouseRevealRadiusPixels > 0 || _settings.MouseRevealFeatherPixels > 0) &&
                bounds.Contains(cursor.X, cursor.Y);
            var innerRadius = _settings.MouseRevealRadiusPixels;
            var feather = _settings.MouseRevealFeatherPixels;
            var outerRadius = innerRadius + feather;

            for (var row = 0; row < _rows; row++)
            {
                var centerY = bounds.Top + (row + 0.5) * bounds.Height / _rows;
                for (var column = 0; column < _columns; column++)
                {
                    var index = row * _columns + column;
                    var alpha = _enabled && !revealEverything ? _cells[index].Alpha : 0f;

                    if (useMouse && alpha > 0f)
                    {
                        var centerX = bounds.Left + (column + 0.5) * bounds.Width / _columns;
                        var deltaX = centerX - cursor.X;
                        var deltaY = centerY - cursor.Y;
                        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

                        if (distance <= innerRadius)
                        {
                            alpha = 0f;
                        }
                        else if (feather > 0 && distance < outerRadius)
                        {
                            var progress = (float)((distance - innerRadius) / feather);
                            alpha *= SmoothStep(progress);
                        }
                    }

                    _renderAlpha[index] = alpha;
                }
            }
        }

        _overlay.SetMask(_renderAlpha, _columns, _rows);
    }

    private static float SmoothStep(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value * value * (3f - 2f * value);
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
