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
        public float ExposureSeconds;
        public float TargetAlpha;
        public float Alpha;
    }

    private sealed class RegionState
    {
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
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly byte[] _previous;
    private readonly byte[] _shortReference;
    private readonly byte[] _mediumReference;
    private readonly byte[] _longReference;
    private readonly bool[] _immediateChanges;
    private readonly bool[] _shortChanges;
    private readonly bool[] _mediumChanges;
    private readonly bool[] _longChanges;
    private readonly bool[] _rawStatic;
    private readonly bool[] _maskA;
    private readonly bool[] _maskB;
    private readonly bool[] _cleanStableMask;
    private readonly bool[] _finalDimMask;
    private readonly bool[] _previousDimMask;
    private readonly bool[] _visited;
    private readonly int[] _queue;
    private readonly float[] _renderAlpha;
    private readonly string _exposureIdentity;
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
    private long _lastCaptureTicks;
    private long _lastExposureSaveTicks;
    private int _mouseRevealIndex = -1;
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
        _rawStatic = new bool[cellCount];
        _maskA = new bool[cellCount];
        _maskB = new bool[cellCount];
        _cleanStableMask = new bool[cellCount];
        _finalDimMask = new bool[cellCount];
        _previousDimMask = new bool[cellCount];
        _visited = new bool[cellCount];
        _queue = new int[cellCount];
        _renderAlpha = new float[cellCount];
        _regionMap = new int[cellCount];
        _nextRegionMap = new int[cellCount];
        Array.Fill(_regionMap, -1);
        Array.Fill(_nextRegionMap, -1);

        _exposureIdentity = string.Join(
            "|",
            screen.DeviceName,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            _columns,
            _rows);

        var maximumExposureSeconds = GetMaximumStoredExposureSeconds();
        var persistedExposure = ExposureStore.Load(
            _exposureIdentity,
            _columns,
            _rows,
            maximumExposureSeconds);
        for (var index = 0; index < _cells.Length; index++)
        {
            _cells[index].ExposureSeconds = persistedExposure[index];
        }

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
            _revealAllUntilTicks = enabled ? now + ToStopwatchTicks(10_000) : now;
            _hasReferences = false;
            _shortReferenceTicks = 0;
            _mediumReferenceTicks = 0;
            _longReferenceTicks = 0;
            _lastCaptureTicks = 0;
            _mouseRevealIndex = -1;

            Array.Clear(_immediateChanges, 0, _immediateChanges.Length);
            Array.Clear(_shortChanges, 0, _shortChanges.Length);
            Array.Clear(_mediumChanges, 0, _mediumChanges.Length);
            Array.Clear(_longChanges, 0, _longChanges.Length);
            Array.Clear(_rawStatic, 0, _rawStatic.Length);
            Array.Clear(_maskA, 0, _maskA.Length);
            Array.Clear(_maskB, 0, _maskB.Length);
            Array.Clear(_cleanStableMask, 0, _cleanStableMask.Length);
            Array.Clear(_finalDimMask, 0, _finalDimMask.Length);
            Array.Clear(_previousDimMask, 0, _previousDimMask.Length);
            Array.Fill(_regionMap, -1);
            Array.Fill(_nextRegionMap, -1);
            _regions.Clear();

            foreach (var cell in _cells)
            {
                cell.LastMotionTicks = now;
                cell.StableEvidence = 0;
                cell.Luminance = 0;
                cell.TargetAlpha = 0f;
                cell.Alpha = 0f;
                // ExposureSeconds intentionally survives disable/enable cycles.
            }

            _maskDirty = true;
        }

        PushMask();
        if (!enabled)
        {
            SaveExposureState();
        }
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
                    if (_cells.Any(static cell => cell.Alpha > 0.02f))
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
        var saveExposure = false;

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
                _lastCaptureTicks = now;
                _lastExposureSaveTicks = now;
                foreach (var cell in _cells)
                {
                    cell.LastMotionTicks = now;
                    cell.StableEvidence = 0;
                }
                return;
            }

            var elapsedSeconds = Math.Clamp(
                FromStopwatchTicks(now - _lastCaptureTicks) / 1000.0,
                0.0,
                5.0);
            _lastCaptureTicks = now;

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
                        cell.StableEvidence = Math.Max(0, cell.StableEvidence - 1);
                    }

                    _rawStatic[index] = cell.StableEvidence >= _settings.StableConfirmationSamples;
                }
            }

            BuildCleanDimMask(now, elapsedSeconds);
            Buffer.BlockCopy(current, 0, _previous, 0, current.Length);

            var effectiveShortReferenceSeconds = Math.Min(
                _settings.ShortReferenceSeconds,
                Math.Max(1, _settings.StaticEligibilitySeconds / 3));
            var effectiveMediumReferenceSeconds = Math.Min(
                _settings.MediumReferenceSeconds,
                Math.Max(effectiveShortReferenceSeconds + 1, _settings.StaticEligibilitySeconds * 2 / 3));
            var effectiveLongReferenceSeconds = Math.Min(
                _settings.LongReferenceSeconds,
                Math.Max(effectiveMediumReferenceSeconds + 1, _settings.StaticEligibilitySeconds));

            if (now - _shortReferenceTicks >= ToStopwatchTicks(effectiveShortReferenceSeconds * 1000.0))
            {
                Buffer.BlockCopy(current, 0, _shortReference, 0, current.Length);
                _shortReferenceTicks = now;
            }

            if (now - _mediumReferenceTicks >= ToStopwatchTicks(effectiveMediumReferenceSeconds * 1000.0))
            {
                Buffer.BlockCopy(current, 0, _mediumReference, 0, current.Length);
                _mediumReferenceTicks = now;
            }

            if (now - _longReferenceTicks >= ToStopwatchTicks(effectiveLongReferenceSeconds * 1000.0))
            {
                Buffer.BlockCopy(current, 0, _longReference, 0, current.Length);
                _longReferenceTicks = now;
            }

            var saveIntervalTicks = ToStopwatchTicks(_settings.ExposureSaveMinutes * 60_000.0);
            if (now - _lastExposureSaveTicks >= saveIntervalTicks)
            {
                _lastExposureSaveTicks = now;
                saveExposure = true;
            }

            _maskDirty = true;
        }

        if (saveExposure)
        {
            SaveExposureState();
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

        var required = Math.Max(1, (int)Math.Ceiling(sampleCount * _settings.ChangedSampleFraction));
        return changedCount >= required;
    }

    private void BuildCleanDimMask(long now, double elapsedSeconds)
    {
        Array.Copy(_rawStatic, _maskA, _rawStatic.Length);
        var source = _maskA;
        var destination = _maskB;

        for (var pass = 0; pass < _settings.MajorityFilterPasses; pass++)
        {
            ApplyBidirectionalMajority(source, destination);
            (source, destination) = (destination, source);
        }

        Array.Copy(source, _cleanStableMask, source.Length);
        RemoveSmallStaticIslands(_cleanStableMask);
        FillSmallActiveHoles(_cleanStableMask);

        UpdateCumulativeExposure(now, elapsedSeconds);
        BuildExposureDimMask(now);
        RebuildUniformRegions();
    }

    private void UpdateCumulativeExposure(long now, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0.0)
        {
            return;
        }

        var eligibilityTicks = ToStopwatchTicks(_settings.StaticEligibilitySeconds * 1000.0);
        var maximumExposure = GetMaximumStoredExposureSeconds();

        for (var index = 0; index < _cells.Length; index++)
        {
            var cell = _cells[index];
            var supportedMotion = _immediateChanges[index] || _shortChanges[index];
            var stable = _cleanStableMask[index] && _rawStatic[index] && !supportedMotion;
            var stableLongEnough = stable && now - cell.LastMotionTicks >= eligibilityTicks;

            if (stableLongEnough)
            {
                var luminanceWeight = ComputeLuminanceWeight(cell.Luminance);
                var remainingLight = Math.Clamp(1.0 - cell.Alpha, 0.05, 1.0);
                cell.ExposureSeconds += (float)(elapsedSeconds * luminanceWeight * remainingLight);
            }
            else
            {
                var decayRate = supportedMotion
                    ? _settings.MovementExposureDecayRate
                    : _settings.UncertainExposureDecayRate;
                cell.ExposureSeconds -= (float)(elapsedSeconds * decayRate);
            }

            cell.ExposureSeconds = Math.Clamp(cell.ExposureSeconds, 0f, maximumExposure);
            cell.TargetAlpha = ComputeTargetAlpha(cell.ExposureSeconds);

            if (stableLongEnough &&
                _settings.StaticEligibilitySeconds <= 15 &&
                cell.Luminance > _settings.MinimumLuminanceToDim)
            {
                cell.TargetAlpha = Math.Max(
                    cell.TargetAlpha,
                    (float)_settings.MaximumMaskOpacity);
            }
        }
    }

    private double ComputeLuminanceWeight(byte luminance)
    {
        if (luminance <= _settings.MinimumLuminanceToDim)
        {
            return 0.0;
        }

        var denominator = Math.Max(1.0, 255.0 - _settings.MinimumLuminanceToDim);
        var normalized = Math.Clamp((luminance - _settings.MinimumLuminanceToDim) / denominator, 0.0, 1.0);

        // Bright whites accumulate substantially faster than mid-grey UI chrome.
        return Math.Pow(normalized, 1.6);
    }

    private float ComputeTargetAlpha(float exposureSeconds)
    {
        var start = _settings.ExposureStartMinutes * 60.0;
        var full = _settings.ExposureFullMinutes * 60.0;
        if (exposureSeconds <= start)
        {
            return 0f;
        }

        var progress = Math.Clamp((exposureSeconds - start) / Math.Max(1.0, full - start), 0.0, 1.0);
        var smooth = progress * progress * (3.0 - 2.0 * progress);
        return (float)(smooth * _settings.MaximumMaskOpacity);
    }

    private void BuildExposureDimMask(long now)
    {
        Array.Copy(_finalDimMask, _previousDimMask, _finalDimMask.Length);
        var reapplyTicks = ToStopwatchTicks(_settings.ReapplyDelaySeconds * 1000.0);
        for (var index = 0; index < _cleanStableMask.Length; index++)
        {
            var cell = _cells[index];
            _maskA[index] = _cleanStableMask[index] &&
                _rawStatic[index] &&
                !_immediateChanges[index] &&
                !_shortChanges[index] &&
                now - cell.LastMotionTicks >= reapplyTicks &&
                cell.TargetAlpha > 0.001f;
        }

        var source = _maskA;
        var destination = _maskB;
        for (var pass = 0; pass < _settings.MajorityFilterPasses; pass++)
        {
            ApplyBidirectionalMajority(source, destination);
            (source, destination) = (destination, source);
        }

        Array.Copy(source, _finalDimMask, source.Length);
        RemoveSmallStaticIslands(_finalDimMask);
        FillSmallActiveHoles(_finalDimMask);
        SuppressDarkRegions(_finalDimMask);

        PreserveUnchangedDimCells(_finalDimMask, _previousDimMask);
        ApplyBidirectionalMajority(_finalDimMask, _maskB);
        Array.Copy(_maskB, _finalDimMask, _finalDimMask.Length);
        RemoveSmallStaticIslands(_finalDimMask);
        FillSmallActiveHoles(_finalDimMask);
        SuppressDarkRegions(_finalDimMask);
    }

    private void PreserveUnchangedDimCells(bool[] currentMask, bool[] previousMask)
    {
        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                if (!previousMask[index] || currentMask[index])
                {
                    continue;
                }

                var moved =
                    HasSupportedChange(_immediateChanges, row, column) ||
                    HasSupportedChange(_shortChanges, row, column);

                if (!moved)
                {
                    currentMask[index] = true;
                }
            }
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

    private void RemoveSmallStaticIslands(bool[] mask)
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

    private void FillSmallActiveHoles(bool[] mask)
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
            var overlapCounts = new Dictionary<int, int>();
            double targetTotal = 0.0;
            float targetMaximum = 0f;

            for (var index = 0; index < count; index++)
            {
                var cellIndex = _queue[index];
                var cellTarget = _cells[cellIndex].TargetAlpha;
                targetTotal += cellTarget;
                targetMaximum = Math.Max(targetMaximum, cellTarget);

                var oldRegion = _regionMap[cellIndex];
                if (oldRegion < 0 || oldRegion >= _regions.Count)
                {
                    continue;
                }

                overlapCounts.TryGetValue(oldRegion, out var overlap);
                overlapCounts[oldRegion] = overlap + 1;
            }

            float initialAlpha = 0f;
            if (overlapCounts.Count > 0)
            {
                var bestOldRegion = overlapCounts.MaxBy(static pair => pair.Value).Key;
                initialAlpha = _regions[bestOldRegion].Alpha;
            }

            var averageTarget = count == 0 ? 0f : (float)(targetTotal / count);
            var regionTarget = Math.Clamp(
                averageTarget * 0.65f + targetMaximum * 0.35f,
                0f,
                (float)_settings.MaximumMaskOpacity);

            var newRegionId = nextRegions.Count;
            nextRegions.Add(new RegionState
            {
                Alpha = initialAlpha,
                TargetAlpha = regionTarget
            });

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
            var mouseRevealIndex = GetMouseRevealIndex();
            if (mouseRevealIndex != _mouseRevealIndex)
            {
                _mouseRevealIndex = mouseRevealIndex;
                changed = true;
            }

            foreach (var region in _regions)
            {
                var target = revealEverything ? 0f : region.TargetAlpha;
                var duration = target > region.Alpha
                    ? _settings.DarkenFadeMilliseconds
                    : _settings.RevealFadeMilliseconds;
                var step = maximumOpacity * (float)(elapsedMs / Math.Max(1, duration));
                var oldAlpha = region.Alpha;
                region.Alpha = MoveTowards(region.Alpha, target, step);

                if (Math.Abs(oldAlpha - region.Alpha) > 0.0005f)
                {
                    changed = true;
                }
                if (Math.Abs(region.Alpha - target) > 0.001f)
                {
                    anyAnimating = true;
                }
            }

            for (var index = 0; index < _cells.Length; index++)
            {
                var cell = _cells[index];
                var oldAlpha = cell.Alpha;
                var regionId = _regionMap[index];

                if (!revealEverything && regionId >= 0 && regionId < _regions.Count)
                {
                    cell.Alpha = _regions[regionId].Alpha;
                }
                else if (cell.Alpha > 0f)
                {
                    var step = maximumOpacity * (float)(elapsedMs / Math.Max(1, _settings.RevealFadeMilliseconds));
                    cell.Alpha = Math.Max(0f, cell.Alpha - step);
                }

                if (Math.Abs(oldAlpha - cell.Alpha) > 0.0005f)
                {
                    changed = true;
                }
                if ((regionId < 0 || revealEverything) && cell.Alpha > 0.001f)
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

    private int GetMouseRevealIndex()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return -1;
        }

        var bounds = _screen.Bounds;
        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            return -1;
        }

        var localX = cursor.X - bounds.Left;
        var localY = cursor.Y - bounds.Top;
        var column = Math.Clamp(
            localX * _columns / Math.Max(1, bounds.Width),
            0,
            _columns - 1);
        var row = Math.Clamp(
            localY * _rows / Math.Max(1, bounds.Height),
            0,
            _rows - 1);
        return row * _columns + column;
    }

    private void PushMask()
    {
        lock (_sync)
        {
            for (var index = 0; index < _cells.Length; index++)
            {
                _renderAlpha[index] = _enabled ? _cells[index].Alpha : 0f;
            }

            if (_enabled &&
                _mouseRevealIndex >= 0 &&
                _mouseRevealIndex < _renderAlpha.Length)
            {
                _renderAlpha[_mouseRevealIndex] = 0f;
            }
        }

        _overlay.SetMask(_renderAlpha, _columns, _rows);
    }

    private void SaveExposureState()
    {
        float[] snapshot;
        lock (_sync)
        {
            snapshot = new float[_cells.Length];
            for (var index = 0; index < _cells.Length; index++)
            {
                snapshot[index] = _cells[index].ExposureSeconds;
            }
        }

        ExposureStore.Save(_exposureIdentity, _columns, _rows, snapshot);
    }

    private float GetMaximumStoredExposureSeconds() =>
        (float)(_settings.ExposureFullMinutes * 60.0 * 2.0);

    private static float MoveTowards(float current, float target, float maximumDelta)
    {
        if (Math.Abs(target - current) <= maximumDelta)
        {
            return target;
        }

        return current + Math.Sign(target - current) * maximumDelta;
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
        SaveExposureState();
        _cancellation.Cancel();
        _animationTimer.Stop();
        _sampler.Dispose();
        _overlay.Close();
        _cancellation.Dispose();
    }
}
