using System.Diagnostics;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;
using WpfRect = System.Windows.Rect;

namespace OledGuardSimple;

internal sealed class DetectionEngine : IDisposable
{
    private const int CaptureIntervalMilliseconds = 20;
    private const int SampleWidthLimit = 960;
    private const int CellSize = 4;
    private const int PixelThreshold = 9;

    private const int SmallZoneHoldMilliseconds = 3_000;
    private const int ValidationOneMilliseconds = 1_000;
    private const int ValidationThreeMilliseconds = 3_000;
    private const int ValidationFiveMilliseconds = 5_000;
    private const int ValidatedZoneHoldMilliseconds = 30_000;
    private const int FadeMilliseconds = 300;
    private const int ResizeIntervalMilliseconds = 500;
    private const int ActivityGapMilliseconds = 500;

    private const int ForegroundHoldMilliseconds = 3_000;
    private const int MaximumZones = 32;
    private const double MaximumOpacity = 0.85;

    private sealed class Zone
    {
        public DrawingRectangle Bounds;
        public DrawingRectangle RecentBounds;
        public bool HasRecentBounds;

        public long FirstActivityTicks;
        public long LastActivityTicks;
        public long LastResizeTicks;

        public bool SeenAtOneSecond;
        public bool SeenAtThreeSeconds;
        public bool Validated;
    }

    private readonly record struct MotionComponent(
        DrawingRectangle Bounds);

    private readonly record struct SceneSnapshot(
        double MaximumOpacity,
        RevealRegion[] Regions,
        CursorHole[] CursorHoles);

    private readonly FormsScreen _screen;
    private readonly DrawingRectangle _bounds;
    private readonly OverlayWindow _overlay;
    private readonly ScreenCapture _capture;

    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly int _columns;
    private readonly int _rows;

    private readonly byte[] _previousFrame;
    private readonly bool[] _changedCells;
    private readonly bool[] _visitedCells;
    private readonly int[] _cellQueue;

    private readonly int[] _pixelMarks;
    private readonly int[] _pixelQueue;
    private int _pixelGeneration = 1;

    private readonly List<Zone> _zones =
        new();

    private readonly object _sync =
        new();

    private readonly CancellationTokenSource
        _cancellation =
            new();

    private Task? _loop;
    private bool _enabled = true;
    private bool _hasPreviousFrame;
    private bool _disposed;

    private IntPtr _lastForegroundWindow;
    private DrawingRectangle _foregroundBounds;
    private long _foregroundTicks;
    private long _ignoreMotionUntilTicks;

    private bool _hasCursor;
    private double _cursorX;
    private double _cursorY;
    private DrawingRectangle _cursorElementBounds;

    public DetectionEngine(
        FormsScreen screen)
    {
        _screen =
            screen;

        _bounds =
            GetProtectionBounds(
                screen);

        _sampleWidth =
            Math.Max(
                320,
                Math.Min(
                    SampleWidthLimit,
                    _bounds.Width));

        _sampleHeight =
            Math.Max(
                180,
                (int)Math.Round(
                    _bounds.Height *
                    _sampleWidth /
                    (double)Math.Max(
                        1,
                        _bounds.Width)));

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

        _previousFrame =
            new byte[
                checked(
                    _sampleStride *
                    _sampleHeight)];

        _changedCells =
            new bool[
                checked(
                    _columns *
                    _rows)];

        _visitedCells =
            new bool[
                _changedCells.Length];

        _cellQueue =
            new int[
                _changedCells.Length];

        _pixelMarks =
            new int[
                checked(
                    _sampleWidth *
                    _sampleHeight)];

        _pixelQueue =
            new int[
                _pixelMarks.Length];

        _overlay =
            new OverlayWindow(
                _bounds);

        _capture =
            new ScreenCapture(
                _bounds,
                _sampleWidth,
                _sampleHeight);
    }

    public void Start()
    {
        _overlay.EnsureVisible();

        _loop =
            Task.Run(
                CaptureLoopAsync);
    }

