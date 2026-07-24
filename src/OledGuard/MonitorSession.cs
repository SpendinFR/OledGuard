using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed partial class MonitorSession : IDisposable
{
    private readonly record struct DetectedRegion(
        int MinimumRow,
        int MaximumRow,
        int MinimumColumn,
        int MaximumColumn,
        int MotionCells)
    {
        public int Width =>
            MaximumColumn -
            MinimumColumn +
            1;

        public int Height =>
            MaximumRow -
            MinimumRow +
            1;

        public int Area =>
            Width *
            Height;
    }

    private readonly record struct MouseSample(
        double X,
        double Y,
        long Timestamp);

    private readonly record struct BoundsObservation(
        int MinimumRow,
        int MaximumRow,
        int MinimumColumn,
        int MaximumColumn,
        long Timestamp);

    private sealed class TrackedRegion
    {
        public int MinimumRow;
        public int MaximumRow;
        public int MinimumColumn;
        public int MaximumColumn;

        public long CreatedTicks;
        public long WindowStartTicks;
        public long LastMotionTicks;
        public long LastHitCaptureTicks;

        public int MotionHits;
        public bool Recurring;
        public int DimStep;
        public bool IsForegroundIntroduction;

        public List<BoundsObservation> BoundsHistory { get; } =
            new();
    }

    private readonly FormsScreen _screen;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly ScreenSampler _sampler;

    private readonly int _columns;
    private readonly int _rows;
    private readonly int _samplesPerCell;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;

    private readonly byte[] _previousFrame;
    private readonly bool[] _rawMotion;
    private readonly bool[] _visited;
    private readonly int[] _queue;

    private readonly List<DetectedRegion>
        _detectedRegions = new();
    private readonly List<TrackedRegion>
        _trackedRegions = new();
    private readonly List<MouseSample>
        _mouseTrail = new();

    private readonly object _sync = new();
    private readonly CancellationTokenSource
        _cancellation = new();

    private Task? _captureLoop;
    private bool _renderSubscribed;
    private bool _enabled;
    private bool _hasPrevious;
    private bool _maskDirty;
    private bool _disposed;

    private long _revealAllUntilTicks;
    private long _sceneSettleUntilTicks;

    private IntPtr _lastForegroundWindow;
    private string _lastForegroundTitle =
        string.Empty;

    private bool _hasCursor;
    private bool _mouseSuppressed;
    private double _cursorX;
    private double _cursorY;
    private double _lastCursorX;
    private double _lastCursorY;
    private long _lastCursorTicks;

    public MonitorSession(
        FormsScreen screen,
        AppSettings settings)
    {
        _screen = screen;
        _settings = settings;

        var bounds =
            screen.Bounds;
        var requestedWidth =
            Math.Min(
                bounds.Width,
                settings.MotionZoneCaptureWidth);
        var requestedHeight =
            Math.Max(
                1,
                (int)Math.Round(
                    bounds.Height *
                    requestedWidth /
                    (double)Math.Max(
                        1,
                        bounds.Width)));

        _samplesPerCell =
            settings.MotionZoneSamplesPerCell;
        _columns =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    requestedWidth /
                    (double)_samplesPerCell));
        _rows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    requestedHeight /
                    (double)_samplesPerCell));

        _sampleWidth =
            checked(
                _columns *
                _samplesPerCell);
        _sampleHeight =
            checked(
                _rows *
                _samplesPerCell);
        _sampleStride =
            checked(
                _sampleWidth *
                4);

        var cellCount =
            checked(
                _columns *
                _rows);
        var frameBytes =
            checked(
                _sampleStride *
                _sampleHeight);

        _previousFrame =
            new byte[frameBytes];
        _rawMotion =
            new bool[cellCount];
        _visited =
            new bool[cellCount];
        _queue =
            new int[cellCount];

        _overlay =
            new OverlayWindow(
                screen);
        _sampler =
            new ScreenSampler(
                bounds,
                _sampleWidth,
                _sampleHeight);
    }

    public bool ExcludedFromCapture =>
        _overlay.ExcludedFromCapture;

    public void Start(
        bool enabled)
    {
        _overlay.EnsureVisible();
        SetEnabled(enabled);

        if (!_renderSubscribed)
        {
            CompositionTarget.Rendering +=
                OnRendering;
            _renderSubscribed = true;
        }

        _captureLoop =
            Task.Run(
                CaptureLoopAsync);
    }

    public void SetEnabled(
        bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _hasPrevious = false;
            _maskDirty = true;
            _revealAllUntilTicks = 0;
            _sceneSettleUntilTicks = 0;
            _lastForegroundWindow =
                IntPtr.Zero;
            _lastForegroundTitle =
                string.Empty;

            _detectedRegions.Clear();
            _trackedRegions.Clear();
            ResetInteractionCompletion();
            ResetMouseVisual();

            Array.Clear(
                _previousFrame,
                0,
                _previousFrame.Length);
            Array.Clear(
                _rawMotion,
                0,
                _rawMotion.Length);
            Array.Clear(
                _visited,
                0,
                _visited.Length);
        }

        PushScene(
            Stopwatch.GetTimestamp());
    }

    public void RevealAll(
        TimeSpan duration)
    {
        lock (_sync)
        {
            _revealAllUntilTicks =
                Stopwatch.GetTimestamp() +
                ToStopwatchTicks(
                    duration.TotalMilliseconds);
            _maskDirty = true;
        }
    }

    private async Task CaptureLoopAsync()
    {
        while (!_cancellation
                   .IsCancellationRequested)
        {
            try
            {
                bool shouldCapture;

                lock (_sync)
                {
                    shouldCapture =
                        _enabled;
                }

                if (shouldCapture)
                {
                    var foregroundWindow =
                        GetForegroundWindow();
                    var foregroundTitle =
                        GetWindowTitle(
                            foregroundWindow);

                    AnalyzeCapture(
                        _sampler.Capture(),
                        foregroundWindow,
                        foregroundTitle);
                }

                await Task.Delay(
                        _settings
                            .MotionZoneSamplingMilliseconds,
                        _cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(
                        250,
                        _cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
    }

    private void AnalyzeCapture(
        byte[] current,
        IntPtr foregroundWindow,
        string foregroundTitle)
    {
        var now =
            Stopwatch.GetTimestamp();

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            if (!_hasPrevious)
            {
                SetCaptureBaseline(
                    current,
                    foregroundWindow,
                    foregroundTitle);
                return;
            }

            var foregroundChanged =
                foregroundWindow !=
                    IntPtr.Zero &&
                _lastForegroundWindow !=
                    IntPtr.Zero &&
                foregroundWindow !=
                    _lastForegroundWindow;

            var titleChanged =
                !string.IsNullOrWhiteSpace(
                    foregroundTitle) &&
                !string.IsNullOrWhiteSpace(
                    _lastForegroundTitle) &&
                !string.Equals(
                    foregroundTitle,
                    _lastForegroundTitle,
                    StringComparison.Ordinal);

            UpdateForegroundIdentity(
                foregroundWindow,
                foregroundTitle);

            if (foregroundChanged)
            {
                ResetSceneToBaseline(
                    current,
                    now,
                    foregroundWindow,
                    revealForeground: true);
                return;
            }

            if (titleChanged)
            {
                _maskDirty = true;
            }

            if (_sceneSettleUntilTicks != 0 &&
                now <
                    _sceneSettleUntilTicks)
            {
                CopyCurrentToPrevious(
                    current);
                return;
            }

            _sceneSettleUntilTicks = 0;

            DetectMotion(current);
            BuildDetectedRegions();
            MergeNearbyDetectedRegions();

            if (IsLargeSceneChange())
            {
                ResetSceneToBaseline(
                    current,
                    now,
                    foregroundWindow,
                    revealForeground: true);
                return;
            }

            var interactionChanged =
                UpdateInteractionCompletion(
                    current,
                    now);
            var trackedChanged =
                UpdateTrackedRegions(now);

            if (interactionChanged ||
                trackedChanged)
            {
                _maskDirty = true;
            }

            CopyCurrentToPrevious(
                current);
        }
    }

    private void SetCaptureBaseline(
        byte[] current,
        IntPtr foregroundWindow,
        string foregroundTitle)
    {
        CopyCurrentToPrevious(
            current);
        _hasPrevious = true;
        _lastForegroundWindow =
            foregroundWindow;
        _lastForegroundTitle =
            foregroundTitle;

        AddForegroundIntroduction(
            foregroundWindow,
            Stopwatch.GetTimestamp());
        _maskDirty = true;
    }

    private void UpdateForegroundIdentity(
        IntPtr foregroundWindow,
        string foregroundTitle)
    {
        if (foregroundWindow !=
            IntPtr.Zero)
        {
            _lastForegroundWindow =
                foregroundWindow;
        }

        if (!string.IsNullOrWhiteSpace(
                foregroundTitle))
        {
            _lastForegroundTitle =
                foregroundTitle;
        }
    }

    private void ResetSceneToBaseline(
        byte[] current,
        long now,
        IntPtr foregroundWindow,
        bool revealForeground)
    {
        _trackedRegions.Clear();
        _detectedRegions.Clear();
        ResetInteractionCompletion();
        ResetMouseVisual();

        if (revealForeground)
        {
            AddForegroundIntroduction(
                foregroundWindow,
                now);
        }

        _sceneSettleUntilTicks =
            now +
            ToStopwatchTicks(
                _settings
                    .MotionZoneSceneSettleMilliseconds);

        CopyCurrentToPrevious(
            current);
        _maskDirty = true;
    }

    private void CopyCurrentToPrevious(
        byte[] current)
    {
        Buffer.BlockCopy(
            current,
            0,
            _previousFrame,
            0,
            current.Length);
    }

    private void DetectMotion(
        byte[] current)
    {
        Array.Clear(
            _rawMotion,
            0,
            _rawMotion.Length);

        var pixelThreshold =
            _settings
                .MotionZonePixelThreshold;
        var sampleCount =
            _samplesPerCell *
            _samplesPerCell;
        var minimumChangedSamples =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    sampleCount *
                    _settings
                        .MotionZoneChangedFraction));

        for (var row = 0;
             row < _rows;
             row++)
        {
            var sampleTop =
                row *
                _samplesPerCell;

            for (var column = 0;
                 column < _columns;
                 column++)
            {
                var sampleLeft =
                    column *
                    _samplesPerCell;
                var changedSamples = 0;
                var maximumDifference = 0;
                var differenceTotal = 0;

                for (var sampleY = 0;
                     sampleY <
                        _samplesPerCell;
                     sampleY++)
                {
                    var sourceRow =
                        sampleTop +
                        sampleY;
                    var rowOffset =
                        sourceRow *
                        _sampleStride;

                    for (var sampleX = 0;
                         sampleX <
                            _samplesPerCell;
                         sampleX++)
                    {
                        var sourceColumn =
                            sampleLeft +
                            sampleX;
                        var offset =
                            rowOffset +
                            sourceColumn *
                            4;

                        var blueDifference =
                            Math.Abs(
                                current[offset] -
                                _previousFrame[offset]);
                        var greenDifference =
                            Math.Abs(
                                current[offset + 1] -
                                _previousFrame[offset + 1]);
                        var redDifference =
                            Math.Abs(
                                current[offset + 2] -
                                _previousFrame[offset + 2]);

                        var difference =
                            Math.Max(
                                blueDifference,
                                Math.Max(
                                    greenDifference,
                                    redDifference));

                        differenceTotal +=
                            difference;
                        maximumDifference =
                            Math.Max(
                                maximumDifference,
                                difference);

                        if (difference >=
                            pixelThreshold)
                        {
                            changedSamples++;
                        }
                    }
                }

                var meanDifference =
                    differenceTotal /
                    (double)sampleCount;

                _rawMotion[
                    row *
                    _columns +
                    column] =
                    changedSamples >=
                        minimumChangedSamples ||
                    maximumDifference >=
                        pixelThreshold *
                        2 ||
                    meanDifference >=
                        pixelThreshold *
                        0.60;
            }
        }
    }

    private void BuildDetectedRegions()
    {
        _detectedRegions.Clear();

        Array.Clear(
            _visited,
            0,
            _visited.Length);

        var minimumMotionCells =
            _settings
                .MotionZoneMinimumMotionCells;
        var minimumVisibleArea =
            _settings
                .MotionZoneMinimumVisibleAreaCells;
        var padding =
            _settings
                .MotionZonePaddingCells;

        for (var start = 0;
             start <
                _rawMotion.Length;
             start++)
        {
            if (!_rawMotion[start] ||
                _visited[start])
            {
                continue;
            }

            var head = 0;
            var tail = 0;

            _queue[tail++] =
                start;
            _visited[start] =
                true;

            var minimumRow =
                start /
                _columns;
            var maximumRow =
                minimumRow;
            var minimumColumn =
                start %
                _columns;
            var maximumColumn =
                minimumColumn;
            var motionCells = 0;

            while (head <
                   tail)
            {
                var index =
                    _queue[head++];
                var row =
                    index /
                    _columns;
                var column =
                    index %
                    _columns;

                minimumRow =
                    Math.Min(
                        minimumRow,
                        row);
                maximumRow =
                    Math.Max(
                        maximumRow,
                        row);
                minimumColumn =
                    Math.Min(
                        minimumColumn,
                        column);
                maximumColumn =
                    Math.Max(
                        maximumColumn,
                        column);
                motionCells++;

                for (var offsetY = -1;
                     offsetY <= 1;
                     offsetY++)
                {
                    var neighbourRow =
                        row +
                        offsetY;

                    if (neighbourRow < 0 ||
                        neighbourRow >=
                            _rows)
                    {
                        continue;
                    }

                    for (var offsetX = -1;
                         offsetX <= 1;
                         offsetX++)
                    {
                        if (offsetX == 0 &&
                            offsetY == 0)
                        {
                            continue;
                        }

                        var neighbourColumn =
                            column +
                            offsetX;

                        if (neighbourColumn < 0 ||
                            neighbourColumn >=
                                _columns)
                        {
                            continue;
                        }

                        var neighbourIndex =
                            neighbourRow *
                            _columns +
                            neighbourColumn;

                        if (_visited[
                                neighbourIndex] ||
                            !_rawMotion[
                                neighbourIndex])
                        {
                            continue;
                        }

                        _visited[
                            neighbourIndex] =
                            true;
                        _queue[tail++] =
                            neighbourIndex;
                    }
                }
            }

            if (motionCells <
                minimumMotionCells ||
                !IsMeaningfulOutputRegion(
                    minimumRow,
                    maximumRow,
                    minimumColumn,
                    maximumColumn))
            {
                continue;
            }

            minimumRow =
                Math.Max(
                    0,
                    minimumRow -
                    padding);
            maximumRow =
                Math.Min(
                    _rows - 1,
                    maximumRow +
                    padding);
            minimumColumn =
                Math.Max(
                    0,
                    minimumColumn -
                    padding);
            maximumColumn =
                Math.Min(
                    _columns - 1,
                    maximumColumn +
                    padding);

            var region =
                new DetectedRegion(
                    minimumRow,
                    maximumRow,
                    minimumColumn,
                    maximumColumn,
                    motionCells);

            if (region.Area <
                minimumVisibleArea)
            {
                continue;
            }

            _detectedRegions.Add(
                region);
        }
    }

    private void MergeNearbyDetectedRegions()
    {
        var maximumGap =
            _settings
                .MotionZoneRenderMergeGapCells;

        if (maximumGap <= 0 ||
            _detectedRegions.Count < 2)
        {
            return;
        }

        var merged = true;

        while (merged)
        {
            merged = false;

            for (var firstIndex = 0;
                 firstIndex <
                    _detectedRegions.Count;
                 firstIndex++)
            {
                for (var secondIndex =
                         firstIndex + 1;
                     secondIndex <
                        _detectedRegions.Count;
                     secondIndex++)
                {
                    var first =
                        _detectedRegions[
                            firstIndex];
                    var second =
                        _detectedRegions[
                            secondIndex];

                    if (!ShouldMergeDetected(
                            first,
                            second,
                            maximumGap))
                    {
                        continue;
                    }

                    var union =
                        Union(
                            first,
                            second);
                    var occupiedArea =
                        first.Area +
                        second.Area;
                    var inflation =
                        union.Area /
                        (double)Math.Max(
                            1,
                            occupiedArea);

                    if (inflation >
                        1.65)
                    {
                        continue;
                    }

                    _detectedRegions[
                        firstIndex] =
                        union;
                    _detectedRegions.RemoveAt(
                        secondIndex);
                    merged = true;
                    break;
                }

                if (merged)
                {
                    break;
                }
            }
        }
    }

    private static bool ShouldMergeDetected(
        DetectedRegion first,
        DetectedRegion second,
        int maximumGap)
    {
        var rowOverlap =
            OverlapLength(
                first.MinimumRow,
                first.MaximumRow,
                second.MinimumRow,
                second.MaximumRow);
        var columnOverlap =
            OverlapLength(
                first.MinimumColumn,
                first.MaximumColumn,
                second.MinimumColumn,
                second.MaximumColumn);
        var rowGap =
            AxisGap(
                first.MinimumRow,
                first.MaximumRow,
                second.MinimumRow,
                second.MaximumRow);
        var columnGap =
            AxisGap(
                first.MinimumColumn,
                first.MaximumColumn,
                second.MinimumColumn,
                second.MaximumColumn);

        if (rowOverlap > 0 &&
            columnGap <=
                maximumGap)
        {
            return true;
        }

        if (columnOverlap > 0 &&
            rowGap <=
                maximumGap)
        {
            return true;
        }

        return rowGap <= 1 &&
               columnGap <= 1;
    }

    private bool IsLargeSceneChange()
    {
        var changedCells = 0;

        for (var index = 0;
             index <
                _rawMotion.Length;
             index++)
        {
            if (_rawMotion[index])
            {
                changedCells++;
            }
        }

        if (changedCells == 0)
        {
            return false;
        }

        var changedFraction =
            changedCells /
            (double)_rawMotion.Length;

        if (changedFraction <
            _settings
                .MotionZoneSceneChangeFraction)
        {
            return false;
        }

        var largestDetectedArea =
            _detectedRegions.Count == 0
                ? 0
                : _detectedRegions.Max(
                    region =>
                        region.Area);

        if (_trackedRegions.Count == 0)
        {
            return changedFraction >=
                       Math.Max(
                           0.20,
                           _settings
                               .MotionZoneSceneChangeFraction *
                           1.70) ||
                   largestDetectedArea >=
                       _rawMotion.Length *
                       0.35;
        }

        var changedInsideTracked = 0;

        for (var row = 0;
             row < _rows;
             row++)
        {
            for (var column = 0;
                 column < _columns;
                 column++)
            {
                var index =
                    row *
                    _columns +
                    column;

                if (!_rawMotion[index])
                {
                    continue;
                }

                if (IsCellInsideTrackedRegion(
                        row,
                        column))
                {
                    changedInsideTracked++;
                }
            }
        }

        var overlapFraction =
            changedInsideTracked /
            (double)changedCells;

        return overlapFraction <
            _settings
                .MotionZoneSceneChangeOverlapFraction;
    }

    private bool IsCellInsideTrackedRegion(
        int row,
        int column)
    {
        foreach (var region in
                 _trackedRegions)
        {
            if (row >=
                    region.MinimumRow &&
                row <=
                    region.MaximumRow &&
                column >=
                    region.MinimumColumn &&
                column <=
                    region.MaximumColumn)
            {
                return true;
            }
        }

        return false;
    }

    private bool UpdateTrackedRegions(
        long now)
    {
        var changed = false;
        var recurringWindowTicks =
            ToStopwatchTicks(
                _settings
                    .MotionZoneRecurringWindowMilliseconds);
        var recurringMinimumSpanTicks =
            ToStopwatchTicks(
                _settings
                    .MotionZoneRecurringMinimumSpanMilliseconds);
        var trackingGap =
            _settings
                .MotionZoneTrackingGapCells;

        foreach (var detected in
                 _detectedRegions)
        {
            var detectedArea =
                Math.Max(
                    1,
                    detected.Area);
            TrackedRegion? best =
                null;
            var bestScore =
                double.NegativeInfinity;

            foreach (var tracked in
                     _trackedRegions)
            {
                if (tracked.IsForegroundIntroduction)
                {
                    continue;
                }

                // One tracked component may consume only one detected
                // component per capture. This prevents unrelated details
                // from being swallowed into one large rectangle.
                if (tracked.LastHitCaptureTicks ==
                    now)
                {
                    continue;
                }

                if (!RectanglesNear(
                        tracked.MinimumRow,
                        tracked.MaximumRow,
                        tracked.MinimumColumn,
                        tracked.MaximumColumn,
                        detected.MinimumRow,
                        detected.MaximumRow,
                        detected.MinimumColumn,
                        detected.MaximumColumn,
                        trackingGap))
                {
                    continue;
                }

                var trackedArea =
                    Math.Max(
                        1,
                        RectangleArea(
                            tracked.MinimumRow,
                            tracked.MaximumRow,
                            tracked.MinimumColumn,
                            tracked.MaximumColumn));
                var intersection =
                    IntersectionArea(
                        tracked.MinimumRow,
                        tracked.MaximumRow,
                        tracked.MinimumColumn,
                        tracked.MaximumColumn,
                        detected.MinimumRow,
                        detected.MaximumRow,
                        detected.MinimumColumn,
                        detected.MaximumColumn);
                var detectedInside =
                    intersection /
                    (double)detectedArea;
                var trackedInside =
                    intersection /
                    (double)trackedArea;
                var sizeRatio =
                    Math.Min(
                        trackedArea,
                        detectedArea) /
                    (double)Math.Max(
                        trackedArea,
                        detectedArea);

                // A small icon inside an old large rectangle is an
                // independent component. It must not wake the large one.
                if (detectedArea <
                        trackedArea *
                        0.35 &&
                    trackedInside <
                        0.35)
                {
                    continue;
                }

                // During staged dimming, only substantial motion may
                // reactivate the complete old component.
                if (tracked.DimStep > 0 &&
                    sizeRatio <
                        0.55 &&
                    trackedInside <
                        0.45)
                {
                    continue;
                }

                var distance =
                    AxisGap(
                        tracked.MinimumRow,
                        tracked.MaximumRow,
                        detected.MinimumRow,
                        detected.MaximumRow) +
                    AxisGap(
                        tracked.MinimumColumn,
                        tracked.MaximumColumn,
                        detected.MinimumColumn,
                        detected.MaximumColumn);

                if (intersection == 0 &&
                    distance >
                        Math.Max(
                            1,
                            trackingGap / 2))
                {
                    continue;
                }

                var score =
                    intersection *
                        1000.0 +
                    detectedInside *
                        400.0 +
                    trackedInside *
                        260.0 +
                    sizeRatio *
                        160.0 -
                    distance *
                        60.0;

                if (score >
                    bestScore)
                {
                    best = tracked;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                var created =
                    new TrackedRegion
                    {
                        MinimumRow =
                            detected.MinimumRow,
                        MaximumRow =
                            detected.MaximumRow,
                        MinimumColumn =
                            detected.MinimumColumn,
                        MaximumColumn =
                            detected.MaximumColumn,
                        CreatedTicks = now,
                        WindowStartTicks = now,
                        LastMotionTicks = now,
                        LastHitCaptureTicks = now,
                        MotionHits = 1,
                        Recurring = false,
                        DimStep = 0
                    };

                created.BoundsHistory.Add(
                    new BoundsObservation(
                        detected.MinimumRow,
                        detected.MaximumRow,
                        detected.MinimumColumn,
                        detected.MaximumColumn,
                        now));

                _trackedRegions.Add(
                    created);

                changed = true;
                continue;
            }

            var oldMinimumRow =
                best.MinimumRow;
            var oldMaximumRow =
                best.MaximumRow;
            var oldMinimumColumn =
                best.MinimumColumn;
            var oldMaximumColumn =
                best.MaximumColumn;
            var oldDimStep =
                best.DimStep;

            RecordBoundsObservation(
                best,
                detected,
                now);

            ExpandStableBounds(
                best,
                detected,
                now);

            ContractStaleBounds(
                best,
                now);

            RefreshTrackedRegion(
                best,
                now,
                recurringWindowTicks,
                recurringMinimumSpanTicks);

            if (best.MinimumRow !=
                    oldMinimumRow ||
                best.MaximumRow !=
                    oldMaximumRow ||
                best.MinimumColumn !=
                    oldMinimumColumn ||
                best.MaximumColumn !=
                    oldMaximumColumn ||
                oldDimStep != 0)
            {
                changed = true;
            }
        }

        return changed;
    }

    private void RecordBoundsObservation(
        TrackedRegion tracked,
        DetectedRegion detected,
        long now)
    {
        var detectedArea =
            Math.Max(
                1,
                detected.Area);
        var intersection =
            IntersectionArea(
                tracked.MinimumRow,
                tracked.MaximumRow,
                tracked.MinimumColumn,
                tracked.MaximumColumn,
                detected.MinimumRow,
                detected.MaximumRow,
                detected.MinimumColumn,
                detected.MaximumColumn);
        var detectedInside =
            intersection /
            (double)detectedArea;
        var expandsBounds =
            detected.MinimumRow <
                tracked.MinimumRow ||
            detected.MaximumRow >
                tracked.MaximumRow ||
            detected.MinimumColumn <
                tracked.MinimumColumn ||
            detected.MaximumColumn >
                tracked.MaximumColumn;

        // A hover fragment that the existing protection refuses to absorb
        // must not pollute the history used for later contraction either.
        if (tracked.Recurring &&
            expandsBounds &&
            detectedInside < 0.85 &&
            IsCursorNearDetectedRegion(
                detected,
                Math.Max(
                    12,
                    _settings
                        .MouseVisualRadiusPixels +
                    8)))
        {
            return;
        }

        tracked.BoundsHistory.Add(
            new BoundsObservation(
                detected.MinimumRow,
                detected.MaximumRow,
                detected.MinimumColumn,
                detected.MaximumColumn,
                now));

        var cutoff =
            now -
            ToStopwatchTicks(
                1_000);

        while (tracked.BoundsHistory.Count > 0 &&
               tracked.BoundsHistory[0]
                       .Timestamp <
                   cutoff)
        {
            tracked.BoundsHistory.RemoveAt(
                0);
        }

        if (tracked.BoundsHistory.Count > 64)
        {
            tracked.BoundsHistory.RemoveRange(
                0,
                tracked.BoundsHistory.Count -
                64);
        }
    }

    private void ContractStaleBounds(
        TrackedRegion tracked,
        long now)
    {
        if (!tracked.Recurring ||
            tracked.BoundsHistory.Count < 6)
        {
            return;
        }

        var oldest =
            tracked.BoundsHistory[0];

        if (now -
                oldest.Timestamp <
            ToStopwatchTicks(
                700))
        {
            return;
        }

        var minimumRow =
            int.MaxValue;
        var maximumRow =
            int.MinValue;
        var minimumColumn =
            int.MaxValue;
        var maximumColumn =
            int.MinValue;

        foreach (var observation in
                 tracked.BoundsHistory)
        {
            minimumRow =
                Math.Min(
                    minimumRow,
                    observation.MinimumRow);
            maximumRow =
                Math.Max(
                    maximumRow,
                    observation.MaximumRow);
            minimumColumn =
                Math.Min(
                    minimumColumn,
                    observation.MinimumColumn);
            maximumColumn =
                Math.Max(
                    maximumColumn,
                    observation.MaximumColumn);
        }

        if (minimumRow == int.MaxValue)
        {
            return;
        }

        var currentArea =
            Math.Max(
                1,
                RectangleArea(
                    tracked.MinimumRow,
                    tracked.MaximumRow,
                    tracked.MinimumColumn,
                    tracked.MaximumColumn));
        var recentArea =
            Math.Max(
                1,
                RectangleArea(
                    minimumRow,
                    maximumRow,
                    minimumColumn,
                    maximumColumn));
        var removesVisibleEdge =
            minimumRow >=
                tracked.MinimumRow +
                2 ||
            maximumRow <=
                tracked.MaximumRow -
                2 ||
            minimumColumn >=
                tracked.MinimumColumn +
                2 ||
            maximumColumn <=
                tracked.MaximumColumn -
                2;

        if (!removesVisibleEdge ||
            recentArea >
                currentArea *
                0.94)
        {
            return;
        }

        tracked.MinimumRow =
            Math.Max(
                tracked.MinimumRow,
                minimumRow);
        tracked.MaximumRow =
            Math.Min(
                tracked.MaximumRow,
                maximumRow);
        tracked.MinimumColumn =
            Math.Max(
                tracked.MinimumColumn,
                minimumColumn);
        tracked.MaximumColumn =
            Math.Min(
                tracked.MaximumColumn,
                maximumColumn);
    }

    private void ExpandStableBounds(
        TrackedRegion tracked,
        DetectedRegion detected,
        long now)
    {
        var trackedArea =
            Math.Max(
                1,
                RectangleArea(
                    tracked.MinimumRow,
                    tracked.MaximumRow,
                    tracked.MinimumColumn,
                    tracked.MaximumColumn));
        var detectedArea =
            Math.Max(
                1,
                detected.Area);
        var intersection =
            IntersectionArea(
                tracked.MinimumRow,
                tracked.MaximumRow,
                tracked.MinimumColumn,
                tracked.MaximumColumn,
                detected.MinimumRow,
                detected.MaximumRow,
                detected.MinimumColumn,
                detected.MaximumColumn);
        var detectedInside =
            intersection /
            (double)detectedArea;
        var trackedInside =
            intersection /
            (double)trackedArea;
        var sizeRatio =
            Math.Min(
                trackedArea,
                detectedArea) /
            (double)Math.Max(
                trackedArea,
                detectedArea);
        var ageTicks =
            now -
            tracked.CreatedTicks;
        var learningTicks =
            ToStopwatchTicks(
                700);
        var expandsBounds =
            detected.MinimumRow <
                tracked.MinimumRow ||
            detected.MaximumRow >
                tracked.MaximumRow ||
            detected.MinimumColumn <
                tracked.MinimumColumn ||
            detected.MaximumColumn >
                tracked.MaximumColumn;

        // A validated zone may still follow real content, but a hover fragment
        // beside the cursor must not enlarge its permanent rectangle.
        if (tracked.Recurring &&
            expandsBounds &&
            detectedInside < 0.85 &&
            IsCursorNearDetectedRegion(
                detected,
                Math.Max(
                    12,
                    _settings
                        .MouseVisualRadiusPixels +
                    8)))
        {
            return;
        }

        if (ageTicks >
                learningTicks &&
            sizeRatio <
                0.35 &&
            trackedInside <
                0.25)
        {
            return;
        }

        var minimumRow =
            Math.Min(
                tracked.MinimumRow,
                detected.MinimumRow);
        var maximumRow =
            Math.Max(
                tracked.MaximumRow,
                detected.MaximumRow);
        var minimumColumn =
            Math.Min(
                tracked.MinimumColumn,
                detected.MinimumColumn);
        var maximumColumn =
            Math.Max(
                tracked.MaximumColumn,
                detected.MaximumColumn);
        var unionArea =
            RectangleArea(
                minimumRow,
                maximumRow,
                minimumColumn,
                maximumColumn);
        var maximumGrowth =
            tracked.Recurring
                ? 1.35
                : ageTicks <=
                    learningTicks
                    ? 2.50
                    : 1.75;

        if (unionArea >
            trackedArea *
            maximumGrowth)
        {
            return;
        }

        tracked.MinimumRow =
            minimumRow;
        tracked.MaximumRow =
            maximumRow;
        tracked.MinimumColumn =
            minimumColumn;
        tracked.MaximumColumn =
            maximumColumn;
    }

    private bool IsCursorNearDetectedRegion(
        DetectedRegion detected,
        double marginPixels)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        var bounds = _screen.Bounds;

        if (!bounds.Contains(cursor.X, cursor.Y))
        {
            return false;
        }

        var column = Math.Clamp(
            (cursor.X - bounds.Left) * _columns /
            Math.Max(1, bounds.Width),
            0,
            _columns - 1);
        var row = Math.Clamp(
            (cursor.Y - bounds.Top) * _rows /
            Math.Max(1, bounds.Height),
            0,
            _rows - 1);
        var columnMargin = Math.Max(
            1,
            (int)Math.Ceiling(
                marginPixels * _columns /
                Math.Max(1.0, bounds.Width)));
        var rowMargin = Math.Max(
            1,
            (int)Math.Ceiling(
                marginPixels * _rows /
                Math.Max(1.0, bounds.Height)));

        return column >= detected.MinimumColumn - columnMargin &&
               column <= detected.MaximumColumn + columnMargin &&
               row >= detected.MinimumRow - rowMargin &&
               row <= detected.MaximumRow + rowMargin;
    }

    private void RefreshTrackedRegion(
        TrackedRegion region,
        long now,
        long recurringWindowTicks,
        long recurringMinimumSpanTicks)
    {
        region.LastMotionTicks = now;
        region.LastHitCaptureTicks = now;
        region.DimStep = 0;

        if (region.WindowStartTicks == 0 ||
            now -
                region.WindowStartTicks >
                recurringWindowTicks)
        {
            region.WindowStartTicks =
                now;
            region.MotionHits = 1;
        }
        else
        {
            region.MotionHits =
                Math.Min(
                    int.MaxValue,
                    region.MotionHits + 1);
        }

        if (!region.Recurring &&
            region.MotionHits >=
                _settings
                    .MotionZoneRecurringHits &&
            now -
                region.WindowStartTicks >=
                recurringMinimumSpanTicks)
        {
            region.Recurring = true;
        }
    }

    private bool IsCompactTransientRegion(
        TrackedRegion region)
    {
        if (region.IsForegroundIntroduction)
        {
            return false;
        }

        var bounds =
            _screen.Bounds;
        var widthPixels =
            (region.MaximumColumn -
             region.MinimumColumn +
             1) *
            bounds.Width /
            (double)Math.Max(
                1,
                _columns);
        var heightPixels =
            (region.MaximumRow -
             region.MinimumRow +
             1) *
            bounds.Height /
            (double)Math.Max(
                1,
                _rows);
        var areaPixels =
            widthPixels *
            heightPixels;
        var screenArea =
            Math.Max(
                1.0,
                (double)bounds.Width *
                bounds.Height);
        var compactLimit =
            Math.Max(
                18_000.0,
                screenArea *
                0.0125);
        var thinControl =
            Math.Min(
                widthPixels,
                heightPixels) <=
                46.0 &&
            areaPixels <=
                compactLimit *
                2.5;

        return areaPixels <=
                   compactLimit ||
               thinControl;
    }

    private bool UpdateRegionVisualStates(
        long now)
    {
        var changed = false;
        var oneShotTicks =
            ToStopwatchTicks(
                _settings
                    .MotionZoneOneShotHoldMilliseconds);
        var recurringTicks =
            ToStopwatchTicks(
                _settings
                    .MotionZoneRecurringHoldMilliseconds);
        var recurringDimDurationTicks =
            ToStopwatchTicks(
                _settings
                    .MotionZoneDimDurationMilliseconds);
        var transientDimDurationTicks =
            ToStopwatchTicks(
                _settings
                    .MotionZoneTransientFadeMilliseconds);
        var foregroundRevealTicks =
            ToStopwatchTicks(
                _settings
                    .ForegroundWindowRevealMilliseconds);
        var foregroundFadeTicks =
            ToStopwatchTicks(
                _settings
                    .ForegroundWindowFadeMilliseconds);
        var dimSteps =
            Math.Max(
                2,
                _settings
                    .MotionZoneDimSteps);

        for (var index =
                 _trackedRegions.Count - 1;
             index >= 0;
             index--)
        {
            var region =
                _trackedRegions[index];
            var useRecurringHold =
                region.Recurring &&
                !IsCompactTransientRegion(
                    region);
            var holdTicks =
                region.IsForegroundIntroduction
                    ? foregroundRevealTicks
                    : useRecurringHold
                        ? recurringTicks
                        : oneShotTicks;
            var dimDurationTicks =
                region.IsForegroundIntroduction
                    ? foregroundFadeTicks
                    : useRecurringHold
                        ? recurringDimDurationTicks
                        : transientDimDurationTicks;
            var elapsed =
                now -
                region.LastMotionTicks;

            if (elapsed < holdTicks)
            {
                if (region.DimStep != 0)
                {
                    region.DimStep = 0;
                    changed = true;
                }

                continue;
            }

            if (dimDurationTicks <= 0 ||
                elapsed >= holdTicks + dimDurationTicks)
            {
                _trackedRegions.RemoveAt(index);
                changed = true;
                continue;
            }

            var fadeElapsed = elapsed - holdTicks;
            var targetStep =
                Math.Clamp(
                    1 +
                    (int)(
                        fadeElapsed *
                        dimSteps /
                        Math.Max(1L, dimDurationTicks)),
                    1,
                    dimSteps);

            if (targetStep != region.DimStep)
            {
                region.DimStep = targetStep;
                changed = true;
            }
        }

        return changed;
    }

    private void OnRendering(
        object? sender,
        EventArgs e)
    {
        var now =
            Stopwatch.GetTimestamp();
        var shouldPush = false;

        lock (_sync)
        {
            var visualChanged =
                UpdateRegionVisualStates(
                    now);
            var interactionChanged =
                UpdateInteractionVisualState(
                    now);
            var mouseChanged =
                UpdateMouseVisual(
                    now);
            var revealAllExpired =
                _revealAllUntilTicks != 0 &&
                now >=
                    _revealAllUntilTicks;

            if (revealAllExpired)
            {
                _revealAllUntilTicks = 0;
            }

            shouldPush =
                _maskDirty ||
                visualChanged ||
                interactionChanged ||
                mouseChanged ||
                revealAllExpired;

            _maskDirty = false;
        }

        if (shouldPush)
        {
            PushScene(now);
        }
    }

    private bool UpdateMouseVisual(
        long now)
    {
        if (!_enabled ||
            !_settings.MouseVisualEnabled ||
            !NativeMethods.GetCursorPos(
                out var cursor))
        {
            return ResetMouseVisual();
        }

        var bounds =
            _screen.Bounds;

        if (!bounds.Contains(
                cursor.X,
                cursor.Y))
        {
            return ResetMouseVisual();
        }

        var localX =
            cursor.X -
            bounds.Left;
        var localY =
            cursor.Y -
            bounds.Top;
        var suppressTrail =
            IsPointInsideClearRegion(
                localX,
                localY);
        var suppressionChanged =
            _mouseSuppressed !=
            suppressTrail;
        _mouseSuppressed =
            suppressTrail;

        var changedPosition =
            !_hasCursor ||
            Math.Abs(
                localX -
                _cursorX) >= 0.5 ||
            Math.Abs(
                localY -
                _cursorY) >= 0.5;

        if (!_hasCursor)
        {
            _hasCursor = true;
            _cursorX = localX;
            _cursorY = localY;
            _lastCursorX = localX;
            _lastCursorY = localY;
            _lastCursorTicks = now;
            return true;
        }

        if (changedPosition)
        {
            if (_mouseSuppressed)
            {
                _mouseTrail.Clear();
            }
            else
            {
                AddInterpolatedMouseSamples(
                    _cursorX,
                    _cursorY,
                    localX,
                    localY,
                    _lastCursorTicks,
                    now);
            }

            _lastCursorX =
                _cursorX;
            _lastCursorY =
                _cursorY;
            _cursorX =
                localX;
            _cursorY =
                localY;
            _lastCursorTicks =
                now;
        }

        var clearedSuppressedTrail =
            false;

        if (_mouseSuppressed &&
            _mouseTrail.Count > 0)
        {
            _mouseTrail.Clear();
            clearedSuppressedTrail = true;
        }

        var trailChanged =
            PruneMouseTrail(now);

        // The current cursor reveal remains visible over partial controls.
        // Only the decorative trail is suppressed inside an existing hole.
        return suppressionChanged ||
               changedPosition ||
               clearedSuppressedTrail ||
               trailChanged ||
               _mouseTrail.Count > 0;
    }

    private void AddInterpolatedMouseSamples(
        double startX,
        double startY,
        double endX,
        double endY,
        long startTicks,
        long endTicks)
    {
        if (_settings.MouseTrailMilliseconds <= 0)
        {
            _mouseTrail.Clear();
            return;
        }

        var distance =
            Math.Sqrt(
                Math.Pow(
                    endX -
                    startX,
                    2) +
                Math.Pow(
                    endY -
                    startY,
                    2));
        var steps =
            Math.Clamp(
                (int)Math.Ceiling(
                    distance /
                    Math.Max(
                        1,
                        _settings
                            .MouseTrailSpacingPixels)),
                1,
                12);

        for (var step = 1;
             step <= steps;
             step++)
        {
            var ratio =
                step /
                (double)steps;
            var x =
                startX +
                (endX -
                 startX) *
                ratio;
            var y =
                startY +
                (endY -
                 startY) *
                ratio;
            var timestamp =
                startTicks +
                (long)(
                    (endTicks -
                     startTicks) *
                    ratio);

            _mouseTrail.Add(
                new MouseSample(
                    x,
                    y,
                    timestamp));
        }

        if (_mouseTrail.Count > 72)
        {
            _mouseTrail.RemoveRange(
                0,
                _mouseTrail.Count -
                72);
        }
    }

    private bool PruneMouseTrail(
        long now)
    {
        if (_mouseTrail.Count == 0)
        {
            return false;
        }

        var lifetimeTicks =
            ToStopwatchTicks(
                _settings
                    .MouseTrailMilliseconds);
        var removed = false;

        while (_mouseTrail.Count > 0 &&
               now -
                   _mouseTrail[0]
                       .Timestamp >=
                   lifetimeTicks)
        {
            _mouseTrail.RemoveAt(0);
            removed = true;
        }

        return removed;
    }

    private bool IsPointInsideClearRegion(
        double localX,
        double localY)
    {
        var bounds =
            _screen.Bounds;
        var column =
            Math.Clamp(
                (int)(
                    localX *
                    _columns /
                    Math.Max(
                        1,
                        bounds.Width)),
                0,
                _columns - 1);
        var row =
            Math.Clamp(
                (int)(
                    localY *
                    _rows /
                    Math.Max(
                        1,
                        bounds.Height)),
                0,
                _rows - 1);

        foreach (var region in
                 _trackedRegions)
        {
            if (region.DimStep != 0)
            {
                continue;
            }

            if (row >=
                    region.MinimumRow &&
                row <=
                    region.MaximumRow &&
                column >=
                    region.MinimumColumn &&
                column <=
                    region.MaximumColumn)
            {
                return true;
            }
        }

        return false;
    }

    private bool ResetMouseVisual()
    {
        var changed =
            _hasCursor ||
            _mouseTrail.Count > 0 ||
            _mouseSuppressed;

        _hasCursor = false;
        _mouseSuppressed = false;
        _cursorX = 0;
        _cursorY = 0;
        _lastCursorX = 0;
        _lastCursorY = 0;
        _lastCursorTicks = 0;
        _mouseTrail.Clear();

        return changed;
    }

    private void PushScene(
        long now)
    {
        List<MaskRegion> regions;
        List<MouseReveal> mouseReveals;
        double maximumOpacity;

        lock (_sync)
        {
            var revealAll =
                !_enabled ||
                (_revealAllUntilTicks != 0 &&
                 now <
                    _revealAllUntilTicks);

            maximumOpacity =
                revealAll
                    ? 0.0
                    : _settings
                        .MaximumMaskOpacity;

            regions =
                revealAll
                    ? new List<MaskRegion>()
                    : BuildMaskRegions();

            mouseReveals =
                revealAll
                    ? new List<MouseReveal>()
                    : BuildMouseReveals(
                        now);
        }

        _overlay.SetScene(
            maximumOpacity,
            regions,
            mouseReveals);
    }

    private List<MaskRegion> BuildMaskRegions()
    {
        var renderRegions =
            BuildMergedRenderRegions();
        var result =
            new List<MaskRegion>(
                renderRegions.Count);
        var dimSteps =
            Math.Max(
                2,
                _settings
                    .MotionZoneDimSteps);
        var maximumOpacity =
            _settings
                .MaximumMaskOpacity;

        foreach (var region in
                 renderRegions)
        {
            var left =
                region.MinimumColumn /
                (double)_columns;
            var top =
                region.MinimumRow /
                (double)_rows;
            var right =
                (region.MaximumColumn + 1) /
                (double)_columns;
            var bottom =
                (region.MaximumRow + 1) /
                (double)_rows;
            var opacity =
                maximumOpacity *
                Math.Clamp(
                    region.DimStep,
                    0,
                    dimSteps) /
                dimSteps;

            result.Add(
                new MaskRegion(
                    new Rect(
                        left,
                        top,
                        right - left,
                        bottom - top),
                    opacity));
        }

        PolishRenderMaskRegions(
            result);
        AppendManualRevealZones(
            result);
        AppendInteractionReveal(
            result);

        return result;
    }

    private List<MouseReveal> BuildMouseReveals(
        long now)
    {
        var result =
            new List<MouseReveal>();

        if (!_settings.MouseVisualEnabled ||
            !_hasCursor)
        {
            return result;
        }

        var bounds =
            _screen.Bounds;
        var baseRadius =
            _settings
                .MouseVisualRadiusPixels;

        result.Add(
            CreateMouseReveal(
                _cursorX,
                _cursorY,
                baseRadius,
                bounds));

        if (_mouseSuppressed)
        {
            return result;
        }

        var lifetimeMilliseconds =
            _settings
                .MouseTrailMilliseconds;

        if (lifetimeMilliseconds <= 0)
        {
            return result;
        }

        var lifetimeTicks =
            Math.Max(
                1L,
                ToStopwatchTicks(
                    lifetimeMilliseconds));

        foreach (var sample in
                 _mouseTrail)
        {
            var age =
                now -
                sample.Timestamp;

            if (age < 0 ||
                age >=
                    lifetimeTicks)
            {
                continue;
            }

            var life =
                1.0 -
                age /
                (double)lifetimeTicks;
            var radius =
                baseRadius *
                (0.35 +
                 0.65 *
                 life);

            result.Add(
                CreateMouseReveal(
                    sample.X,
                    sample.Y,
                    radius,
                    bounds));
        }

        return result;
    }

    private static MouseReveal CreateMouseReveal(
        double localX,
        double localY,
        double radius,
        System.Drawing.Rectangle bounds)
    {
        return new MouseReveal(
            new System.Windows.Point(
                localX /
                Math.Max(
                    1.0,
                    bounds.Width),
                localY /
                Math.Max(
                    1.0,
                    bounds.Height)),
            radius /
            Math.Max(
                1.0,
                bounds.Width),
            radius /
            Math.Max(
                1.0,
                bounds.Height));
    }

    private static DetectedRegion Union(
        DetectedRegion first,
        DetectedRegion second)
    {
        return new DetectedRegion(
            Math.Min(
                first.MinimumRow,
                second.MinimumRow),
            Math.Max(
                first.MaximumRow,
                second.MaximumRow),
            Math.Min(
                first.MinimumColumn,
                second.MinimumColumn),
            Math.Max(
                first.MaximumColumn,
                second.MaximumColumn),
            first.MotionCells +
            second.MotionCells);
    }

    private static int RectangleArea(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn)
    {
        return Math.Max(
                   0,
                   maximumRow -
                   minimumRow +
                   1) *
               Math.Max(
                   0,
                   maximumColumn -
                   minimumColumn +
                   1);
    }

    private static int IntersectionArea(
        int firstMinimumRow,
        int firstMaximumRow,
        int firstMinimumColumn,
        int firstMaximumColumn,
        int secondMinimumRow,
        int secondMaximumRow,
        int secondMinimumColumn,
        int secondMaximumColumn)
    {
        var rows =
            OverlapLength(
                firstMinimumRow,
                firstMaximumRow,
                secondMinimumRow,
                secondMaximumRow);
        var columns =
            OverlapLength(
                firstMinimumColumn,
                firstMaximumColumn,
                secondMinimumColumn,
                secondMaximumColumn);

        return rows *
               columns;
    }

    private static int OverlapLength(
        int firstMinimum,
        int firstMaximum,
        int secondMinimum,
        int secondMaximum)
    {
        return Math.Max(
            0,
            Math.Min(
                firstMaximum,
                secondMaximum) -
            Math.Max(
                firstMinimum,
                secondMinimum) +
            1);
    }

    private static bool RectanglesNear(
        int firstMinimumRow,
        int firstMaximumRow,
        int firstMinimumColumn,
        int firstMaximumColumn,
        int secondMinimumRow,
        int secondMaximumRow,
        int secondMinimumColumn,
        int secondMaximumColumn,
        int maximumGap)
    {
        return AxisGap(
                   firstMinimumRow,
                   firstMaximumRow,
                   secondMinimumRow,
                   secondMaximumRow) <=
               maximumGap &&
               AxisGap(
                   firstMinimumColumn,
                   firstMaximumColumn,
                   secondMinimumColumn,
                   secondMaximumColumn) <=
               maximumGap;
    }

    private static int AxisGap(
        int firstMinimum,
        int firstMaximum,
        int secondMinimum,
        int secondMaximum)
    {
        if (firstMaximum <
            secondMinimum)
        {
            return secondMinimum -
                   firstMaximum -
                   1;
        }

        if (secondMaximum <
            firstMinimum)
        {
            return firstMinimum -
                   secondMaximum -
                   1;
        }

        return 0;
    }

    private static long ToStopwatchTicks(
        double milliseconds)
    {
        return (long)(
            milliseconds *
            Stopwatch.Frequency /
            1000.0);
    }

    private static string GetWindowTitle(
        IntPtr window)
    {
        if (window ==
            IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer =
            new StringBuilder(
                512);
        var length =
            GetWindowText(
                window,
                buffer,
                buffer.Capacity);

        return length > 0
            ? buffer.ToString()
            : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr
        GetForegroundWindow();

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int
        GetWindowText(
            IntPtr window,
            StringBuilder text,
            int maximumCharacters);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        if (_renderSubscribed)
        {
            CompositionTarget.Rendering -=
                OnRendering;
            _renderSubscribed = false;
        }

        _sampler.Dispose();
        _overlay.Close();
        _cancellation.Dispose();
    }
}
