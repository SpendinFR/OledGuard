using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;
using WpfRect = System.Windows.Rect;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private const int CellSize = 8;
    private const int ReconcileMilliseconds = 500;
    private const int MaximumZoneCount = 64;
    private const int MaximumTrailSamples = 96;

    private sealed class ActiveZone
    {
        public DrawingRectangle Bounds;
        public DrawingRectangle PendingBounds;
        public bool HasPendingBounds;
        public long LastActivityTicks;
        public long LastReconcileTicks;
    }

    private readonly record struct MotionComponent(
        DrawingRectangle Bounds,
        int CellCount);

    private readonly record struct MouseSample(
        double X,
        double Y,
        long Timestamp);

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
    private readonly bool[] _visited;
    private readonly int[] _queue;

    private readonly List<ActiveZone> _activeZones = new();
    private readonly List<WpfRect> _manualRevealZones = new();
    private readonly List<MouseSample> _mouseTrail = new();

    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _renderSubscribed;
    private bool _enabled;
    private bool _hasPreviousFrame;
    private bool _maskDirty;
    private bool _disposed;

    private long _revealAllUntilTicks;

    private IntPtr _lastForegroundWindow;
    private DrawingRectangle _foregroundBounds;
    private long _foregroundRevealTicks;

    private bool _hasCursor;
    private double _cursorX;
    private double _cursorY;
    private long _lastCursorTicks;

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
                640,
                1280);
        requestedWidth =
            Math.Min(
                requestedWidth,
                _protectionBounds.Width);

        _sampleWidth =
            Math.Max(
                CellSize,
                requestedWidth);
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

        var frameBytes =
            checked(
                _sampleStride *
                _sampleHeight);
        var cellCount =
            checked(
                _columns *
                _rows);

        _previousFrame =
            new byte[frameBytes];
        _changedCells =
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
            _lastForegroundWindow =
                IntPtr.Zero;
            _foregroundBounds =
                DrawingRectangle.Empty;
            _foregroundRevealTicks = 0;

            _activeZones.Clear();
            _mouseTrail.Clear();
            _hasCursor = false;
            _cursorX = 0;
            _cursorY = 0;
            _lastCursorTicks = 0;

            Array.Clear(
                _previousFrame,
                0,
                _previousFrame.Length);
            Array.Clear(
                _changedCells,
                0,
                _changedCells.Length);
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
                duration <= TimeSpan.Zero
                    ? 0
                    : Stopwatch.GetTimestamp() +
                      ToStopwatchTicks(
                          duration.TotalMilliseconds);
            _maskDirty = true;
        }
    }

    public void SetManualRevealZones(
        IReadOnlyList<WpfRect> zones)
    {
        lock (_sync)
        {
            _manualRevealZones.Clear();

            foreach (var zone in
                     zones)
            {
                var normalized =
                    NormalizeRect(
                        zone);

                if (normalized.Width <= 0.0 ||
                    normalized.Height <= 0.0)
                {
                    continue;
                }

                _manualRevealZones.Add(
                    normalized);
            }

            _maskDirty = true;
        }
    }

    private async Task CaptureLoopAsync()
    {
        var delayMilliseconds =
            Math.Clamp(
                _settings
                    .MotionZoneSamplingMilliseconds,
                50,
                100);

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
                    var current =
                        _sampler.Capture();

                    AnalyzeCapture(
                        current,
                        foregroundWindow);
                }

                await Task.Delay(
                        delayMilliseconds,
                        _cancellation.Token)
                    .ConfigureAwait(false);
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
                            250,
                            _cancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void AnalyzeCapture(
        byte[] current,
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

            UpdateForegroundReveal(
                foregroundWindow,
                now);

            if (!_hasPreviousFrame)
            {
                CopyCurrentToPrevious(
                    current);
                _hasPreviousFrame = true;
                _maskDirty = true;
                return;
            }

            DetectChangedCells(
                current);
            var components =
                BuildMotionComponents();
            MergeNearbyComponents(
                components);
            UpdateActiveZones(
                components,
                now);

            CopyCurrentToPrevious(
                current);
        }
    }

    private void UpdateForegroundReveal(
        IntPtr foregroundWindow,
        long now)
    {
        if (foregroundWindow ==
            _lastForegroundWindow)
        {
            return;
        }

        _lastForegroundWindow =
            foregroundWindow;

        if (foregroundWindow ==
                IntPtr.Zero ||
            IsDesktopOrShellWindow(
                foregroundWindow) ||
            !GetWindowRect(
                foregroundWindow,
                out var native))
        {
            _foregroundBounds =
                DrawingRectangle.Empty;
            _foregroundRevealTicks = 0;
            _maskDirty = true;
            return;
        }

        var windowBounds =
            DrawingRectangle.FromLTRB(
                native.Left,
                native.Top,
                native.Right,
                native.Bottom);
        var visible =
            DrawingRectangle.Intersect(
                windowBounds,
                _protectionBounds);

        if (visible.Width < 40 ||
            visible.Height < 30)
        {
            _foregroundBounds =
                DrawingRectangle.Empty;
            _foregroundRevealTicks = 0;
            _maskDirty = true;
            return;
        }

        _foregroundBounds =
            ToLocalBounds(
                visible);
        _foregroundRevealTicks =
            now;
        _maskDirty = true;
    }

    private static bool IsDesktopOrShellWindow(
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

    private void DetectChangedCells(
        byte[] current)
    {
        Array.Clear(
            _changedCells,
            0,
            _changedCells.Length);

        var threshold =
            Math.Clamp(
                _settings
                    .MotionZonePixelThreshold,
                6,
                32);

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

                var changedSamples = 0;

                changedSamples +=
                    PixelChanged(
                        current,
                        left,
                        top,
                        threshold)
                        ? 1
                        : 0;
                changedSamples +=
                    PixelChanged(
                        current,
                        right,
                        top,
                        threshold)
                        ? 1
                        : 0;
                changedSamples +=
                    PixelChanged(
                        current,
                        left,
                        bottom,
                        threshold)
                        ? 1
                        : 0;
                changedSamples +=
                    PixelChanged(
                        current,
                        right,
                        bottom,
                        threshold)
                        ? 1
                        : 0;
                changedSamples +=
                    PixelChanged(
                        current,
                        centerX,
                        centerY,
                        threshold)
                        ? 1
                        : 0;

                if (changedSamples >= 2)
                {
                    _changedCells[
                        row *
                        _columns +
                        column] = true;
                }
            }
        }
    }

    private bool PixelChanged(
        byte[] current,
        int x,
        int y,
        int threshold)
    {
        var index =
            y *
            _sampleStride +
            x *
            4;

        var blue =
            Math.Abs(
                current[index] -
                _previousFrame[index]);
        var green =
            Math.Abs(
                current[index + 1] -
                _previousFrame[index + 1]);
        var red =
            Math.Abs(
                current[index + 2] -
                _previousFrame[index + 2]);

        return Math.Max(
                   blue,
                   Math.Max(
                       green,
                       red)) >=
               threshold;
    }

    private List<MotionComponent>
        BuildMotionComponents()
    {
        Array.Clear(
            _visited,
            0,
            _visited.Length);

        var result =
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
                    _visited[startIndex])
                {
                    continue;
                }

                var head = 0;
                var tail = 0;
                _queue[tail++] =
                    startIndex;
                _visited[startIndex] =
                    true;

                var minimumRow = row;
                var maximumRow = row;
                var minimumColumn =
                    column;
                var maximumColumn =
                    column;
                var cells = 0;

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
                    cells++;

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

                            if (!_changedCells[
                                    nextIndex] ||
                                _visited[
                                    nextIndex])
                            {
                                continue;
                            }

                            _visited[nextIndex] =
                                true;
                            _queue[tail++] =
                                nextIndex;
                        }
                    }
                }

                var bounds =
                    ComponentToLocalBounds(
                        minimumRow,
                        maximumRow,
                        minimumColumn,
                        maximumColumn);

                if (cells < 2 &&
                    !IsCursorNear(
                        bounds,
                        20))
                {
                    continue;
                }

                if (bounds.Width < 5 ||
                    bounds.Height < 5)
                {
                    continue;
                }

                result.Add(
                    new MotionComponent(
                        bounds,
                        cells));
            }
        }

        return result;
    }

    private DrawingRectangle
        ComponentToLocalBounds(
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
            6,
            6);

        return ClampLocalBounds(
            rectangle);
    }

    private static void MergeNearbyComponents(
        List<MotionComponent> components)
    {
        var merged = true;

        while (merged)
        {
            merged = false;

            for (var firstIndex = 0;
                 firstIndex < components.Count;
                 firstIndex++)
            {
                for (var secondIndex =
                         firstIndex + 1;
                     secondIndex <
                     components.Count;
                     secondIndex++)
                {
                    var first =
                        components[firstIndex];
                    var second =
                        components[secondIndex];

                    if (!RectanglesNear(
                            first.Bounds,
                            second.Bounds,
                            10))
                    {
                        continue;
                    }

                    components[firstIndex] =
                        new MotionComponent(
                            DrawingRectangle.Union(
                                first.Bounds,
                                second.Bounds),
                            first.CellCount +
                            second.CellCount);
                    components.RemoveAt(
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

    private void UpdateActiveZones(
        IReadOnlyList<MotionComponent> components,
        long now)
    {
        foreach (var component in
                 components)
        {
            var zone =
                FindBestZone(
                    component.Bounds);

            if (zone is null)
            {
                zone =
                    new ActiveZone
                    {
                        Bounds =
                            component.Bounds,
                        PendingBounds =
                            component.Bounds,
                        HasPendingBounds =
                            true,
                        LastActivityTicks =
                            now,
                        LastReconcileTicks =
                            now
                    };

                _activeZones.Add(
                    zone);

                if (_activeZones.Count >
                    MaximumZoneCount)
                {
                    var oldest =
                        _activeZones
                            .OrderBy(
                                candidate =>
                                    candidate
                                        .LastActivityTicks)
                            .First();
                    _activeZones.Remove(
                        oldest);
                }

                _maskDirty = true;
                continue;
            }

            var grown =
                DrawingRectangle.Union(
                    zone.Bounds,
                    component.Bounds);

            if (grown !=
                zone.Bounds)
            {
                zone.Bounds =
                    ClampLocalBounds(
                        grown);
                _maskDirty = true;
            }

            zone.PendingBounds =
                zone.HasPendingBounds
                    ? DrawingRectangle.Union(
                        zone.PendingBounds,
                        component.Bounds)
                    : component.Bounds;
            zone.HasPendingBounds =
                true;
            zone.LastActivityTicks =
                now;
        }

        foreach (var zone in
                 _activeZones)
        {
            if (now -
                    zone.LastReconcileTicks <
                ToStopwatchTicks(
                    ReconcileMilliseconds))
            {
                continue;
            }

            if (zone.HasPendingBounds)
            {
                var target =
                    zone.PendingBounds;
                target.Inflate(
                    6,
                    6);
                target =
                    ClampLocalBounds(
                        target);

                if (target.Width > 0 &&
                    target.Height > 0)
                {
                    var currentArea =
                        Math.Max(
                            1,
                            zone.Bounds.Width *
                            zone.Bounds.Height);
                    var targetArea =
                        Math.Max(
                            1,
                            target.Width *
                            target.Height);

                    if (targetArea <
                        currentArea *
                        0.90)
                    {
                        zone.Bounds =
                            target;
                        _maskDirty = true;
                    }
                    else
                    {
                        zone.Bounds =
                            ClampLocalBounds(
                                DrawingRectangle.Union(
                                    zone.Bounds,
                                    target));
                    }
                }
            }

            zone.PendingBounds =
                DrawingRectangle.Empty;
            zone.HasPendingBounds =
                false;
            zone.LastReconcileTicks =
                now;
        }
    }

    private ActiveZone? FindBestZone(
        DrawingRectangle component)
    {
        ActiveZone? best = null;
        var bestScore =
            double.NegativeInfinity;

        foreach (var zone in
                 _activeZones)
        {
            if (!RectanglesNear(
                    zone.Bounds,
                    component,
                    18))
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
                    zone.Bounds.Width / 2.0 -
                    component.Left -
                    component.Width / 2.0) +
                Math.Abs(
                    zone.Bounds.Top +
                    zone.Bounds.Height / 2.0 -
                    component.Top -
                    component.Height / 2.0);

            var score =
                intersectionArea *
                1000.0 -
                centerDistance;

            if (score >
                bestScore)
            {
                best =
                    zone;
                bestScore =
                    score;
            }
        }

        return best;
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
            var cursorChanged =
                UpdateCursor(
                    now);
            var animationActive =
                UpdateLifetimes(
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
                cursorChanged ||
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

    private bool UpdateCursor(
        long now)
    {
        if (!_enabled ||
            !_settings.MouseVisualEnabled ||
            !NativeMethods.GetCursorPos(
                out var cursor) ||
            !_protectionBounds.Contains(
                cursor.X,
                cursor.Y))
        {
            var changed =
                _hasCursor ||
                _mouseTrail.Count > 0;
            _hasCursor = false;
            _mouseTrail.Clear();
            return changed;
        }

        var localX =
            cursor.X -
            _protectionBounds.Left;
        var localY =
            cursor.Y -
            _protectionBounds.Top;

        if (!_hasCursor)
        {
            _hasCursor = true;
            _cursorX = localX;
            _cursorY = localY;
            _lastCursorTicks =
                now;
            RefreshZoneUnderCursor(
                localX,
                localY,
                now);
            return true;
        }

        var moved =
            Math.Abs(
                localX -
                _cursorX) >= 0.5 ||
            Math.Abs(
                localY -
                _cursorY) >= 0.5;

        if (moved)
        {
            AddCursorPath(
                _cursorX,
                _cursorY,
                localX,
                localY,
                _lastCursorTicks,
                now);

            _cursorX = localX;
            _cursorY = localY;
            _lastCursorTicks =
                now;

            RefreshZoneUnderCursor(
                localX,
                localY,
                now);
        }

        var trailChanged =
            PruneMouseTrail(
                now);

        return moved ||
               trailChanged ||
               _mouseTrail.Count > 0;
    }

    private void RefreshZoneUnderCursor(
        double x,
        double y,
        long now)
    {
        foreach (var zone in
                 _activeZones)
        {
            if (!zone.Bounds.Contains(
                    (int)Math.Round(x),
                    (int)Math.Round(y)))
            {
                continue;
            }

            zone.LastActivityTicks =
                now;
            _maskDirty = true;
        }
    }

    private void AddCursorPath(
        double startX,
        double startY,
        double endX,
        double endY,
        long startTicks,
        long endTicks)
    {
        var trailMilliseconds =
            Math.Clamp(
                _settings
                    .MouseTrailMilliseconds,
                0,
                250);

        if (trailMilliseconds <= 0)
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
        var spacing =
            Math.Max(
                4,
                _settings
                    .MouseTrailSpacingPixels);
        var steps =
            Math.Clamp(
                (int)Math.Ceiling(
                    distance /
                    spacing),
                1,
                12);

        for (var step = 1;
             step <= steps;
             step++)
        {
            var ratio =
                step /
                (double)steps;

            _mouseTrail.Add(
                new MouseSample(
                    startX +
                    (endX -
                     startX) *
                    ratio,
                    startY +
                    (endY -
                     startY) *
                    ratio,
                    startTicks +
                    (long)(
                        (endTicks -
                         startTicks) *
                        ratio)));
        }

        if (_mouseTrail.Count >
            MaximumTrailSamples)
        {
            _mouseTrail.RemoveRange(
                0,
                _mouseTrail.Count -
                MaximumTrailSamples);
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
                Math.Clamp(
                    _settings
                        .MouseTrailMilliseconds,
                    0,
                    250));
        var removed = false;

        while (_mouseTrail.Count > 0 &&
               now -
                   _mouseTrail[0]
                       .Timestamp >=
               lifetimeTicks)
        {
            _mouseTrail.RemoveAt(
                0);
            removed = true;
        }

        return removed;
    }

    private bool UpdateLifetimes(
        long now)
    {
        var holdTicks =
            ToStopwatchTicks(
                Math.Clamp(
                    _settings
                        .MotionZoneRecurringHoldMilliseconds,
                    500,
                    10_000));
        var fadeTicks =
            ToStopwatchTicks(
                Math.Clamp(
                    _settings
                        .MotionZoneTransientFadeMilliseconds,
                    80,
                    1_000));
        var animationActive = false;

        for (var index =
                 _activeZones.Count - 1;
             index >= 0;
             index--)
        {
            var elapsed =
                now -
                _activeZones[index]
                    .LastActivityTicks;

            if (elapsed >=
                holdTicks +
                fadeTicks)
            {
                _activeZones.RemoveAt(
                    index);
                _maskDirty = true;
                continue;
            }

            if (elapsed >=
                holdTicks)
            {
                animationActive = true;
            }
        }

        if (!_foregroundBounds.IsEmpty)
        {
            var foregroundHold =
                ToStopwatchTicks(
                    Math.Clamp(
                        _settings
                            .ForegroundWindowRevealMilliseconds,
                        400,
                        5_000));
            var foregroundFade =
                ToStopwatchTicks(
                    Math.Clamp(
                        _settings
                            .ForegroundWindowFadeMilliseconds,
                        100,
                        1_000));
            var elapsed =
                now -
                _foregroundRevealTicks;

            if (elapsed >=
                foregroundHold +
                foregroundFade)
            {
                _foregroundBounds =
                    DrawingRectangle.Empty;
                _foregroundRevealTicks = 0;
                _maskDirty = true;
            }
            else if (elapsed >=
                     foregroundHold)
            {
                animationActive = true;
            }
        }

        if (_mouseTrail.Count > 0)
        {
            animationActive = true;
        }

        return animationActive;
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
                    : BuildMaskRegions(
                        now);
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

    private List<MaskRegion> BuildMaskRegions(
        long now)
    {
        var result =
            new List<MaskRegion>(
                _activeZones.Count +
                _manualRevealZones.Count +
                1);
        var maximumOpacity =
            _settings
                .MaximumMaskOpacity;

        foreach (var zone in
                 _activeZones)
        {
            result.Add(
                new MaskRegion(
                    ToNormalizedRect(
                        zone.Bounds),
                    ComputeOpacity(
                        now -
                        zone.LastActivityTicks,
                        Math.Clamp(
                            _settings
                                .MotionZoneRecurringHoldMilliseconds,
                            500,
                            10_000),
                        Math.Clamp(
                            _settings
                                .MotionZoneTransientFadeMilliseconds,
                            80,
                            1_000),
                        maximumOpacity)));
        }

        if (!_foregroundBounds.IsEmpty)
        {
            result.Add(
                new MaskRegion(
                    ToNormalizedRect(
                        _foregroundBounds),
                    ComputeOpacity(
                        now -
                        _foregroundRevealTicks,
                        Math.Clamp(
                            _settings
                                .ForegroundWindowRevealMilliseconds,
                            400,
                            5_000),
                        Math.Clamp(
                            _settings
                                .ForegroundWindowFadeMilliseconds,
                            100,
                            1_000),
                        maximumOpacity)));
        }

        AppendManualZones(
            result);

        return result;
    }

    private void AppendManualZones(
        ICollection<MaskRegion> result)
    {
        var screenBounds =
            _screen.Bounds;

        foreach (var normalized in
                 _manualRevealZones)
        {
            var absolute =
                DrawingRectangle.FromLTRB(
                    screenBounds.Left +
                    (int)Math.Floor(
                        normalized.Left *
                        screenBounds.Width),
                    screenBounds.Top +
                    (int)Math.Floor(
                        normalized.Top *
                        screenBounds.Height),
                    screenBounds.Left +
                    (int)Math.Ceiling(
                        normalized.Right *
                        screenBounds.Width),
                    screenBounds.Top +
                    (int)Math.Ceiling(
                        normalized.Bottom *
                        screenBounds.Height));
            var visible =
                DrawingRectangle.Intersect(
                    absolute,
                    _protectionBounds);

            if (visible.Width <= 0 ||
                visible.Height <= 0)
            {
                continue;
            }

            result.Add(
                new MaskRegion(
                    ToNormalizedRect(
                        ToLocalBounds(
                            visible)),
                    0.0));
        }
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

        var radius =
            Math.Clamp(
                _settings
                    .MouseVisualRadiusPixels,
                8,
                48);

        result.Add(
            CreateMouseReveal(
                _cursorX,
                _cursorY,
                radius));

        var lifetimeMilliseconds =
            Math.Clamp(
                _settings
                    .MouseTrailMilliseconds,
                0,
                250);

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

            result.Add(
                CreateMouseReveal(
                    sample.X,
                    sample.Y,
                    Math.Max(
                        4.0,
                        radius *
                        (0.35 +
                         0.65 *
                         life))));
        }

        return result;
    }

    private MouseReveal CreateMouseReveal(
        double localX,
        double localY,
        double radius)
    {
        return new MouseReveal(
            new System.Windows.Point(
                localX /
                Math.Max(
                    1.0,
                    _protectionBounds.Width),
                localY /
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
                _protectionBounds.Height));
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

    private bool IsCursorNear(
        DrawingRectangle bounds,
        int margin)
    {
        if (!_hasCursor)
        {
            return false;
        }

        var expanded =
            bounds;
        expanded.Inflate(
            margin,
            margin);

        return expanded.Contains(
            (int)Math.Round(
                _cursorX),
            (int)Math.Round(
                _cursorY));
    }

    private static bool RectanglesNear(
        DrawingRectangle first,
        DrawingRectangle second,
        int maximumGap)
    {
        var expanded =
            first;
        expanded.Inflate(
            maximumGap,
            maximumGap);

        return expanded.IntersectsWith(
                   second) ||
               expanded.Contains(
                   second);
    }

    private DrawingRectangle ClampLocalBounds(
        DrawingRectangle bounds)
    {
        var local =
            new DrawingRectangle(
                0,
                0,
                _protectionBounds.Width,
                _protectionBounds.Height);

        return DrawingRectangle.Intersect(
            bounds,
            local);
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
        DrawingRectangle local)
    {
        return new WpfRect(
            local.Left /
            (double)Math.Max(
                1,
                _protectionBounds.Width),
            local.Top /
            (double)Math.Max(
                1,
                _protectionBounds.Height),
            local.Width /
            (double)Math.Max(
                1,
                _protectionBounds.Width),
            local.Height /
            (double)Math.Max(
                1,
                _protectionBounds.Height));
    }

    private static WpfRect NormalizeRect(
        WpfRect rect)
    {
        var left =
            Math.Clamp(
                rect.Left,
                0.0,
                1.0);
        var top =
            Math.Clamp(
                rect.Top,
                0.0,
                1.0);
        var right =
            Math.Clamp(
                rect.Right,
                0.0,
                1.0);
        var bottom =
            Math.Clamp(
                rect.Bottom,
                0.0,
                1.0);

        return right <= left ||
               bottom <= top
            ? WpfRect.Empty
            : new WpfRect(
                left,
                top,
                right - left,
                bottom - top);
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
