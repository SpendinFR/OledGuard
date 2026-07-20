using System.Diagnostics;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private const float InfiniteDistance = 1_000_000f;

    private enum ChangeKind
    {
        None,
        Weak,
        Strong
    }

    private sealed class AnalysisCell
    {
        public long ContentUntilTicks;
        public byte WeakChangeStreak;
        public byte Luminance;
    }

    private sealed class RenderCell
    {
        public long MouseUntilTicks;
        public float Alpha;
        public float TargetAlpha;
    }

    private readonly FormsScreen _screen;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly ScreenSampler _sampler;
    private readonly AnalysisCell[] _analysisCells;
    private readonly RenderCell[] _renderCells;
    private readonly int _analysisColumns;
    private readonly int _analysisRows;
    private readonly int _renderColumns;
    private readonly int _renderRows;
    private readonly int _samplesPerCell;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly byte[] _previous;
    private readonly bool[] _changedCells;
    private readonly bool[] _visitedChanged;
    private readonly bool[] _contentActiveCells;
    private readonly float[] _contentDistance;
    private readonly float[] _renderAlpha;
    private readonly int[] _componentQueue;
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
    private bool _disposed;

    public MonitorSession(FormsScreen screen, AppSettings settings)
    {
        _screen = screen;
        _settings = settings;

        var bounds = screen.Bounds;
        _analysisColumns = Math.Max(1, (int)Math.Ceiling(bounds.Width / (double)settings.DetectionCellSizePixels));
        _analysisRows = Math.Max(1, (int)Math.Ceiling(bounds.Height / (double)settings.DetectionCellSizePixels));
        _renderColumns = Math.Max(1, (int)Math.Ceiling(bounds.Width / (double)settings.VisualCellSizePixels));
        _renderRows = Math.Max(1, (int)Math.Ceiling(bounds.Height / (double)settings.VisualCellSizePixels));
        _samplesPerCell = settings.SamplesPerCell;
        _sampleWidth = _analysisColumns * _samplesPerCell;
        _sampleHeight = _analysisRows * _samplesPerCell;
        _sampleStride = _sampleWidth * 4;

        var analysisCount = checked(_analysisColumns * _analysisRows);
        var renderCount = checked(_renderColumns * _renderRows);
        _previous = new byte[checked(_sampleStride * _sampleHeight)];
        _analysisCells = Enumerable.Range(0, analysisCount).Select(_ => new AnalysisCell()).ToArray();
        _renderCells = Enumerable.Range(0, renderCount).Select(_ => new RenderCell()).ToArray();
        _changedCells = new bool[analysisCount];
        _visitedChanged = new bool[analysisCount];
        _contentActiveCells = new bool[analysisCount];
        _contentDistance = new float[analysisCount];
        _renderAlpha = new float[renderCount];
        _componentQueue = new int[analysisCount];

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
            _revealAllUntilTicks = enabled
                ? now + ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0)
                : now;

            foreach (var cell in _analysisCells)
            {
                cell.ContentUntilTicks = now;
                cell.WeakChangeStreak = 0;
            }

            foreach (var cell in _renderCells)
            {
                cell.MouseUntilTicks = now;
                cell.TargetAlpha = 0f;
                cell.Alpha = 0f;
            }

            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
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
                    if (_renderCells.Any(static cell => cell.Alpha > 0.03f || cell.TargetAlpha > 0.03f))
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

            for (var row = 0; row < _analysisRows; row++)
            {
                for (var column = 0; column < _analysisColumns; column++)
                {
                    var index = row * _analysisColumns + column;
                    var cell = _analysisCells[index];
                    var kind = AnalyzeCell(current, row, column, cell);

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

            if (_changedCells.Any(static changed => changed))
            {
                ActivateChangedComponents(now);
                _maskDirty = true;
            }

            Buffer.BlockCopy(current, 0, _previous, 0, current.Length);
        }
    }

    private ChangeKind AnalyzeCell(byte[] current, int cellRow, int cellColumn, AnalysisCell cell)
    {
        var startX = cellColumn * _samplesPerCell;
        var startY = cellRow * _samplesPerCell;
        var changedSamples = 0;
        var strongSamples = 0;
        var sampleCount = _samplesPerCell * _samplesPerCell;
        double differenceTotal = 0;
        double maximumDifference = 0;
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
                    Math.Abs(blue - _previous[offset]) +
                    Math.Abs(green - _previous[offset + 1]) +
                    Math.Abs(red - _previous[offset + 2])) / 3.0;

                differenceTotal += difference;
                maximumDifference = Math.Max(maximumDifference, difference);
                luminanceTotal += (red * 54L + green * 183L + blue * 19L) >> 8;

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

        cell.Luminance = (byte)Math.Clamp(luminanceTotal / sampleCount, 0, 255);
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

    private void ActivateChangedComponents(long now)
    {
        Array.Clear(_visitedChanged, 0, _visitedChanged.Length);
        var activeUntil = now + ToStopwatchTicks(_settings.StaticDelaySeconds * 1000.0);
        var mergeGap = _settings.ContentMergeGapCells;

        for (var startIndex = 0; startIndex < _changedCells.Length; startIndex++)
        {
            if (!_changedCells[startIndex] || _visitedChanged[startIndex])
            {
                continue;
            }

            var queueHead = 0;
            var queueTail = 0;
            _componentQueue[queueTail++] = startIndex;
            _visitedChanged[startIndex] = true;

            var startRow = startIndex / _analysisColumns;
            var startColumn = startIndex % _analysisColumns;
            var minRow = startRow;
            var maxRow = startRow;
            var minColumn = startColumn;
            var maxColumn = startColumn;
            var componentCount = 0;

            while (queueHead < queueTail)
            {
                var index = _componentQueue[queueHead++];
                var row = index / _analysisColumns;
                var column = index % _analysisColumns;
                componentCount++;
                minRow = Math.Min(minRow, row);
                maxRow = Math.Max(maxRow, row);
                minColumn = Math.Min(minColumn, column);
                maxColumn = Math.Max(maxColumn, column);

                for (var offsetY = -mergeGap; offsetY <= mergeGap; offsetY++)
                {
                    var neighbourRow = row + offsetY;
                    if (neighbourRow < 0 || neighbourRow >= _analysisRows)
                    {
                        continue;
                    }

                    for (var offsetX = -mergeGap; offsetX <= mergeGap; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        var neighbourColumn = column + offsetX;
                        if (neighbourColumn < 0 || neighbourColumn >= _analysisColumns)
                        {
                            continue;
                        }

                        var neighbourIndex = neighbourRow * _analysisColumns + neighbourColumn;
                        if (_changedCells[neighbourIndex] && !_visitedChanged[neighbourIndex])
                        {
                            _visitedChanged[neighbourIndex] = true;
                            _componentQueue[queueTail++] = neighbourIndex;
                        }
                    }
                }
            }

            if (componentCount < _settings.MinimumActivityComponentCells)
            {
                continue;
            }

            ExpandToMinimum(ref minColumn, ref maxColumn, _settings.MinimumRevealBlockWidthCells, _analysisColumns);
            ExpandToMinimum(ref minRow, ref maxRow, _settings.MinimumRevealBlockHeightCells, _analysisRows);

            var padding = _settings.ContentActivationPaddingCells;
            minRow = Math.Max(0, minRow - padding);
            maxRow = Math.Min(_analysisRows - 1, maxRow + padding);
            minColumn = Math.Max(0, minColumn - padding);
            maxColumn = Math.Min(_analysisColumns - 1, maxColumn + padding);

            MergeWithActiveContent(ref minRow, ref maxRow, ref minColumn, ref maxColumn, now);

            for (var row = minRow; row <= maxRow; row++)
            {
                for (var column = minColumn; column <= maxColumn; column++)
                {
                    var cell = _analysisCells[row * _analysisColumns + column];
                    cell.ContentUntilTicks = Math.Max(cell.ContentUntilTicks, activeUntil);
                    cell.WeakChangeStreak = 0;
                }
            }
        }
    }

    private static void ExpandToMinimum(ref int minimum, ref int maximum, int minimumSize, int limit)
    {
        while (maximum - minimum + 1 < minimumSize)
        {
            var expanded = false;
            if (minimum > 0)
            {
                minimum--;
                expanded = true;
            }
            if (maximum - minimum + 1 < minimumSize && maximum + 1 < limit)
            {
                maximum++;
                expanded = true;
            }
            if (!expanded)
            {
                break;
            }
        }
    }

    private void MergeWithActiveContent(
        ref int minRow,
        ref int maxRow,
        ref int minColumn,
        ref int maxColumn,
        long now)
    {
        var gap = _settings.ContentMergeGapCells;
        var expanded = true;

        while (expanded)
        {
            expanded = false;
            var searchMinRow = Math.Max(0, minRow - gap);
            var searchMaxRow = Math.Min(_analysisRows - 1, maxRow + gap);
            var searchMinColumn = Math.Max(0, minColumn - gap);
            var searchMaxColumn = Math.Min(_analysisColumns - 1, maxColumn + gap);

            for (var row = searchMinRow; row <= searchMaxRow; row++)
            {
                for (var column = searchMinColumn; column <= searchMaxColumn; column++)
                {
                    if (_analysisCells[row * _analysisColumns + column].ContentUntilTicks <= now)
                    {
                        continue;
                    }

                    var oldMinRow = minRow;
                    var oldMaxRow = maxRow;
                    var oldMinColumn = minColumn;
                    var oldMaxColumn = maxColumn;
                    minRow = Math.Min(minRow, row);
                    maxRow = Math.Max(maxRow, row);
                    minColumn = Math.Min(minColumn, column);
                    maxColumn = Math.Max(maxColumn, column);

                    if (oldMinRow != minRow || oldMaxRow != maxRow ||
                        oldMinColumn != minColumn || oldMaxColumn != maxColumn)
                    {
                        expanded = true;
                    }
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

            ApplyOriginalMouseReveal(cursor, bounds, now);
            BuildTargets(bounds, now);
            changed = _maskDirty;
            _maskDirty = false;

            foreach (var cell in _renderCells)
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

        _animationTimer.Interval = TimeSpan.FromMilliseconds(anyAnimating ? 33 : 50);
        if (changed)
        {
            PushMask();
        }
    }

    private void ApplyOriginalMouseReveal(NativeMethods.Point cursor, System.Drawing.Rectangle bounds, long now)
    {
        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            return;
        }

        var moved = _lastCursorX == int.MinValue ||
            Math.Abs(cursor.X - _lastCursorX) >= 2 ||
            Math.Abs(cursor.Y - _lastCursorY) >= 2;
        _lastCursorX = cursor.X;
        _lastCursorY = cursor.Y;

        if (!_settings.MouseRevealWhileStationary && !moved)
        {
            return;
        }

        var radius = _settings.MouseRevealRadiusPixels;
        var radiusSquared = radius * radius;
        var localX = cursor.X - bounds.Left;
        var localY = cursor.Y - bounds.Top;
        var minColumn = Math.Max(0, (localX - radius) * _renderColumns / Math.Max(1, bounds.Width) - 1);
        var maxColumn = Math.Min(_renderColumns - 1, (localX + radius) * _renderColumns / Math.Max(1, bounds.Width) + 1);
        var minRow = Math.Max(0, (localY - radius) * _renderRows / Math.Max(1, bounds.Height) - 1);
        var maxRow = Math.Min(_renderRows - 1, (localY + radius) * _renderRows / Math.Max(1, bounds.Height) + 1);
        var revealUntil = now + ToStopwatchTicks(_settings.MouseRevealHoldMilliseconds);

        for (var row = minRow; row <= maxRow; row++)
        {
            var cellTop = bounds.Top + row * bounds.Height / _renderRows;
            var cellBottom = bounds.Top + (row + 1) * bounds.Height / _renderRows;

            for (var column = minColumn; column <= maxColumn; column++)
            {
                var cellLeft = bounds.Left + column * bounds.Width / _renderColumns;
                var cellRight = bounds.Left + (column + 1) * bounds.Width / _renderColumns;
                var distanceX = cursor.X < cellLeft
                    ? cellLeft - cursor.X
                    : cursor.X > cellRight ? cursor.X - cellRight : 0;
                var distanceY = cursor.Y < cellTop
                    ? cellTop - cursor.Y
                    : cursor.Y > cellBottom ? cursor.Y - cellBottom : 0;

                if (distanceX * distanceX + distanceY * distanceY > radiusSquared)
                {
                    continue;
                }

                var cell = _renderCells[row * _renderColumns + column];
                if (cell.MouseUntilTicks < revealUntil)
                {
                    cell.MouseUntilTicks = revealUntil;
                    _maskDirty = true;
                }
            }
        }
    }

    private void BuildTargets(System.Drawing.Rectangle bounds, long now)
    {
        if (now < _revealAllUntilTicks)
        {
            foreach (var cell in _renderCells)
            {
                if (Math.Abs(cell.TargetAlpha) > 0.001f)
                {
                    cell.TargetAlpha = 0f;
                    _maskDirty = true;
                }
            }
            return;
        }

        for (var index = 0; index < _analysisCells.Length; index++)
        {
            _contentActiveCells[index] = now < _analysisCells[index].ContentUntilTicks;
        }
        BuildContentDistanceField();

        var maximumOpacity = (float)_settings.MaximumMaskOpacity;
        for (var row = 0; row < _renderRows; row++)
        {
            var normalizedY = (row + 0.5f) / _renderRows;
            var analysisY = normalizedY * _analysisRows - 0.5f;
            var nearestAnalysisRow = Math.Clamp((int)MathF.Round(analysisY), 0, _analysisRows - 1);

            for (var column = 0; column < _renderColumns; column++)
            {
                var index = row * _renderColumns + column;
                var cell = _renderCells[index];
                var normalizedX = (column + 0.5f) / _renderColumns;
                var analysisX = normalizedX * _analysisColumns - 0.5f;
                var nearestAnalysisColumn = Math.Clamp((int)MathF.Round(analysisX), 0, _analysisColumns - 1);
                var distanceCells = SampleDistance(analysisX, analysisY);
                var contentAlpha = ContentAlphaFromDistance(distanceCells, maximumOpacity);
                var mouseAlpha = now < cell.MouseUntilTicks ? 0f : maximumOpacity;
                var target = Math.Min(contentAlpha, mouseAlpha);

                if (_settings.MinimumLuminanceToDim > 0)
                {
                    var analysisCell = _analysisCells[nearestAnalysisRow * _analysisColumns + nearestAnalysisColumn];
                    if (analysisCell.Luminance <= _settings.MinimumLuminanceToDim)
                    {
                        target = 0f;
                    }
                }

                if (Math.Abs(cell.TargetAlpha - target) > 0.001f)
                {
                    cell.TargetAlpha = target;
                    _maskDirty = true;
                }
            }
        }
    }

    private void BuildContentDistanceField()
    {
        var diagonal = 1f + 0.41421356f * (_settings.ContentCornerRoundnessPercent / 100f);

        for (var index = 0; index < _contentDistance.Length; index++)
        {
            _contentDistance[index] = _contentActiveCells[index] ? 0f : InfiniteDistance;
        }

        for (var row = 0; row < _analysisRows; row++)
        {
            for (var column = 0; column < _analysisColumns; column++)
            {
                var index = row * _analysisColumns + column;
                var value = _contentDistance[index];
                if (column > 0)
                {
                    value = Math.Min(value, _contentDistance[index - 1] + 1f);
                }
                if (row > 0)
                {
                    value = Math.Min(value, _contentDistance[index - _analysisColumns] + 1f);
                    if (column > 0)
                    {
                        value = Math.Min(value, _contentDistance[index - _analysisColumns - 1] + diagonal);
                    }
                    if (column + 1 < _analysisColumns)
                    {
                        value = Math.Min(value, _contentDistance[index - _analysisColumns + 1] + diagonal);
                    }
                }
                _contentDistance[index] = value;
            }
        }

        for (var row = _analysisRows - 1; row >= 0; row--)
        {
            for (var column = _analysisColumns - 1; column >= 0; column--)
            {
                var index = row * _analysisColumns + column;
                var value = _contentDistance[index];
                if (column + 1 < _analysisColumns)
                {
                    value = Math.Min(value, _contentDistance[index + 1] + 1f);
                }
                if (row + 1 < _analysisRows)
                {
                    value = Math.Min(value, _contentDistance[index + _analysisColumns] + 1f);
                    if (column > 0)
                    {
                        value = Math.Min(value, _contentDistance[index + _analysisColumns - 1] + diagonal);
                    }
                    if (column + 1 < _analysisColumns)
                    {
                        value = Math.Min(value, _contentDistance[index + _analysisColumns + 1] + diagonal);
                    }
                }
                _contentDistance[index] = value;
            }
        }
    }

    private float SampleDistance(float x, float y)
    {
        var x0 = Math.Clamp((int)MathF.Floor(x), 0, _analysisColumns - 1);
        var x1 = Math.Min(_analysisColumns - 1, x0 + 1);
        var y0 = Math.Clamp((int)MathF.Floor(y), 0, _analysisRows - 1);
        var y1 = Math.Min(_analysisRows - 1, y0 + 1);
        var tx = Math.Clamp(x - x0, 0f, 1f);
        var ty = Math.Clamp(y - y0, 0f, 1f);
        var top = Lerp(_contentDistance[y0 * _analysisColumns + x0], _contentDistance[y0 * _analysisColumns + x1], tx);
        var bottom = Lerp(_contentDistance[y1 * _analysisColumns + x0], _contentDistance[y1 * _analysisColumns + x1], tx);
        return Lerp(top, bottom, ty);
    }

    private float ContentAlphaFromDistance(float distanceCells, float maximumOpacity)
    {
        if (distanceCells >= InfiniteDistance / 2)
        {
            return maximumOpacity;
        }
        if (distanceCells <= 0f)
        {
            return 0f;
        }
        if (_settings.ContentFeatherRadiusPixels <= 0)
        {
            return maximumOpacity;
        }

        var distancePixels = distanceCells * _settings.DetectionCellSizePixels;
        if (distancePixels >= _settings.ContentFeatherRadiusPixels)
        {
            return maximumOpacity;
        }

        return SmoothStep(distancePixels / _settings.ContentFeatherRadiusPixels) * maximumOpacity;
    }

    private void PushMask()
    {
        lock (_sync)
        {
            var steps = Math.Max(2, _settings.OpacitySteps);
            for (var index = 0; index < _renderCells.Length; index++)
            {
                var value = _enabled ? _renderCells[index].Alpha : 0f;
                value = Math.Clamp(value, 0f, 1f);
                value = MathF.Round(value * (steps - 1)) / (steps - 1);
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
