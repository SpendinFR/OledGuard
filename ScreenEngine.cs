using System.Diagnostics;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace OledGuardNeuf;

internal sealed class ScreenEngine : IDisposable
{
    private const int DetectionIntervalMilliseconds = 20;
    private const int SampleWidthLimit = 960;
    private const int CellSize = 4;
    private const int PixelDifferenceThreshold = 9;

    private const int ActiveWindowVisibleMilliseconds = 3_000;
    private const int SmallZoneVisibleMilliseconds = 3_000;
    private const int ValidationOneMilliseconds = 1_000;
    private const int ValidationThreeMilliseconds = 3_000;
    private const int ValidationFiveMilliseconds = 5_000;
    private const int ValidatedZoneVisibleMilliseconds = 30_000;
    private const int ResizeEveryMilliseconds = 500;
    private const int FadeMilliseconds = 250;
    private const int ValidationGapMilliseconds = 500;

    private const double MaximumMaskOpacity = 0.85;
    private const int MaximumZones = 32;

    private sealed class TimedZone
    {
        public DrawingRectangle Bounds;
        public DrawingRectangle RecentBounds;
        public bool HasRecentBounds;

        public long StartedTicks;
        public long LastSeenTicks;
        public long LastResizeTicks;

        public bool ConfirmedAtOneSecond;
        public bool ConfirmedAtThreeSeconds;
        public bool Validated;
    }

    private readonly record struct Component(
        DrawingRectangle Bounds);

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
    private readonly int[] _componentQueue;

    private readonly int[] _colorMarks;
    private readonly int[] _colorQueue;
    private int _colorGeneration = 1;

    private readonly List<TimedZone> _zones =
        new();

    private readonly object _sync =
        new();

    private readonly CancellationTokenSource _cancellation =
        new();

    private Task? _loop;
    private bool _enabled = true;
    private bool _hasPreviousFrame;
    private bool _disposed;

    private IntPtr _lastForegroundWindow;
    private DrawingRectangle _activeWindowBounds;
    private long _activeWindowTicks;
    private long _ignoreDetectionUntilTicks;

    private bool _cursorInside;
    private double _cursorX;
    private double _cursorY;
    private DrawingRectangle _cursorElementBounds;

    public ScreenEngine(
        FormsScreen screen)
    {
        _screen =
            screen;

        _bounds =
            screen.WorkingArea.Width > 0 &&
            screen.WorkingArea.Height > 0
                ? screen.WorkingArea
                : screen.Bounds;

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

        _componentQueue =
            new int[
                _changedCells.Length];

        _colorMarks =
            new int[
                checked(
                    _sampleWidth *
                    _sampleHeight)];

        _colorQueue =
            new int[
                _colorMarks.Length];

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
        _overlay.ShowOverlay();

        _loop =
            Task.Run(
                RunAsync);
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

            _lastForegroundWindow =
                IntPtr.Zero;

            _activeWindowBounds =
                DrawingRectangle.Empty;

            _activeWindowTicks =
                0;

            _cursorElementBounds =
                DrawingRectangle.Empty;
        }