    public void SetEnabled(
        bool enabled)
    {
        lock (_sync)
        {
            _enabled =
                enabled;

            _hasPreviousFrame =
                false;

            _zones.Clear();

            _foregroundBounds =
                DrawingRectangle.Empty;

            _foregroundTicks =
                0;

            _lastForegroundWindow =
                IntPtr.Zero;

            _cursorElementBounds =
                DrawingRectangle.Empty;
        }

        Publish(
            Stopwatch.GetTimestamp());
    }

    private async Task CaptureLoopAsync()
    {
        while (!_cancellation
                   .IsCancellationRequested)
        {
            var iterationStart =
                Stopwatch.GetTimestamp();

            try
            {
                bool enabled;

                lock (_sync)
                {
                    enabled =
                        _enabled;
                }

                if (enabled)
                {
                    var frame =
                        _capture.Capture();

                    SceneSnapshot snapshot;

                    lock (_sync)
                    {
                        if (!_enabled)
                        {
                            snapshot =
                                BuildSnapshot(
                                    iterationStart);
                        }
                        else
                        {
                            ProcessFrame(
                                frame,
                                iterationStart);

                            snapshot =
                                BuildSnapshot(
                                    iterationStart);
                        }
                    }

                    _overlay.SetScene(
                        snapshot.MaximumOpacity,
                        snapshot.Regions,
                        snapshot.CursorHoles);
                }
                else
                {
                    Publish(
                        iterationStart);
                }
            }
            catch
            {
            }

            var elapsedMilliseconds =
                (Stopwatch.GetTimestamp() -
                 iterationStart) *
                1000.0 /
                Stopwatch.Frequency;

            var delayMilliseconds =
                Math.Max(
                    1,
                    CaptureIntervalMilliseconds -
                    (int)Math.Round(
                        elapsedMilliseconds));

            try
            {
                await Task.Delay(
                        delayMilliseconds,
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

    private void ProcessFrame(
        byte[] frame,
        long now)
    {
        ReadCursor();

        var foregroundWindow =
            NativeMethods.GetForegroundWindow();

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
                frame);

            _hasPreviousFrame =
                true;

            UpdateCursorElement(
                frame,
                Array.Empty<MotionComponent>());

            return;
        }

        var changedCount =
            DetectChangedCells(
                frame);

        var changedFraction =
            changedCount /
            (double)Math.Max(
                1,
                _changedCells.Length);

        IReadOnlyList<MotionComponent>
            components =
                Array.Empty<MotionComponent>();

        if (now >=
                _ignoreMotionUntilTicks &&
            changedFraction <
                0.18)
        {
            components =
                BuildConnectedComponents();

            UpdateZones(
                components,
                now);
        }
        else if (changedFraction >=
                 0.18)
        {
            RevealForegroundWindow(
                foregroundWindow,
                now);

            _ignoreMotionUntilTicks =
                now +
                ToStopwatchTicks(
                    120);
        }

        UpdateCursorElement(
            frame,
            components);

        ExpireZones(
            now);

        CopyFrame(
            frame);
    }

    private int DetectChangedCells(
        byte[] frame)
    {
        Array.Clear(
            _changedCells,
            0,
            _changedCells.Length);

        var changedCount =
            0;

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

                var samples =
                    0;

                samples +=
                    PixelChanged(
                        frame,
                        left,
                        top)
                        ? 1
                        : 0;

                samples +=
                    PixelChanged(
                        frame,
                        right,
                        top)
                        ? 1
                        : 0;

                samples +=
                    PixelChanged(
                        frame,
                        left,
                        bottom)
                        ? 1
                        : 0;

                samples +=
                    PixelChanged(
                        frame,
                        right,
                        bottom)
                        ? 1
                        : 0;

                samples +=
                    PixelChanged(
                        frame,
                        centerX,
                        centerY)
                        ? 1
                        : 0;

                if (samples < 2)
                {
                    continue;
                }

                _changedCells[
                    row *
                    _columns +
                    column] =
                        true;

                changedCount++;
            }
        }

        return changedCount;
    }

