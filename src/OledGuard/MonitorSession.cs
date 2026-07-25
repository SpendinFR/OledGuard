using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;
using WpfRect = System.Windows.Rect;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private const int CaptureMilliseconds = 20;
    private const int CellSize = 6;

    private const int TransientHoldMilliseconds = 3_000;
    private const int ValidationOneMilliseconds = 1_000;
    private const int ValidationThreeMilliseconds = 3_000;
    private const int ValidationFiveMilliseconds = 5_000;
    private const int ValidatedHoldMilliseconds = 30_000;
    private const int FadeMilliseconds = 300;
    private const int ResizeMilliseconds = 500;
    private const int ContinuousGapMilliseconds = 700;

    private const int CursorComponentHoldMilliseconds = 3_000;
    private const int CursorComponentNearPixels = 24;
    private const int MaximumZoneCount = 48;

    private sealed class ActivityZone
    {
        public DrawingRectangle Bounds;
        public DrawingRectangle RecentBounds;
        public bool HasRecentBounds;

        public long ContinuousStartTicks;
        public long LastActivityTicks;
        public long LastResizeTicks;

        public bool ConfirmedAtOneSecond;
        public bool ConfirmedAtThreeSeconds;
        public bool Validated;
    }

    private readonly record struct MotionComponent(
        DrawingRectangle Bounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly FormsScreen _screen;
    private readonly AppSettings _settings;
    private readonly DrawingRectangle _protectionBounds;
    private readonly OverlayWindow _overlay;
    private readonly ScreenSampler _sampler;

    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly int _columns;
    private readonly int _rows;

    private readonly byte[] _previousFrame;
    private readonly bool[] _changedCells;
    private readonly bool[] _visitedCells;
    private readonly int[] _queue;

    private readonly List<ActivityZone> _zones = new();
    private readonly List<WpfRect> _manualZones = new();

    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _renderSubscribed;
    private bool _enabled;
    private bool _hasPreviousFrame;
    private bool _maskDirty;
    private bool _disposed;

    private long _revealAllUntilTicks;
    private long _ignoreMotionUntilTicks;

    private IntPtr _lastForegroundWindow;
    private DrawingRectangle _foregroundRevealBounds;
    private long _foregroundRevealTicks;

    private bool _hasCursor;
    private double _cursorX;
    private double _cursorY;

    private DrawingRectangle _cursorComponentBounds;
    private long _cursorComponentTicks;

    public MonitorSession(
        FormsScreen screen,
        AppSettings settings)
    {
        _screen = screen;
        _settings = settings;
        _protectionBounds =
            ProtectionArea.GetBounds(
                screen);

        var requestedWidth =
            Math.Clamp(
                settings.MotionZoneCaptureWidth,
                960,
                1280);

        _sampleWidth =
            Math.Max(
                CellSize,
                Math.Min(
                    requestedWidth,
                    _protectionBounds.Width));

        _sampleHeight =
            Math.Max(
                CellSize,
                (int)Math.Round(
                    _protectionBounds.Height *
                    _sampleWidth /
                    (double)Math.Max(
                        1,
                        _protectionBounds.Width)));

        _sampleStride =
            checked(
                _sampleWidth *
                4);

        _columns =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    _sampleWidth /
                    (double)CellSize));

        _rows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    _sampleHeight /
                    (double)CellSize));

        var frameLength =
            checked(
                _sampleStride *
                _sampleHeight);

        var cellCount =
            checked(
                _columns *
                _rows);

        _previousFrame =
            new byte[frameLength];
        _changedCells =
            new bool[cellCount];
        _visitedCells =
            new bool[cellCount];
        _queue =
            new int[cellCount];

        _overlay =
            new OverlayWindow(
                screen);

        _sampler =
            new ScreenSampler(
                _protectionBounds,
                _sampleWidth,
                _sampleHeight);
    }

    public bool ExcludedFromCapture =>
        _overlay.ExcludedFromCapture;

    public string ScreenDeviceName =>
        _screen.DeviceName;

    public void Start(
        bool enabled)
    {
        _overlay.EnsureVisible();

        SetEnabled(
            enabled);

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
            _hasPreviousFrame = false;
            _maskDirty = true;

            _revealAllUntilTicks = 0;
            _ignoreMotionUntilTicks = 0;

            _lastForegroundWindow =
                IntPtr.Zero;
            _foregroundRevealBounds =
                DrawingRectangle.Empty;
            _foregroundRevealTicks = 0;

            _zones.Clear();

            _hasCursor = false;
            _cursorX = 0.0;
            _cursorY = 0.0;
            _cursorComponentBounds =
                DrawingRectangle.Empty;
            _cursorComponentTicks = 0;

            Array.Clear(
                _previousFrame,
                0,
                _previousFrame.Length);
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
                duration <=
                    TimeSpan.Zero
                    ? 0
                    : Stopwatch.GetTimestamp() +
                      ToStopwatchTicks(
                          duration
                              .TotalMilliseconds);

            _maskDirty = true;
        }
    }

    public void SetManualRevealZones(
        IReadOnlyList<WpfRect> zones)
    {
        lock (_sync)
        {
            _manualZones.Clear();

            foreach (var zone in
                     zones)
            {
                var normalized =
                    NormalizeRect(
                        zone);

                if (normalized.IsEmpty)
                {
                    continue;
                }

                _manualZones.Add(
                    normalized);
            }

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
                bool capture;

                lock (_sync)
                {
                    capture =
                        _enabled;
                }

                if (capture)
                {
                    var foregroundWindow =
                        GetForegroundWindow();

                    var currentFrame =
                        _sampler.Capture();

                    AnalyzeFrame(
                        currentFrame,
                        foregroundWindow);
                }

                await Task.Delay(
                        CaptureMilliseconds,
                        _cancellation.Token)
                    .ConfigureAwait(
                        false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(
                            100,
                            _cancellation.Token)
                        .ConfigureAwait(
                            false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void AnalyzeFrame(
        byte[] currentFrame,
        IntPtr foregroundWindow)
    {
        var now =
            Stopwatch.GetTimestamp();

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            ReadCursor(
                now);

            var foregroundChanged =
                foregroundWindow !=
                _lastForegroundWindow;

            if (foregroundChanged)
            {
                _lastForegroundWindow =
                    foregroundWindow;

                RevealForegroundWindow(
                    foregroundWindow,
                    now);

                _ignoreMotionUntilTicks =
                    now +
                    ToStopwatchTicks(
                        120);
            }

            if (!_hasPreviousFrame)
            {
                CopyFrame(
                    currentFrame);

                _hasPreviousFrame =
                    true;
                _maskDirty = true;
                return;
            }

            var changedCellCount =
                DetectChangedCells(
                    currentFrame);

            if (now <
                _ignoreMotionUntilTicks)
            {
                CopyFrame(
                    currentFrame);
                return;
            }

            var changedFraction =
                changedCellCount /
                (double)Math.Max(
                    1,
                    _changedCells.Length);

            if (changedFraction >=
                0.18)
            {
                RevealForegroundWindow(
                    foregroundWindow,
                    now);

                _ignoreMotionUntilTicks =
                    now +
                    ToStopwatchTicks(
                        120);

                CopyFrame(
                    currentFrame);
                return;
            }

            var components =
                BuildConnectedComponents();

            UpdateZones(
                components,
                now);

            UpdateCursorComponent(
                components,
                now);

            CopyFrame(
                currentFrame);
        }
    }

    private void RevealForegroundWindow(
        IntPtr foregroundWindow,
        long now)
    {
        if (foregroundWindow ==
                IntPtr.Zero ||
            IsShellWindow(
                foregroundWindow) ||
            !GetWindowRect(
                foregroundWindow,
                out var nativeRectangle))
        {
            return;
        }

        var absolute =
            DrawingRectangle.FromLTRB(
                nativeRectangle.Left,
                nativeRectangle.Top,
                nativeRectangle.Right,
                nativeRectangle.Bottom);

        var visible =
            DrawingRectangle.Intersect(
                absolute,
                _protectionBounds);

        if (visible.Width < 40 ||
            visible.Height < 30)
        {
            return;
        }

        _foregroundRevealBounds =
            ToLocalBounds(
                visible);

        _foregroundRevealTicks =
            now;

        _maskDirty = true;
    }

    private static bool IsShellWindow(
        IntPtr window)
    {
        var className =
            new System.Text.StringBuilder(
                128);

        if (GetClassName(
                window,
                className,
                className.Capacity) <= 0)
        {
            return false;
        }

        return className.ToString() is
            "Progman" or
            "WorkerW" or
            "Shell_TrayWnd" or
            "Shell_SecondaryTrayWnd";
    }

    private void ReadCursor(
        long now)
    {
        if (!_settings.MouseVisualEnabled ||
            !NativeMethods.GetCursorPos(
                out var cursor) ||
            !_protectionBounds.Contains(
                cursor.X,
                cursor.Y))
        {
            if (_hasCursor)
            {
                _hasCursor = false;
                _maskDirty = true;
            }

            return;
        }

        var localX =
            cursor.X -
            _protectionBounds.Left;

        var localY =
            cursor.Y -
            _protectionBounds.Top;

        var moved =
            !_hasCursor ||
            Math.Abs(
                localX -
                _cursorX) >= 0.5 ||
            Math.Abs(
                localY -
                _cursorY) >= 0.5;

        _hasCursor = true;
        _cursorX = localX;
        _cursorY = localY;

        if (moved)
        {
            RevealKnownZoneUnderCursor(
                localX,
                localY,
                now);

            _maskDirty = true;
        }
    }

    private int DetectChangedCells(
        byte[] currentFrame)
    {
        Array.Clear(
            _changedCells,
            0,
            _changedCells.Length);

        var normalThreshold =
            Math.Clamp(
                _settings
                    .MotionZonePixelThreshold,
                6,
                24);

        var changedCellCount = 0;

        for (var row = 0;
             row < _rows;
             row++)
        {
            var top =
                row *
                CellSize;

            var bottom =
                Math.Min(
                    _sampleHeight - 1,
                    top +
                    CellSize -
                    1);

            for (var column = 0;
                 column < _columns;
                 column++)
            {
                var left =
                    column *
                    CellSize;

                var right =
                    Math.Min(
                        _sampleWidth - 1,
                        left +
                        CellSize -
                        1);

                var centerX =
                    (left +
                     right) /
                    2;

                var centerY =
                    (top +
                     bottom) /
                    2;

                var nearCursor =
                    IsSampleCellNearCursor(
                        left,
                        top,
                        right,
                        bottom);

                var threshold =
                    nearCursor
                        ? Math.Max(
                            5,
                            normalThreshold -
                            2)
                        : normalThreshold;

                var changedSamples = 0;

                changedSamples +=
                    PixelChanged(
                        currentFrame,
                        left,
                        top,
                        threshold)
                        ? 1
                        : 0;

                changedSamples +=
                    PixelChanged(
                        currentFrame,
                        right,
                        top,
                        threshold)
                        ? 1
                        : 0;

                changedSamples +=
                    PixelChanged(
                        currentFrame,
                        left,
                        bottom,
                        threshold)
                        ? 1
                        : 0;

                changedSamples +=
                    PixelChanged(
                        currentFrame,
                        right,
                        bottom,
                        threshold)
                        ? 1
                        : 0;

                changedSamples +=
                    PixelChanged(
                        currentFrame,
                        centerX,
                        centerY,
                        threshold)
                        ? 1
                        : 0;

                var changed =
                    nearCursor
                        ? changedSamples >= 1
                        : changedSamples >= 2;

                if (!changed)
                {
                    continue;
                }

                _changedCells[
                    row *
                    _columns +
                    column] = true;

                changedCellCount++;
            }
        }

        return changedCellCount;
    }

    private bool IsSampleCellNearCursor(
        int left,
        int top,
        int right,
        int bottom)
    {
        if (!_hasCursor)
        {
            return false;
        }

        var sampleCursorX =
            _cursorX *
            _sampleWidth /
            Math.Max(
                1.0,
                _protectionBounds.Width);

        var sampleCursorY =
            _cursorY *
            _sampleHeight /
            Math.Max(
                1.0,
                _protectionBounds.Height);

        var margin =
            CursorComponentNearPixels *
            _sampleWidth /
            Math.Max(
                1.0,
                _protectionBounds.Width);

        return sampleCursorX >=
                   left -
                   margin &&
               sampleCursorX <=
                   right +
                   margin &&
               sampleCursorY >=
                   top -
                   margin &&
               sampleCursorY <=
                   bottom +
                   margin;
    }

    private bool PixelChanged(
        byte[] currentFrame,
        int x,
        int y,
        int threshold)
    {
        var index =
            y *
            _sampleStride +
            x *
            4;

        var blueDifference =
            Math.Abs(
                currentFrame[index] -
                _previousFrame[index]);

        var greenDifference =
            Math.Abs(
                currentFrame[index + 1] -
                _previousFrame[index + 1]);

        var redDifference =
            Math.Abs(
                currentFrame[index + 2] -
                _previousFrame[index + 2]);

        return Math.Max(
                   blueDifference,
                   Math.Max(
                       greenDifference,
                       redDifference)) >=
               threshold;
    }

    private List<MotionComponent>
        BuildConnectedComponents()
    {
        Array.Clear(
            _visitedCells,
            0,
            _visitedCells.Length);

        var components =
            new List<MotionComponent>();

        for (var row = 0;
             row < _rows;
             row++)
        {
            for (var column = 0;
                 column < _columns;
                 column++)
            {
                var startIndex =
                    row *
                    _columns +
                    column;

                if (!_changedCells[startIndex] ||
                    _visitedCells[startIndex])
                {
                    continue;
                }

                var head = 0;
                var tail = 0;

                _queue[tail++] =
                    startIndex;

                _visitedCells[startIndex] =
                    true;

                var minimumRow = row;
                var maximumRow = row;
                var minimumColumn =
                    column;
                var maximumColumn =
                    column;
                var cellCount = 0;

                while (head < tail)
                {
                    var index =
                        _queue[head++];

                    var currentRow =
                        index /
                        _columns;

                    var currentColumn =
                        index %
                        _columns;

                    cellCount++;

                    minimumRow =
                        Math.Min(
                            minimumRow,
                            currentRow);

                    maximumRow =
                        Math.Max(
                            maximumRow,
                            currentRow);

                    minimumColumn =
                        Math.Min(
                            minimumColumn,
                            currentColumn);

                    maximumColumn =
                        Math.Max(
                            maximumColumn,
                            currentColumn);

                    for (var rowOffset = -1;
                         rowOffset <= 1;
                         rowOffset++)
                    {
                        for (var columnOffset = -1;
                             columnOffset <= 1;
                             columnOffset++)
                        {
                            if (rowOffset == 0 &&
                                columnOffset == 0)
                            {
                                continue;
                            }

                            var nextRow =
                                currentRow +
                                rowOffset;

                            var nextColumn =
                                currentColumn +
                                columnOffset;

                            if (nextRow < 0 ||
                                nextRow >= _rows ||
                                nextColumn < 0 ||
                                nextColumn >=
                                _columns)
                            {
                                continue;
                            }

                            var nextIndex =
                                nextRow *
                                _columns +
                                nextColumn;

                            if (!_changedCells[nextIndex] ||
                                _visitedCells[nextIndex])
                            {
                                continue;
                            }

                            _visitedCells[nextIndex] =
                                true;

                            _queue[tail++] =
                                nextIndex;
                        }
                    }
                }

                var bounds =
                    CellsToLocalRectangle(
                        minimumRow,
                        maximumRow,
                        minimumColumn,
                        maximumColumn);

                var nearCursor =
                    IsCursorNearRectangle(
                        bounds,
                        CursorComponentNearPixels);

                if (cellCount < 2 &&
                    !nearCursor)
                {
                    continue;
                }

                if (bounds.Width < 5 ||
                    bounds.Height < 5)
                {
                    continue;
                }

                components.Add(
                    new MotionComponent(
                        bounds));
            }
        }

        return components;
    }

    private DrawingRectangle
        CellsToLocalRectangle(
            int minimumRow,
            int maximumRow,
            int minimumColumn,
            int maximumColumn)
    {
        var sampleLeft =
            minimumColumn *
            CellSize;

        var sampleTop =
            minimumRow *
            CellSize;

        var sampleRight =
            Math.Min(
                _sampleWidth,
                (maximumColumn + 1) *
                CellSize);

        var sampleBottom =
            Math.Min(
                _sampleHeight,
                (maximumRow + 1) *
                CellSize);

        var left =
            (int)Math.Floor(
                sampleLeft *
                _protectionBounds.Width /
                (double)Math.Max(
                    1,
                    _sampleWidth));

        var top =
            (int)Math.Floor(
                sampleTop *
                _protectionBounds.Height /
                (double)Math.Max(
                    1,
                    _sampleHeight));

        var right =
            (int)Math.Ceiling(
                sampleRight *
                _protectionBounds.Width /
                (double)Math.Max(
                    1,
                    _sampleWidth));

        var bottom =
            (int)Math.Ceiling(
                sampleBottom *
                _protectionBounds.Height /
                (double)Math.Max(
                    1,
                    _sampleHeight));

        var rectangle =
            DrawingRectangle.FromLTRB(
                left,
                top,
                right,
                bottom);

        rectangle.Inflate(
            4,
            4);

        return ClampLocalRectangle(
            rectangle);
    }

    private void UpdateZones(
        IReadOnlyList<MotionComponent> components,
        long now)
    {
        foreach (var component in
                 components)
        {
            var zone =
                FindMatchingZone(
                    component.Bounds);

            if (zone is null)
            {
                zone =
                    new ActivityZone
                    {
                        Bounds =
                            component.Bounds,
                        RecentBounds =
                            component.Bounds,
                        HasRecentBounds =
                            true,
                        ContinuousStartTicks =
                            now,
                        LastActivityTicks =
                            now,
                        LastResizeTicks =
                            now
                    };

                _zones.Add(
                    zone);

                if (_zones.Count >
                    MaximumZoneCount)
                {
                    var oldest =
                        _zones
                            .OrderBy(
                                candidate =>
                                    candidate
                                        .LastActivityTicks)
                            .First();

                    _zones.Remove(
                        oldest);
                }

                _maskDirty = true;
                continue;
            }

            var previousActivity =
                zone.LastActivityTicks;

            if (now -
                    previousActivity >
                ToStopwatchTicks(
                    ContinuousGapMilliseconds))
            {
                zone.ContinuousStartTicks =
                    now;

                zone.ConfirmedAtOneSecond =
                    false;

                zone.ConfirmedAtThreeSeconds =
                    false;
            }

            zone.LastActivityTicks =
                now;

            zone.RecentBounds =
                zone.HasRecentBounds
                    ? DrawingRectangle.Union(
                        zone.RecentBounds,
                        component.Bounds)
                    : component.Bounds;

            zone.HasRecentBounds =
                true;

            var grownBounds =
                DrawingRectangle.Union(
                    zone.Bounds,
                    component.Bounds);

            if (grownBounds !=
                zone.Bounds)
            {
                zone.Bounds =
                    ClampLocalRectangle(
                        grownBounds);

                _maskDirty = true;
            }

            if (!zone.Validated)
            {
                var continuousElapsed =
                    now -
                    zone.ContinuousStartTicks;

                if (continuousElapsed >=
                    ToStopwatchTicks(
                        ValidationOneMilliseconds))
                {
                    zone.ConfirmedAtOneSecond =
                        true;
                }

                if (continuousElapsed >=
                    ToStopwatchTicks(
                        ValidationThreeMilliseconds))
                {
                    zone.ConfirmedAtThreeSeconds =
                        true;
                }

                if (continuousElapsed >=
                        ToStopwatchTicks(
                            ValidationFiveMilliseconds) &&
                    zone.ConfirmedAtOneSecond &&
                    zone.ConfirmedAtThreeSeconds)
                {
                    zone.Validated =
                        true;

                    _maskDirty = true;
                }
            }
        }

        ResizeZones(
            now);
    }

    private ActivityZone? FindMatchingZone(
        DrawingRectangle component)
    {
        ActivityZone? best = null;
        var bestScore =
            double.NegativeInfinity;

        foreach (var zone in
                 _zones)
        {
            if (!RectanglesConnected(
                    zone.Bounds,
                    component,
                    6))
            {
                continue;
            }

            var intersection =
                DrawingRectangle.Intersect(
                    zone.Bounds,
                    component);

            var intersectionArea =
                Math.Max(
                    0,
                    intersection.Width) *
                Math.Max(
                    0,
                    intersection.Height);

            var centerDistance =
                Math.Abs(
                    zone.Bounds.Left +
                    zone.Bounds.Width /
                    2.0 -
                    component.Left -
                    component.Width /
                    2.0) +
                Math.Abs(
                    zone.Bounds.Top +
                    zone.Bounds.Height /
                    2.0 -
                    component.Top -
                    component.Height /
                    2.0);

            var score =
                intersectionArea *
                1000.0 -
                centerDistance;

            if (score <=
                bestScore)
            {
                continue;
            }

            best =
                zone;

            bestScore =
                score;
        }

        return best;
    }

    private void ResizeZones(
        long now)
    {
        foreach (var zone in
                 _zones)
        {
            if (now -
                    zone.LastResizeTicks <
                ToStopwatchTicks(
                    ResizeMilliseconds))
            {
                continue;
            }

            zone.LastResizeTicks =
                now;

            if (!zone.HasRecentBounds)
            {
                continue;
            }

            var cleanBounds =
                zone.RecentBounds;

            cleanBounds.Inflate(
                zone.Validated
                    ? 8
                    : 4,
                zone.Validated
                    ? 8
                    : 4);

            cleanBounds =
                ClampLocalRectangle(
                    cleanBounds);

            if (cleanBounds.Width > 0 &&
                cleanBounds.Height > 0 &&
                cleanBounds !=
                zone.Bounds)
            {
                zone.Bounds =
                    cleanBounds;

                _maskDirty = true;
            }

            zone.RecentBounds =
                DrawingRectangle.Empty;

            zone.HasRecentBounds =
                false;
        }
    }

    private void UpdateCursorComponent(
        IReadOnlyList<MotionComponent> components,
        long now)
    {
        if (!_hasCursor)
        {
            return;
        }

        foreach (var component in
                 components)
        {
            if (!IsCursorNearRectangle(
                    component.Bounds,
                    CursorComponentNearPixels))
            {
                continue;
            }

            var zone =
                FindMatchingZone(
                    component.Bounds);

            _cursorComponentBounds =
                zone?.Bounds ??
                component.Bounds;

            _cursorComponentTicks =
                now;

            if (zone is not null)
            {
                zone.LastActivityTicks =
                    now;
            }

            _maskDirty = true;
            return;
        }
    }

    private void RevealKnownZoneUnderCursor(
        double localX,
        double localY,
        long now)
    {
        foreach (var zone in
                 _zones)
        {
            if (!zone.Bounds.Contains(
                    (int)Math.Round(
                        localX),
                    (int)Math.Round(
                        localY)))
            {
                continue;
            }

            _cursorComponentBounds =
                zone.Bounds;

            _cursorComponentTicks =
                now;

            zone.LastActivityTicks =
                now;

            _maskDirty = true;
            return;
        }
    }

    private void OnRendering(
        object? sender,
        EventArgs eventArgs)
    {
        var now =
            Stopwatch.GetTimestamp();

        var shouldPush = false;

        lock (_sync)
        {
            ReadCursor(
                now);

            var animationActive =
                UpdateExpirations(
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
                animationActive ||
                revealAllExpired;

            _maskDirty = false;
        }

        if (shouldPush)
        {
            PushScene(
                now);
        }
    }

    private bool UpdateExpirations(
        long now)
    {
        var animationActive = false;

        for (var index =
                 _zones.Count - 1;
             index >= 0;
             index--)
        {
            var zone =
                _zones[index];

            var holdMilliseconds =
                zone.Validated
                    ? ValidatedHoldMilliseconds
                    : TransientHoldMilliseconds;

            var elapsed =
                now -
                zone.LastActivityTicks;

            if (elapsed >=
                ToStopwatchTicks(
                    holdMilliseconds +
                    FadeMilliseconds))
            {
                _zones.RemoveAt(
                    index);

                _maskDirty = true;
                continue;
            }

            if (elapsed >=
                ToStopwatchTicks(
                    holdMilliseconds))
            {
                animationActive = true;
            }
        }

        if (!_foregroundRevealBounds.IsEmpty)
        {
            var elapsed =
                now -
                _foregroundRevealTicks;

            if (elapsed >=
                ToStopwatchTicks(
                    TransientHoldMilliseconds +
                    FadeMilliseconds))
            {
                _foregroundRevealBounds =
                    DrawingRectangle.Empty;

                _foregroundRevealTicks = 0;

                _maskDirty = true;
            }
            else if (elapsed >=
                     ToStopwatchTicks(
                         TransientHoldMilliseconds))
            {
                animationActive = true;
            }
        }

        if (!_cursorComponentBounds.IsEmpty)
        {
            var elapsed =
                now -
                _cursorComponentTicks;

            if (elapsed >=
                ToStopwatchTicks(
                    CursorComponentHoldMilliseconds +
                    FadeMilliseconds))
            {
                _cursorComponentBounds =
                    DrawingRectangle.Empty;

                _cursorComponentTicks = 0;

                _maskDirty = true;
            }
            else if (elapsed >=
                     ToStopwatchTicks(
                         CursorComponentHoldMilliseconds))
            {
                animationActive = true;
            }
        }

        return animationActive;
    }

    private void PushScene(
        long now)
    {
        List<MaskRegion> regions;
        List<MouseReveal> cursorReveals;
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
                    : BuildMaskRegions(
                        now);

            cursorReveals =
                revealAll
                    ? new List<MouseReveal>()
                    : BuildCursorReveal();
        }

        _overlay.SetScene(
            maximumOpacity,
            regions,
            cursorReveals);
    }

    private List<MaskRegion> BuildMaskRegions(
        long now)
    {
        var regions =
            new List<MaskRegion>(
                _zones.Count +
                _manualZones.Count +
                2);

        var maximumOpacity =
            _settings
                .MaximumMaskOpacity;

        foreach (var zone in
                 _zones)
        {
            var holdMilliseconds =
                zone.Validated
                    ? ValidatedHoldMilliseconds
                    : TransientHoldMilliseconds;

            regions.Add(
                new MaskRegion(
                    ToNormalizedRect(
                        zone.Bounds),
                    ComputeOpacity(
                        now -
                        zone.LastActivityTicks,
                        holdMilliseconds,
                        FadeMilliseconds,
                        maximumOpacity)));
        }

        if (!_foregroundRevealBounds.IsEmpty)
        {
            regions.Add(
                new MaskRegion(
                    ToNormalizedRect(
                        _foregroundRevealBounds),
                    ComputeOpacity(
                        now -
                        _foregroundRevealTicks,
                        TransientHoldMilliseconds,
                        FadeMilliseconds,
                        maximumOpacity)));
        }

        if (!_cursorComponentBounds.IsEmpty)
        {
            regions.Add(
                new MaskRegion(
                    ToNormalizedRect(
                        _cursorComponentBounds),
                    ComputeOpacity(
                        now -
                        _cursorComponentTicks,
                        CursorComponentHoldMilliseconds,
                        FadeMilliseconds,
                        maximumOpacity)));
        }

        AppendManualZones(
            regions);

        return regions;
    }

    private List<MouseReveal> BuildCursorReveal()
    {
        var result =
            new List<MouseReveal>(
                1);

        if (!_hasCursor ||
            !_settings.MouseVisualEnabled)
        {
            return result;
        }

        var radius =
            Math.Clamp(
                _settings
                    .MouseVisualRadiusPixels,
                8,
                48);

        result.Add(
            new MouseReveal(
                new System.Windows.Point(
                    _cursorX /
                    Math.Max(
                        1.0,
                        _protectionBounds.Width),
                    _cursorY /
                    Math.Max(
                        1.0,
                        _protectionBounds.Height)),
                radius /
                Math.Max(
                    1.0,
                    _protectionBounds.Width),
                radius /
                Math.Max(
                    1.0,
                    _protectionBounds.Height)));

        return result;
    }

    private void AppendManualZones(
        ICollection<MaskRegion> regions)
    {
        var fullScreen =
            _screen.Bounds;

        foreach (var normalized in
                 _manualZones)
        {
            var absolute =
                DrawingRectangle.FromLTRB(
                    fullScreen.Left +
                    (int)Math.Floor(
                        normalized.Left *
                        fullScreen.Width),
                    fullScreen.Top +
                    (int)Math.Floor(
                        normalized.Top *
                        fullScreen.Height),
                    fullScreen.Left +
                    (int)Math.Ceiling(
                        normalized.Right *
                        fullScreen.Width),
                    fullScreen.Top +
                    (int)Math.Ceiling(
                        normalized.Bottom *
                        fullScreen.Height));

            var visible =
                DrawingRectangle.Intersect(
                    absolute,
                    _protectionBounds);

            if (visible.Width <= 0 ||
                visible.Height <= 0)
            {
                continue;
            }

            regions.Add(
                new MaskRegion(
                    ToNormalizedRect(
                        ToLocalBounds(
                            visible)),
                    0.0));
        }
    }

    private bool IsCursorNearRectangle(
        DrawingRectangle rectangle,
        int margin)
    {
        if (!_hasCursor)
        {
            return false;
        }

        var expanded =
            rectangle;

        expanded.Inflate(
            margin,
            margin);

        return expanded.Contains(
            (int)Math.Round(
                _cursorX),
            (int)Math.Round(
                _cursorY));
    }

    private static bool RectanglesConnected(
        DrawingRectangle first,
        DrawingRectangle second,
        int tolerance)
    {
        var expanded =
            first;

        expanded.Inflate(
            tolerance,
            tolerance);

        return expanded.IntersectsWith(
                   second) ||
               expanded.Contains(
                   second);
    }

    private DrawingRectangle ClampLocalRectangle(
        DrawingRectangle rectangle)
    {
        return DrawingRectangle.Intersect(
            rectangle,
            new DrawingRectangle(
                0,
                0,
                _protectionBounds.Width,
                _protectionBounds.Height));
    }

    private DrawingRectangle ToLocalBounds(
        DrawingRectangle absolute)
    {
        return new DrawingRectangle(
            absolute.Left -
            _protectionBounds.Left,
            absolute.Top -
            _protectionBounds.Top,
            absolute.Width,
            absolute.Height);
    }

    private WpfRect ToNormalizedRect(
        DrawingRectangle rectangle)
    {
        return new WpfRect(
            rectangle.Left /
            (double)Math.Max(
                1,
                _protectionBounds.Width),
            rectangle.Top /
            (double)Math.Max(
                1,
                _protectionBounds.Height),
            rectangle.Width /
            (double)Math.Max(
                1,
                _protectionBounds.Width),
            rectangle.Height /
            (double)Math.Max(
                1,
                _protectionBounds.Height));
    }

    private static WpfRect NormalizeRect(
        WpfRect rectangle)
    {
        var left =
            Math.Clamp(
                rectangle.Left,
                0.0,
                1.0);

        var top =
            Math.Clamp(
                rectangle.Top,
                0.0,
                1.0);

        var right =
            Math.Clamp(
                rectangle.Right,
                0.0,
                1.0);

        var bottom =
            Math.Clamp(
                rectangle.Bottom,
                0.0,
                1.0);

        if (right <= left ||
            bottom <= top)
        {
            return WpfRect.Empty;
        }

        return new WpfRect(
            left,
            top,
            right - left,
            bottom - top);
    }

    private static double ComputeOpacity(
        long elapsedTicks,
        int holdMilliseconds,
        int fadeMilliseconds,
        double maximumOpacity)
    {
        var holdTicks =
            ToStopwatchTicks(
                holdMilliseconds);

        if (elapsedTicks <=
            holdTicks)
        {
            return 0.0;
        }

        var fadeTicks =
            Math.Max(
                1L,
                ToStopwatchTicks(
                    fadeMilliseconds));

        var progress =
            Math.Clamp(
                (elapsedTicks -
                 holdTicks) /
                (double)fadeTicks,
                0.0,
                1.0);

        return maximumOpacity *
               progress;
    }

    private void CopyFrame(
        byte[] currentFrame)
    {
        Buffer.BlockCopy(
            currentFrame,
            0,
            _previousFrame,
            0,
            currentFrame.Length);
    }

    private static long ToStopwatchTicks(
        double milliseconds)
    {
        return (long)(
            milliseconds *
            Stopwatch.Frequency /
            1000.0);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr
        GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out NativeWindowRect rectangle);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        System.Text.StringBuilder className,
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
