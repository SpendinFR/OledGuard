using System.Diagnostics;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace OledGuardFresh;

internal sealed class FreshEngine : IDisposable
{
    private const int DetectionMilliseconds =
        20;

    private const int ActiveWindowMilliseconds =
        3_000;

    private const int SmallAreaMilliseconds =
        3_000;

    private const int CheckOneMilliseconds =
        1_000;

    private const int CheckThreeMilliseconds =
        3_000;

    private const int CheckFiveMilliseconds =
        5_000;

    private const int ValidAreaMilliseconds =
        30_000;

    private const int ResizeMilliseconds =
        500;

    private const int ContinuityGapMilliseconds =
        300;

    private const int SampleWidthLimit =
        640;

    private const int BlockSize =
        4;

    private const int PixelThreshold =
        14;

    private const int MinimumChangedPixels =
        4;

    private const int MaximumAreas =
        32;

    private sealed class ActivityBox
    {
        internal DrawingRectangle Bounds;
        internal DrawingRectangle RecentBounds;
        internal bool HasRecentBounds;

        internal long StartedTicks;
        internal long LastSeenTicks;
        internal long LastResizeTicks;

        internal bool PassedOneSecond;
        internal bool PassedThreeSeconds;
        internal bool Validated;
    }

    private readonly record struct CurrentComponent(
        DrawingRectangle Bounds);

    private readonly FormsScreen _screen;
    private readonly DrawingRectangle _bounds;
    private readonly FreshOverlay _overlay;
    private readonly CaptureBuffer _capture;

    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly int _columns;
    private readonly int _rows;

    private readonly byte[] _previous;
    private readonly bool[] _changed;
    private readonly bool[] _visited;
    private readonly int[] _componentQueue;

    private readonly int[] _colorMarks;
    private readonly int[] _colorQueue;
    private int _colorGeneration =
        1;

    private readonly List<ActivityBox> _areas =
        new();

    private readonly object _sync =
        new();

    private readonly CancellationTokenSource
        _cancellation =
            new();

    private Task? _worker;
    private bool _enabled =
        true;

    private bool _hasPrevious;
    private bool _disposed;

    private IntPtr _foregroundWindow;
    private DrawingRectangle _activeWindow;
    private long _activeWindowStarted;
    private long _ignoreChangesUntil;

    private bool _cursorInside;
    private double _cursorX;
    private double _cursorY;
    private DrawingRectangle _cursorElement;