    private bool PixelChanged(
        byte[] frame,
        int x,
        int y)
    {
        var index =
            y *
            _sampleStride +
            x *
            4;

        var blue =
            Math.Abs(
                frame[index] -
                _previousFrame[index]);

        var green =
            Math.Abs(
                frame[index + 1] -
                _previousFrame[index + 1]);

        var red =
            Math.Abs(
                frame[index + 2] -
                _previousFrame[index + 2]);

        return Math.Max(
                   blue,
                   Math.Max(
                       green,
                       red)) >=
               PixelThreshold;
    }

    private List<MotionComponent>
        BuildConnectedComponents()
    {
        Array.Clear(
            _visitedCells,
            0,
            _visitedCells.Length);

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
                var start =
                    row *
                    _columns +
                    column;

                if (!_changedCells[start] ||
                    _visitedCells[start])
                {
                    continue;
                }

                var head = 0;
                var tail = 0;

                _cellQueue[tail++] =
                    start;

                _visitedCells[start] =
                    true;

                var minimumRow =
                    row;

                var maximumRow =
                    row;

                var minimumColumn =
                    column;

                var maximumColumn =
                    column;

                var cells =
                    0;

                while (head < tail)
                {
                    var packed =
                        _cellQueue[head++];

                    var currentRow =
                        packed /
                        _columns;

                    var currentColumn =
                        packed %
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

                            var next =
                                nextRow *
                                _columns +
                                nextColumn;

                            if (!_changedCells[next] ||
                                _visitedCells[next])
                            {
                                continue;
                            }

                            _visitedCells[next] =
                                true;

                            _cellQueue[tail++] =
                                next;
                        }
                    }
                }

                if (cells <
                    2)
                {
                    continue;
                }

                var bounds =
                    CellsToLocalBounds(
                        minimumRow,
                        maximumRow,
                        minimumColumn,
                        maximumColumn);

                if (bounds.Width < 4 ||
                    bounds.Height < 4)
                {
                    continue;
                }

                result.Add(
                    new MotionComponent(
                        bounds));
            }
        }

        return result;
    }

    private void UpdateZones(
        IReadOnlyList<MotionComponent> components,
        long now)
    {
        var used =
            new HashSet<Zone>();

        foreach (var component in
                 components)
        {
            var zone =
                FindMatchingZone(
                    component.Bounds,
                    used);

            if (zone is null)
            {
                zone =
                    new Zone
                    {
                        Bounds =
                            PadAndClamp(
                                component.Bounds,
                                3),
                        RecentBounds =
                            component.Bounds,
                        HasRecentBounds =
                            true,
                        FirstActivityTicks =
                            now,
                        LastActivityTicks =
                            now,
                        LastResizeTicks =
                            now
                    };

                _zones.Add(
                    zone);

                if (_zones.Count >
                    MaximumZones)
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
            }
            else
            {
                used.Add(
                    zone);

                if (!zone.Validated &&
                    now -
                        zone.LastActivityTicks >
                    ToStopwatchTicks(
                        ActivityGapMilliseconds))
                {
                    zone.FirstActivityTicks =
                        now;

                    zone.SeenAtOneSecond =
                        false;

                    zone.SeenAtThreeSeconds =
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

                var expanded =
                    PadAndClamp(
                        DrawingRectangle.Union(
                            zone.Bounds,
                            component.Bounds),
                        0);

                if (expanded !=
                    zone.Bounds)
                {
                    zone.Bounds =
                        expanded;
                }
            }

            var continuousAge =
                now -
                zone.FirstActivityTicks;

            if (continuousAge >=
                ToStopwatchTicks(
                    ValidationOneMilliseconds))
            {
                zone.SeenAtOneSecond =
                    true;
            }

            if (continuousAge >=
                ToStopwatchTicks(
                    ValidationThreeMilliseconds))
            {
                zone.SeenAtThreeSeconds =
                    true;
            }

            if (!zone.Validated &&
                continuousAge >=
                    ToStopwatchTicks(
                        ValidationFiveMilliseconds) &&
                zone.SeenAtOneSecond &&
                zone.SeenAtThreeSeconds)
            {
                zone.Validated =
                    true;
            }
        }

        ResizeZones(
            now);
    }

    private Zone? FindMatchingZone(
        DrawingRectangle component,
        ISet<Zone> used)
    {
        Zone? best =
            null;

        var bestIntersection =
            0;

        foreach (var zone in
                 _zones)
        {
            if (used.Contains(
                    zone))
            {
                continue;
            }

            var intersection =
                DrawingRectangle.Intersect(
                    zone.Bounds,
                    component);

            var area =
                Math.Max(
                    0,
                    intersection.Width) *
                Math.Max(
                    0,
                    intersection.Height);

            if (area <=
                bestIntersection)
            {
                continue;
            }

            best =
                zone;

            bestIntersection =
                area;
        }

        return bestIntersection >
                   0
            ? best
            : null;
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
                    ResizeIntervalMilliseconds))
            {
                continue;
            }

            zone.LastResizeTicks =
                now;

            if (!zone.HasRecentBounds)
            {
                continue;
            }

            zone.Bounds =
                PadAndClamp(
                    zone.RecentBounds,
                    zone.Validated
                        ? 8
                        : 3);

            zone.RecentBounds =
                DrawingRectangle.Empty;

            zone.HasRecentBounds =
                false;
        }
    }

    private void ExpireZones(
        long now)
    {
        for (var index =
                 _zones.Count -
                 1;
             index >= 0;
             index--)
        {
            var zone =
                _zones[index];

            var holdMilliseconds =
                zone.Validated
                    ? ValidatedZoneHoldMilliseconds
                    : SmallZoneHoldMilliseconds;

            if (now -
                    zone.LastActivityTicks >
                ToStopwatchTicks(
                    holdMilliseconds +
                    FadeMilliseconds))
            {
                _zones.RemoveAt(
                    index);
            }
        }
    }

    private void ReadCursor()
    {
        if (!NativeMethods.GetCursorPos(
                out var cursor) ||
            !_bounds.Contains(
                cursor.X,
                cursor.Y))
        {
            _hasCursor =
                false;

            _cursorElementBounds =
                DrawingRectangle.Empty;

            return;
        }

        _hasCursor =
            true;

        _cursorX =
            cursor.X -
            _bounds.Left;

        _cursorY =
            cursor.Y -
            _bounds.Top;
    }

    private void UpdateCursorElement(
        byte[] frame,
        IReadOnlyList<MotionComponent> components)
    {
        if (!_hasCursor)
        {
            _cursorElementBounds =
                DrawingRectangle.Empty;

            return;
        }

        MotionComponent? selected =
            null;

        var selectedArea =
            int.MaxValue;

        foreach (var component in
                 components)
        {
            var expanded =
                component.Bounds;

            expanded.Inflate(
                12,
                12);

            if (!expanded.Contains(
                    (int)Math.Round(
                        _cursorX),
                    (int)Math.Round(
                        _cursorY)))
            {
                continue;
            }

            var area =
                component.Bounds.Width *
                component.Bounds.Height;

            if (area >=
                selectedArea)
            {
                continue;
            }

            selected =
                component;

            selectedArea =
                area;
        }

        if (selected is not null)
        {
            _cursorElementBounds =
                PadAndClamp(
                    selected.Value.Bounds,
                    4);

            return;
        }

        _cursorElementBounds =
            ProbeColorConnectedElement(
                frame) ??
            DrawingRectangle.Empty;
    }

    private DrawingRectangle?
        ProbeColorConnectedElement(
            byte[] frame)
    {
        var sampleX =
            Math.Clamp(
                (int)Math.Round(
                    _cursorX *
                    _sampleWidth /
                    Math.Max(
                        1.0,
                        _bounds.Width)),
                0,
                _sampleWidth -
                1);

        var sampleY =
            Math.Clamp(
                (int)Math.Round(
                    _cursorY *
                    _sampleHeight /
                    Math.Max(
                        1.0,
                        _bounds.Height)),
                0,
                _sampleHeight -
                1);

        const int radiusX = 48;
        const int radiusY = 32;
        const int colorThreshold = 14;

        var left =
            Math.Max(
                0,
                sampleX -
                radiusX);

        var right =
            Math.Min(
                _sampleWidth -
                1,
                sampleX +
                radiusX);

        var top =
            Math.Max(
                0,
                sampleY -
                radiusY);

        var bottom =
            Math.Min(
                _sampleHeight -
                1,
                sampleY +
                radiusY);

        var seedIndex =
            sampleY *
            _sampleStride +
            sampleX *
            4;

        var seedBlue =
            frame[seedIndex];

        var seedGreen =
            frame[seedIndex +
                  1];

        var seedRed =
            frame[seedIndex +
                  2];

        BeginPixelGeneration();

        var head = 0;
        var tail = 0;

        var start =
            sampleY *
            _sampleWidth +
            sampleX;

        _pixelQueue[tail++] =
            start;

        _pixelMarks[start] =
            _pixelGeneration;

        var minimumX =
            sampleX;

        var maximumX =
            sampleX;

        var minimumY =
            sampleY;

        var maximumY =
            sampleY;

        var count = 0;
        var touchedLeft = false;
        var touchedRight = false;
        var touchedTop = false;
        var touchedBottom = false;

        while (head < tail)
        {
            var packed =
                _pixelQueue[head++];

            var x =
                packed %
                _sampleWidth;

            var y =
                packed /
                _sampleWidth;

            count++;

            minimumX =
                Math.Min(
                    minimumX,
                    x);

            maximumX =
                Math.Max(
                    maximumX,
                    x);

            minimumY =
                Math.Min(
                    minimumY,
                    y);

            maximumY =
                Math.Max(
                    maximumY,
                    y);

            touchedLeft |=
                x ==
                left;

            touchedRight |=
                x ==
                right;

            touchedTop |=
                y ==
                top;

            touchedBottom |=
                y ==
                bottom;

            VisitColorPixel(
                frame,
                x -
                1,
                y,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);

            VisitColorPixel(
                frame,
                x +
                1,
                y,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);

            VisitColorPixel(
                frame,
                x,
                y -
                1,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);

            VisitColorPixel(
                frame,
                x,
                y +
                1,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);
        }

        var touchedBorders =
            (touchedLeft
                ? 1
                : 0) +
            (touchedRight
                ? 1
                : 0) +
            (touchedTop
                ? 1
                : 0) +
            (touchedBottom
                ? 1
                : 0);

        var roiArea =
            (right -
             left +
             1) *
            (bottom -
             top +
             1);

        var width =
            maximumX -
            minimumX +
            1;

        var height =
            maximumY -
            minimumY +
            1;

        if (count < 8 ||
            count >
                roiArea *
                0.40 ||
            touchedBorders >=
                2 ||
            width < 2 ||
            height < 2)
        {
            return null;
        }

        var local =
            SampleToLocalBounds(
                minimumX,
                minimumY,
                maximumX +
                1,
                maximumY +
                1);

        if (local.Width >
                420 ||
            local.Height >
                260)
        {
            return null;
        }

        return PadAndClamp(
            local,
            4);
    }

    private void VisitColorPixel(
        byte[] frame,
        int x,
        int y,
        int left,
        int top,
        int right,
        int bottom,
        byte seedBlue,
        byte seedGreen,
        byte seedRed,
        int threshold,
        ref int tail)
    {
        if (x < left ||
            x > right ||
            y < top ||
            y > bottom)
        {
            return;
        }

        var packed =
            y *
            _sampleWidth +
            x;

        if (_pixelMarks[packed] ==
            _pixelGeneration)
        {
            return;
        }

        var index =
            y *
            _sampleStride +
            x *
            4;

        var difference =
            Math.Max(
                Math.Abs(
                    frame[index] -
                    seedBlue),
                Math.Max(
                    Math.Abs(
                        frame[index +
                              1] -
                        seedGreen),
                    Math.Abs(
                        frame[index +
                              2] -
                        seedRed)));

        if (difference >
            threshold)
        {
            return;
        }

        _pixelMarks[packed] =
            _pixelGeneration;

        _pixelQueue[tail++] =
            packed;
    }

    private void BeginPixelGeneration()
    {
        _pixelGeneration++;

        if (_pixelGeneration !=
            int.MaxValue)
        {
            return;
        }

        Array.Clear(
            _pixelMarks,
            0,
            _pixelMarks.Length);

        _pixelGeneration =
            1;
    }

    private void RevealForegroundWindow(
        IntPtr foregroundWindow,
        long now)
    {
        if (foregroundWindow ==
                IntPtr.Zero ||
            IsShellWindow(
                foregroundWindow) ||
            !NativeMethods.GetWindowRect(
                foregroundWindow,
                out var nativeBounds))
        {
            return;
        }

        var absolute =
            DrawingRectangle.FromLTRB(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Right,
                nativeBounds.Bottom);

        var visible =
            DrawingRectangle.Intersect(
                absolute,
                _bounds);

        if (visible.Width < 40 ||
            visible.Height < 30)
        {
            return;
        }

        _foregroundBounds =
            new DrawingRectangle(
                visible.Left -
                _bounds.Left,
                visible.Top -
                _bounds.Top,
                visible.Width,
                visible.Height);

        _foregroundTicks =
            now;
    }

    private static bool IsShellWindow(
        IntPtr window)
    {
        var className =
            new System.Text.StringBuilder(
                128);

        if (NativeMethods.GetClassName(
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

    private SceneSnapshot BuildSnapshot(
        long now)
    {
        if (!_enabled)
        {
            return new SceneSnapshot(
                0.0,
                Array.Empty<RevealRegion>(),
                Array.Empty<CursorHole>());
        }

        var regions =
            new List<RevealRegion>(
                _zones.Count +
                2);

        foreach (var zone in
                 _zones)
        {
            var holdMilliseconds =
                zone.Validated
                    ? ValidatedZoneHoldMilliseconds
                    : SmallZoneHoldMilliseconds;

            regions.Add(
                new RevealRegion(
                    ToNormalizedRect(
                        zone.Bounds),
                    ComputeOpacity(
                        now -
                        zone.LastActivityTicks,
                        holdMilliseconds)));
        }

        if (!_foregroundBounds.IsEmpty &&
            now -
                _foregroundTicks <=
            ToStopwatchTicks(
                ForegroundHoldMilliseconds +
                FadeMilliseconds))
        {
            regions.Add(
                new RevealRegion(
                    ToNormalizedRect(
                        _foregroundBounds),
                    ComputeOpacity(
                        now -
                        _foregroundTicks,
                        ForegroundHoldMilliseconds)));
        }

        if (!_cursorElementBounds.IsEmpty)
        {
            regions.Add(
                new RevealRegion(
                    ToNormalizedRect(
                        _cursorElementBounds),
                    0.0));
        }

        CursorHole[] cursorHoles;

        if (_hasCursor)
        {
            const double radius =
                18.0;

            cursorHoles =
            [
                new CursorHole(
                    new System.Windows.Point(
                        _cursorX /
                        Math.Max(
                            1.0,
                            _bounds.Width),
                        _cursorY /
                        Math.Max(
                            1.0,
                            _bounds.Height)),
                    radius /
                    Math.Max(
                        1.0,
                        _bounds.Width),
                    radius /
                    Math.Max(
                        1.0,
                        _bounds.Height))
            ];
        }
        else
        {
            cursorHoles =
                Array.Empty<CursorHole>();
        }

        return new SceneSnapshot(
            MaximumOpacity,
            regions.ToArray(),
            cursorHoles);
    }

    private void Publish(
        long now)
    {
        SceneSnapshot snapshot;

        lock (_sync)
        {
            snapshot =
                BuildSnapshot(
                    now);
        }

        _overlay.SetScene(
            snapshot.MaximumOpacity,
            snapshot.Regions,
            snapshot.CursorHoles);
    }

    private DrawingRectangle CellsToLocalBounds(
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
                (maximumColumn +
                 1) *
                CellSize);

        var sampleBottom =
            Math.Min(
                _sampleHeight,
                (maximumRow +
                 1) *
                CellSize);

        return SampleToLocalBounds(
            sampleLeft,
            sampleTop,
            sampleRight,
            sampleBottom);
    }

    private DrawingRectangle SampleToLocalBounds(
        int sampleLeft,
        int sampleTop,
        int sampleRight,
        int sampleBottom)
    {
        var left =
            (int)Math.Floor(
                sampleLeft *
                _bounds.Width /
                (double)Math.Max(
                    1,
                    _sampleWidth));

        var top =
            (int)Math.Floor(
                sampleTop *
                _bounds.Height /
                (double)Math.Max(
                    1,
                    _sampleHeight));

        var right =
            (int)Math.Ceiling(
                sampleRight *
                _bounds.Width /
                (double)Math.Max(
                    1,
                    _sampleWidth));

        var bottom =
            (int)Math.Ceiling(
                sampleBottom *
                _bounds.Height /
                (double)Math.Max(
                    1,
                    _sampleHeight));

        return DrawingRectangle.FromLTRB(
            left,
            top,
            right,
            bottom);
    }

    private DrawingRectangle PadAndClamp(
        DrawingRectangle rectangle,
        int padding)
    {
        rectangle.Inflate(
            padding,
            padding);

        return DrawingRectangle.Intersect(
            rectangle,
            new DrawingRectangle(
                0,
                0,
                _bounds.Width,
                _bounds.Height));
    }

    private WpfRect ToNormalizedRect(
        DrawingRectangle rectangle)
    {
        return new WpfRect(
            rectangle.Left /
            (double)Math.Max(
                1,
                _bounds.Width),
            rectangle.Top /
            (double)Math.Max(
                1,
                _bounds.Height),
            rectangle.Width /
            (double)Math.Max(
                1,
                _bounds.Width),
            rectangle.Height /
            (double)Math.Max(
                1,
                _bounds.Height));
    }

    private static double ComputeOpacity(
        long elapsedTicks,
        int holdMilliseconds)
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
                    FadeMilliseconds));

        var progress =
            Math.Clamp(
                (elapsedTicks -
                 holdTicks) /
                (double)fadeTicks,
                0.0,
                1.0);

        return MaximumOpacity *
               progress;
    }

    private void CopyFrame(
        byte[] frame)
    {
        Buffer.BlockCopy(
            frame,
            0,
            _previousFrame,
            0,
            frame.Length);
    }

    private static DrawingRectangle
        GetProtectionBounds(
            FormsScreen screen)
    {
        var workingArea =
            screen.WorkingArea;

        if (workingArea.Width > 0 &&
            workingArea.Height > 0 &&
            workingArea !=
            screen.Bounds)
        {
            return workingArea;
        }

        var bounds =
            screen.Bounds;

        return DrawingRectangle.FromLTRB(
            bounds.Left,
            bounds.Top,
            bounds.Right,
            Math.Max(
                bounds.Top +
                1,
                bounds.Bottom -
                4));
    }

    private static long ToStopwatchTicks(
        double milliseconds)
    {
        return (long)(
            milliseconds *
            Stopwatch.Frequency /
            1000.0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed =
            true;

        _cancellation.Cancel();

        try
        {
            _loop?.Wait(
                500);
        }
        catch
        {
        }

        _capture.Dispose();

        _overlay.Dispatcher.Invoke(
            () =>
                _overlay.Close());

        _cancellation.Dispose();
    }
}