        Publish(
            Stopwatch.GetTimestamp());
    }

    private async Task RunAsync()
    {
        while (!_cancellation
                   .IsCancellationRequested)
        {
            var started =
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

                    lock (_sync)
                    {
                        if (_enabled)
                        {
                            Process(
                                frame,
                                started);
                        }
                    }
                }

                Publish(
                    started);
            }
            catch
            {
            }

            var elapsedMilliseconds =
                (Stopwatch.GetTimestamp() -
                 started) *
                1000.0 /
                Stopwatch.Frequency;

            var delay =
                Math.Max(
                    1,
                    DetectionIntervalMilliseconds -
                    (int)Math.Round(
                        elapsedMilliseconds));

            try
            {
                await Task.Delay(
                        delay,
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

    private void Process(
        byte[] frame,
        long now)
    {
        ReadCursor();

        var foreground =
            NativeMethods.GetForegroundWindow();

        if (foreground !=
            _lastForegroundWindow)
        {
            _lastForegroundWindow =
                foreground;

            RevealActiveWindow(
                foreground,
                now);

            _ignoreDetectionUntilTicks =
                now +
                ToTicks(
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
                Array.Empty<Component>());

            return;
        }

        var changedCellCount =
            MarkChangedCells(
                frame);

        IReadOnlyList<Component> components =
            Array.Empty<Component>();

        var changedFraction =
            changedCellCount /
            (double)Math.Max(
                1,
                _changedCells.Length);

        if (now >=
                _ignoreDetectionUntilTicks &&
            changedFraction <
                0.18)
        {
            components =
                FindConnectedComponents();

            UpdateZones(
                components,
                now);
        }
        else if (changedFraction >=
                 0.18)
        {
            RevealActiveWindow(
                foreground,
                now);

            _ignoreDetectionUntilTicks =
                now +
                ToTicks(
                    120);
        }

        UpdateCursorElement(
            frame,
            components);

        RemoveExpiredZones(
            now);

        CopyFrame(
            frame);
    }

    private int MarkChangedCells(
        byte[] frame)
    {
        Array.Clear(
            _changedCells,
            0,
            _changedCells.Length);

        var changed =
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

                var changedSamples =
                    0;

                changedSamples +=
                    PixelChanged(
                        frame,
                        left,
                        top)
                        ? 1
                        : 0;

                changedSamples +=
                    PixelChanged(
                        frame,
                        right,
                        top)
                        ? 1
                        : 0;

                changedSamples +=
                    PixelChanged(
                        frame,
                        left,
                        bottom)
                        ? 1
                        : 0;

                changedSamples +=
                    PixelChanged(
                        frame,
                        right,
                        bottom)
                        ? 1
                        : 0;

                changedSamples +=
                    PixelChanged(
                        frame,
                        centerX,
                        centerY)
                        ? 1
                        : 0;

                if (changedSamples <
                    2)
                {
                    continue;
                }

                _changedCells[
                    row *
                    _columns +
                    column] =
                        true;

                changed++;
            }
        }

        return changed;
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
               PixelDifferenceThreshold;
    }

    private List<Component>
        FindConnectedComponents()
    {
        Array.Clear(
            _visitedCells,
            0,
            _visitedCells.Length);

        var components =
            new List<Component>();

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

                _componentQueue[tail++] =
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

                var count =
                    0;

                while (head < tail)
                {
                    var packed =
                        _componentQueue[head++];

                    var currentRow =
                        packed /
                        _columns;

                    var currentColumn =
                        packed %
                        _columns;

                    count++;

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

                    AddConnectedNeighbor(
                        currentRow - 1,
                        currentColumn,
                        ref tail);

                    AddConnectedNeighbor(
                        currentRow + 1,
                        currentColumn,
                        ref tail);

                    AddConnectedNeighbor(
                        currentRow,
                        currentColumn - 1,
                        ref tail);

                    AddConnectedNeighbor(
                        currentRow,
                        currentColumn + 1,
                        ref tail);
                }

                if (count <
                    2)
                {
                    continue;
                }

                var bounds =
                    CellsToLocalRectangle(
                        minimumRow,
                        maximumRow,
                        minimumColumn,
                        maximumColumn);

                if (bounds.Width < 4 ||
                    bounds.Height < 4)
                {
                    continue;
                }

                components.Add(
                    new Component(
                        bounds));
            }
        }

        return components;
    }

    private void AddConnectedNeighbor(
        int row,
        int column,
        ref int tail)
    {
        if (row < 0 ||
            row >= _rows ||
            column < 0 ||
            column >= _columns)
        {
            return;
        }

        var index =
            row *
            _columns +
            column;

        if (!_changedCells[index] ||
            _visitedCells[index])
        {
            return;
        }

        _visitedCells[index] =
            true;

        _componentQueue[tail++] =
            index;
    }

    private void UpdateZones(
        IReadOnlyList<Component> components,
        long now)
    {
        var alreadyMatched =
            new HashSet<TimedZone>();

        foreach (var component in
                 components)
        {
            var zone =
                FindOverlappingZone(
                    component.Bounds,
                    alreadyMatched);

            if (zone is null)
            {
                zone =
                    new TimedZone
                    {
                        Bounds =
                            PadAndClamp(
                                component.Bounds,
                                3),
                        RecentBounds =
                            component.Bounds,
                        HasRecentBounds =
                            true,
                        StartedTicks =
                            now,
                        LastSeenTicks =
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
                                item =>
                                    item.LastSeenTicks)
                            .First();

                    _zones.Remove(
                        oldest);
                }
            }
            else
            {
                alreadyMatched.Add(
                    zone);

                if (!zone.Validated &&
                    now -
                        zone.LastSeenTicks >
                    ToTicks(
                        ValidationGapMilliseconds))
                {
                    zone.StartedTicks =
                        now;

                    zone.ConfirmedAtOneSecond =
                        false;

                    zone.ConfirmedAtThreeSeconds =
                        false;
                }

                zone.LastSeenTicks =
                    now;

                zone.RecentBounds =
                    zone.HasRecentBounds
                        ? DrawingRectangle.Union(
                            zone.RecentBounds,
                            component.Bounds)
                        : component.Bounds;

                zone.HasRecentBounds =
                    true;

                zone.Bounds =
                    PadAndClamp(
                        DrawingRectangle.Union(
                            zone.Bounds,
                            component.Bounds),
                        0);
            }

            var age =
                now -
                zone.StartedTicks;

            if (age >=
                ToTicks(
                    ValidationOneMilliseconds))
            {
                zone.ConfirmedAtOneSecond =
                    true;
            }

            if (age >=
                ToTicks(
                    ValidationThreeMilliseconds))
            {
                zone.ConfirmedAtThreeSeconds =
                    true;
            }

            if (!zone.Validated &&
                age >=
                    ToTicks(
                        ValidationFiveMilliseconds) &&
                zone.ConfirmedAtOneSecond &&
                zone.ConfirmedAtThreeSeconds)
            {
                zone.Validated =
                    true;
            }
        }

        foreach (var zone in
                 _zones)
        {
            if (now -
                    zone.LastResizeTicks <
                ToTicks(
                    ResizeEveryMilliseconds))
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

    private TimedZone? FindOverlappingZone(
        DrawingRectangle component,
        ISet<TimedZone> alreadyMatched)
    {
        TimedZone? best =
            null;

        var bestArea =
            0;

        foreach (var zone in
                 _zones)
        {
            if (alreadyMatched.Contains(
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
                bestArea)
            {
                continue;
            }

            best =
                zone;

            bestArea =
                area;
        }

        return bestArea > 0
            ? best
            : null;
    }

    private void RemoveExpiredZones(
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

            var hold =
                zone.Validated
                    ? ValidatedZoneVisibleMilliseconds
                    : SmallZoneVisibleMilliseconds;

            if (now -
                    zone.LastSeenTicks >
                ToTicks(
                    hold +
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
            _cursorInside =
                false;

            _cursorElementBounds =
                DrawingRectangle.Empty;

            return;
        }

        _cursorInside =
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
        IReadOnlyList<Component> components)
    {
        if (!_cursorInside)
        {
            _cursorElementBounds =
                DrawingRectangle.Empty;

            return;
        }

        foreach (var component in
                 components)
        {
            var bounds =
                component.Bounds;

            bounds.Inflate(
                10,
                10);

            if (!bounds.Contains(
                    (int)Math.Round(
                        _cursorX),
                    (int)Math.Round(
                        _cursorY)))
            {
                continue;
            }

            _cursorElementBounds =
                PadAndClamp(
                    component.Bounds,
                    4);

            return;
        }

        _cursorElementBounds =
            FindColorConnectedElement(
                frame) ??
            DrawingRectangle.Empty;
    }

    private DrawingRectangle?
        FindColorConnectedElement(
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
            frame[seedIndex + 1];

        var seedRed =
            frame[seedIndex + 2];

        BeginColorGeneration();

        var head = 0;
        var tail = 0;

        var start =
            sampleY *
            _sampleWidth +
            sampleX;

        _colorQueue[tail++] =
            start;

        _colorMarks[start] =
            _colorGeneration;

        var minimumX =
            sampleX;

        var maximumX =
            sampleX;

        var minimumY =
            sampleY;

        var maximumY =
            sampleY;

        var count =
            0;

        var touchedLeft =
            false;

        var touchedRight =
            false;

        var touchedTop =
            false;

        var touchedBottom =
            false;

        while (head < tail)
        {
            var packed =
                _colorQueue[head++];

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
                x == left;

            touchedRight |=
                x == right;

            touchedTop |=
                y == top;

            touchedBottom |=
                y == bottom;

            AddColorNeighbor(
                frame,
                x - 1,
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

            AddColorNeighbor(
                frame,
                x + 1,
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

            AddColorNeighbor(
                frame,
                x,
                y - 1,
                left,
                top,
                right,
                bottom,
                seedBlue,
                seedGreen,
                seedRed,
                colorThreshold,
                ref tail);

            AddColorNeighbor(
                frame,
                x,
                y + 1,
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
            SampleToLocalRectangle(
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

    private void AddColorNeighbor(
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

        if (_colorMarks[packed] ==
            _colorGeneration)
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
                        frame[index + 1] -
                        seedGreen),
                    Math.Abs(
                        frame[index + 2] -
                        seedRed)));

        if (difference >
            threshold)
        {
            return;
        }

        _colorMarks[packed] =
            _colorGeneration;

        _colorQueue[tail++] =
            packed;
    }

    private void BeginColorGeneration()
    {
        _colorGeneration++;

        if (_colorGeneration <
            int.MaxValue)
        {
            return;
        }

        Array.Clear(
            _colorMarks,
            0,
            _colorMarks.Length);

        _colorGeneration =
            1;
    }

    private void RevealActiveWindow(
        IntPtr window,
        long now)
    {
        if (window ==
                IntPtr.Zero ||
            IsShellWindow(
                window) ||
            !NativeMethods.GetWindowRect(
                window,
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

        _activeWindowBounds =
            new DrawingRectangle(
                visible.Left -
                    _bounds.Left,
                visible.Top -
                    _bounds.Top,
                visible.Width,
                visible.Height);

        _activeWindowTicks =
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

    private void Publish(
        long now)
    {
        List<VisibleRectangle> rectangles;
        CursorReveal? cursorReveal;
        double opacity;

        lock (_sync)
        {
            if (!_enabled)
            {
                rectangles =
                    new List<VisibleRectangle>();

                cursorReveal =
                    null;

                opacity =
                    0.0;
            }
            else
            {
                rectangles =
                    BuildVisibleRectangles(
                        now);

                cursorReveal =
                    BuildCursorReveal();

                opacity =
                    MaximumMaskOpacity;
            }
        }

        _overlay.Render(
            opacity,
            rectangles,
            cursorReveal);
    }

    private List<VisibleRectangle>
        BuildVisibleRectangles(
            long now)
    {
        var result =
            new List<VisibleRectangle>(
                _zones.Count +
                2);

        foreach (var zone in
                 _zones)
        {
            var hold =
                zone.Validated
                    ? ValidatedZoneVisibleMilliseconds
                    : SmallZoneVisibleMilliseconds;

            result.Add(
                new VisibleRectangle(
                    Normalize(
                        zone.Bounds),
                    FadeOpacity(
                        now -
                            zone.LastSeenTicks,
                        hold)));
        }

        if (!_activeWindowBounds.IsEmpty &&
            now -
                _activeWindowTicks <=
            ToTicks(
                ActiveWindowVisibleMilliseconds +
                FadeMilliseconds))
        {
            result.Add(
                new VisibleRectangle(
                    Normalize(
                        _activeWindowBounds),
                    FadeOpacity(
                        now -
                            _activeWindowTicks,
                        ActiveWindowVisibleMilliseconds)));
        }

        if (!_cursorElementBounds.IsEmpty)
        {
            result.Add(
                new VisibleRectangle(
                    Normalize(
                        _cursorElementBounds),
                    0.0));
        }

        return result;
    }

    private CursorReveal? BuildCursorReveal()
    {
        if (!_cursorInside)
        {
            return null;
        }

        const double radius =
            18.0;

        return new CursorReveal(
            new WpfPoint(
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
                _bounds.Height));
    }

    private DrawingRectangle
        CellsToLocalRectangle(
            int minimumRow,
            int maximumRow,
            int minimumColumn,
            int maximumColumn)
    {
        return SampleToLocalRectangle(
            minimumColumn *
                CellSize,
            minimumRow *
                CellSize,
            Math.Min(
                _sampleWidth,
                (maximumColumn +
                    1) *
                CellSize),
            Math.Min(
                _sampleHeight,
                (maximumRow +
                    1) *
                CellSize));
    }

    private DrawingRectangle
        SampleToLocalRectangle(
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

    private WpfRect Normalize(
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

    private static double FadeOpacity(
        long elapsedTicks,
        int holdMilliseconds)
    {
        var holdTicks =
            ToTicks(
                holdMilliseconds);

        if (elapsedTicks <=
            holdTicks)
        {
            return 0.0;
        }

        var fadeTicks =
            Math.Max(
                1L,
                ToTicks(
                    FadeMilliseconds));

        var progress =
            Math.Clamp(
                (elapsedTicks -
                    holdTicks) /
                (double)fadeTicks,
                0.0,
                1.0);

        return MaximumMaskOpacity *
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

    private static long ToTicks(
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