    internal FreshEngine(
        FormsScreen screen)
    {
        _screen =
            screen;

        _bounds =
            screen.WorkingArea.Width >
                    0 &&
            screen.WorkingArea.Height >
                    0
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
                    (double)BlockSize));

        _rows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    _sampleHeight /
                    (double)BlockSize));

        _previous =
            new byte[
                checked(
                    _sampleStride *
                    _sampleHeight)];

        _changed =
            new bool[
                checked(
                    _columns *
                    _rows)];

        _visited =
            new bool[
                _changed.Length];

        _componentQueue =
            new int[
                _changed.Length];

        _colorMarks =
            new int[
                checked(
                    _sampleWidth *
                    _sampleHeight)];

        _colorQueue =
            new int[
                _colorMarks.Length];

        _overlay =
            new FreshOverlay(
                _bounds);

        _capture =
            new CaptureBuffer(
                _bounds,
                _sampleWidth,
                _sampleHeight);
    }

    internal void Start()
    {
        _overlay.ShowOverlay();

        _worker =
            Task.Run(
                WorkAsync);
    }

    internal void SetEnabled(
        bool enabled)
    {
        lock (_sync)
        {
            _enabled =
                enabled;

            _hasPrevious =
                false;

            _areas.Clear();

            _foregroundWindow =
                IntPtr.Zero;

            _activeWindow =
                DrawingRectangle.Empty;

            _activeWindowStarted =
                0;

            _cursorElement =
                DrawingRectangle.Empty;
        }

        Publish(
            Stopwatch.GetTimestamp());
    }

    private async Task WorkAsync()
    {
        while (!_cancellation
                   .IsCancellationRequested)
        {
            var iterationStarted =
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
                        _capture.Grab();

                    lock (_sync)
                    {
                        if (_enabled)
                        {
                            Analyze(
                                frame,
                                iterationStarted);
                        }
                    }
                }

                Publish(
                    iterationStarted);
            }
            catch
            {
            }

            var elapsedMilliseconds =
                (Stopwatch.GetTimestamp() -
                 iterationStarted) *
                1000.0 /
                Stopwatch.Frequency;

            var delay =
                Math.Max(
                    1,
                    DetectionMilliseconds -
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

    private void Analyze(
        byte[] frame,
        long now)
    {
        ReadCursor();

        var foreground =
            WinApi.GetForegroundWindow();

        if (foreground !=
            _foregroundWindow)
        {
            _foregroundWindow =
                foreground;

            SetActiveWindow(
                foreground,
                now);

            _ignoreChangesUntil =
                now +
                ToTicks(
                    100);
        }

        if (!_hasPrevious)
        {
            CopyFrame(
                frame);

            _hasPrevious =
                true;

            SetCursorElement(
                frame,
                Array.Empty<CurrentComponent>());

            return;
        }

        var changedCells =
            FindChangedBlocks(
                frame);

        IReadOnlyList<CurrentComponent>
            components =
                Array.Empty<CurrentComponent>();

        var changedShare =
            changedCells /
            (double)Math.Max(
                1,
                _changed.Length);

        if (now >=
                _ignoreChangesUntil &&
            changedShare <
                0.20)
        {
            components =
                BuildComponents();

            UpdateAreas(
                components,
                now);
        }

        SetCursorElement(
            frame,
            components);

        RemoveExpiredAreas(
            now);

        CopyFrame(
            frame);
    }

    private int FindChangedBlocks(
        byte[] frame)
    {
        Array.Clear(
            _changed,
            0,
            _changed.Length);

        var changedCount =
            0;

        for (var row = 0;
             row < _rows;
             row++)
        {
            var top =
                row *
                BlockSize;

            var bottom =
                Math.Min(
                    _sampleHeight,
                    top +
                    BlockSize);

            for (var column = 0;
                 column < _columns;
                 column++)
            {
                var left =
                    column *
                    BlockSize;

                var right =
                    Math.Min(
                        _sampleWidth,
                        left +
                        BlockSize);

                var hotPixels =
                    0;

                var totalDifference =
                    0;

                var pixelCount =
                    0;

                for (var y = top;
                     y < bottom;
                     y++)
                {
                    for (var x = left;
                         x < right;
                         x++)
                    {
                        var index =
                            y *
                            _sampleStride +
                            x *
                            4;

                        var difference =
                            Math.Max(
                                Math.Abs(
                                    frame[index] -
                                    _previous[index]),
                                Math.Max(
                                    Math.Abs(
                                        frame[index + 1] -
                                        _previous[index + 1]),
                                    Math.Abs(
                                        frame[index + 2] -
                                        _previous[index + 2])));

                        totalDifference +=
                            difference;

                        pixelCount++;

                        if (difference >=
                            PixelThreshold)
                        {
                            hotPixels++;
                        }
                    }
                }

                var averageDifference =
                    totalDifference /
                    Math.Max(
                        1,
                        pixelCount);

                if (hotPixels <
                        MinimumChangedPixels ||
                    averageDifference <
                        8)
                {
                    continue;
                }

                _changed[
                    row *
                    _columns +
                    column] =
                        true;

                changedCount++;
            }
        }

        return changedCount;
    }

    private List<CurrentComponent>
        BuildComponents()
    {
        Array.Clear(
            _visited,
            0,
            _visited.Length);

        var result =
            new List<CurrentComponent>();

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

                if (!_changed[start] ||
                    _visited[start])
                {
                    continue;
                }

                var head =
                    0;

                var tail =
                    0;

                _componentQueue[tail++] =
                    start;

                _visited[start] =
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

                while (head <
                       tail)
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

                    AddNeighbor(
                        currentRow -
                            1,
                        currentColumn,
                        ref tail);

                    AddNeighbor(
                        currentRow +
                            1,
                        currentColumn,
                        ref tail);

                    AddNeighbor(
                        currentRow,
                        currentColumn -
                            1,
                        ref tail);

                    AddNeighbor(
                        currentRow,
                        currentColumn +
                            1,
                        ref tail);
                }

                if (count <
                    2)
                {
                    continue;
                }

                var bounds =
                    BlocksToLocal(
                        minimumRow,
                        maximumRow,
                        minimumColumn,
                        maximumColumn);

                if (bounds.Width <
                        4 ||
                    bounds.Height <
                        4)
                {
                    continue;
                }

                result.Add(
                    new CurrentComponent(
                        bounds));
            }
        }

        return result;
    }

    private void AddNeighbor(
        int row,
        int column,
        ref int tail)
    {
        if (row <
                0 ||
            row >=
                _rows ||
            column <
                0 ||
            column >=
                _columns)
        {
            return;
        }

        var index =
            row *
            _columns +
            column;

        if (!_changed[index] ||
            _visited[index])
        {
            return;
        }

        _visited[index] =
            true;

        _componentQueue[tail++] =
            index;
    }

    private void UpdateAreas(
        IReadOnlyList<CurrentComponent> components,
        long now)
    {
        var matched =
            new HashSet<ActivityBox>();

        foreach (var component in
                 components)
        {
            var area =
                FindOverlappingArea(
                    component.Bounds,
                    matched);

            if (area is null)
            {
                area =
                    new ActivityBox
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

                _areas.Add(
                    area);

                if (_areas.Count >
                    MaximumAreas)
                {
                    var oldest =
                        _areas
                            .OrderBy(
                                candidate =>
                                    candidate
                                        .LastSeenTicks)
                            .First();

                    _areas.Remove(
                        oldest);
                }
            }
            else
            {
                matched.Add(
                    area);

                if (!area.Validated &&
                    now -
                        area.LastSeenTicks >
                    ToTicks(
                        ContinuityGapMilliseconds))
                {
                    area.StartedTicks =
                        now;

                    area.PassedOneSecond =
                        false;

                    area.PassedThreeSeconds =
                        false;
                }

                area.LastSeenTicks =
                    now;

                area.RecentBounds =
                    area.HasRecentBounds
                        ? DrawingRectangle.Union(
                            area.RecentBounds,
                            component.Bounds)
                        : component.Bounds;

                area.HasRecentBounds =
                    true;
            }

            var age =
                now -
                area.StartedTicks;

            if (age >=
                ToTicks(
                    CheckOneMilliseconds))
            {
                area.PassedOneSecond =
                    true;
            }

            if (age >=
                ToTicks(
                    CheckThreeMilliseconds))
            {
                area.PassedThreeSeconds =
                    true;
            }

            if (!area.Validated &&
                age >=
                    ToTicks(
                        CheckFiveMilliseconds) &&
                area.PassedOneSecond &&
                area.PassedThreeSeconds)
            {
                area.Validated =
                    true;
            }
        }

        foreach (var area in
                 _areas)
        {
            if (now -
                    area.LastResizeTicks <
                ToTicks(
                    ResizeMilliseconds))
            {
                continue;
            }

            area.LastResizeTicks =
                now;

            if (!area.HasRecentBounds)
            {
                continue;
            }

            area.Bounds =
                PadAndClamp(
                    area.RecentBounds,
                    area.Validated
                        ? 7
                        : 3);

            area.RecentBounds =
                DrawingRectangle.Empty;

            area.HasRecentBounds =
                false;
        }
    }

    private ActivityBox? FindOverlappingArea(
        DrawingRectangle component,
        ISet<ActivityBox> matched)
    {
        ActivityBox? best =
            null;

        var bestIntersection =
            0;

        foreach (var area in
                 _areas)
        {
            if (matched.Contains(
                    area))
            {
                continue;
            }

            var intersection =
                DrawingRectangle.Intersect(
                    area.Bounds,
                    component);

            var intersectionArea =
                Math.Max(
                    0,
                    intersection.Width) *
                Math.Max(
                    0,
                    intersection.Height);

            if (intersectionArea <=
                bestIntersection)
            {
                continue;
            }

            best =
                area;

            bestIntersection =
                intersectionArea;
        }

        return bestIntersection >
                   0
            ? best
            : null;
    }

    private void RemoveExpiredAreas(
        long now)
    {
        for (var index =
                 _areas.Count -
                 1;
             index >=
                 0;
             index--)
        {
            var area =
                _areas[index];

            var lifetime =
                area.Validated
                    ? ValidAreaMilliseconds
                    : SmallAreaMilliseconds;

            if (now -
                    area.LastSeenTicks >
                ToTicks(
                    lifetime))
            {
                _areas.RemoveAt(
                    index);
            }
        }
    }

    private void ReadCursor()
    {
        if (!WinApi.GetCursorPos(
                out var point) ||
            !_bounds.Contains(
                point.X,
                point.Y))
        {
            _cursorInside =
                false;

            _cursorElement =
                DrawingRectangle.Empty;

            return;
        }

        _cursorInside =
            true;

        _cursorX =
            point.X -
            _bounds.Left;

        _cursorY =
            point.Y -
            _bounds.Top;
    }

    private void SetCursorElement(
        byte[] frame,
        IReadOnlyList<CurrentComponent> components)
    {
        if (!_cursorInside)
        {
            _cursorElement =
                DrawingRectangle.Empty;

            return;
        }

        foreach (var component in
                 components)
        {
            if (!component.Bounds.Contains(
                    (int)Math.Round(
                        _cursorX),
                    (int)Math.Round(
                        _cursorY)))
            {
                continue;
            }

            _cursorElement =
                PadAndClamp(
                    component.Bounds,
                    4);

            return;
        }

        _cursorElement =
            FindConnectedColor(
                frame) ??
            DrawingRectangle.Empty;
    }

    private DrawingRectangle?
        FindConnectedColor(
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

        const int radiusX =
            52;

        const int radiusY =
            36;

        const int colorThreshold =
            14;

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

        BeginColorGeneration();

        var head =
            0;

        var tail =
            0;

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

        while (head <
               tail)
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

            AddColorPixel(
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

            AddColorPixel(
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

            AddColorPixel(
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

            AddColorPixel(
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

        if (count <
                7 ||
            count >
                roiArea *
                0.35 ||
            touchedBorders >=
                2 ||
            width <
                2 ||
            height <
                2)
        {
            return null;
        }

        var local =
            SampleToLocal(
                minimumX,
                minimumY,
                maximumX +
                    1,
                maximumY +
                    1);

        if (local.Width >
                500 ||
            local.Height >
                320)
        {
            return null;
        }

        return PadAndClamp(
            local,
            4);
    }

    private void AddColorPixel(
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
        if (x <
                left ||
            x >
                right ||
            y <
                top ||
            y >
                bottom)
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

    private void SetActiveWindow(
        IntPtr window,
        long now)
    {
        if (window ==
                IntPtr.Zero ||
            IsShellWindow(
                window) ||
            !WinApi.GetWindowRect(
                window,
                out var native))
        {
            return;
        }

        var absolute =
            DrawingRectangle.FromLTRB(
                native.Left,
                native.Top,
                native.Right,
                native.Bottom);

        var visible =
            DrawingRectangle.Intersect(
                absolute,
                _bounds);

        if (visible.Width <
                40 ||
            visible.Height <
                30)
        {
            return;
        }

        _activeWindow =
            new DrawingRectangle(
                visible.Left -
                    _bounds.Left,
                visible.Top -
                    _bounds.Top,
                visible.Width,
                visible.Height);

        _activeWindowStarted =
            now;
    }

    private static bool IsShellWindow(
        IntPtr window)
    {
        var className =
            new System.Text.StringBuilder(
                128);

        if (WinApi.GetClassName(
                window,
                className,
                className.Capacity) <=
            0)
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
        List<RevealBox> boxes;
        CursorSpot? cursor;
        bool enabled;

        lock (_sync)
        {
            enabled =
                _enabled;

            boxes =
                enabled
                    ? BuildBoxes(
                        now)
                    : new List<RevealBox>();

            cursor =
                enabled
                    ? BuildCursor()
                    : null;
        }

        _overlay.Draw(
            enabled,
            boxes,
            cursor);
    }

    private List<RevealBox> BuildBoxes(
        long now)
    {
        var result =
            new List<RevealBox>(
                _areas.Count +
                2);

        foreach (var area in
                 _areas)
        {
            result.Add(
                new RevealBox(
                    Normalize(
                        area.Bounds)));
        }

        if (!_activeWindow.IsEmpty &&
            now -
                _activeWindowStarted <=
            ToTicks(
                ActiveWindowMilliseconds))
        {
            result.Add(
                new RevealBox(
                    Normalize(
                        _activeWindow)));
        }

        if (!_cursorElement.IsEmpty)
        {
            result.Add(
                new RevealBox(
                    Normalize(
                        _cursorElement)));
        }

        return result;
    }

    private CursorSpot? BuildCursor()
    {
        if (!_cursorInside)
        {
            return null;
        }

        const double radius =
            16.0;

        return new CursorSpot(
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

    private DrawingRectangle BlocksToLocal(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn)
    {
        return SampleToLocal(
            minimumColumn *
                BlockSize,
            minimumRow *
                BlockSize,
            Math.Min(
                _sampleWidth,
                (maximumColumn +
                    1) *
                BlockSize),
            Math.Min(
                _sampleHeight,
                (maximumRow +
                    1) *
                BlockSize));
    }

    private DrawingRectangle SampleToLocal(
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

    private void CopyFrame(
        byte[] frame)
    {
        Buffer.BlockCopy(
            frame,
            0,
            _previous,
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
            _worker?.Wait(
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
