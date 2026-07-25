using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
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
    private readonly OverlayWindow _overlay;
    private readonly ScreenSampler _sampler;
    private readonly DispatcherTimer _animationTimer;

    private readonly int _columns;
    private readonly int _rows;
    private readonly int _samplesPerCell;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _sampleStride;
    private readonly int _renderColumns;
    private readonly int _renderRows;

    private readonly byte[] _previousFrame;
    private readonly bool[] _strongMotion;
    private readonly bool[] _weakMotion;
    private readonly bool[] _visited;
    private readonly int[] _queue;
    private readonly long[] _revealUntilTicks;
    private readonly float[] _cellAlpha;
    private readonly float[] _renderAlpha;
    private readonly bool[] _manualRevealMask;
    private readonly List<MouseSample> _mouseTrail = new();

    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _enabled;
    private bool _hasPrevious;
    private bool _disposed;
    private long _revealAllUntilTicks;
    private long _lastAnimationTicks;

    private IntPtr _lastForegroundWindow;
    private string _lastForegroundTitle = string.Empty;

    private bool _hasCursor;
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

        var bounds = screen.Bounds;
        var requestedWidth = Math.Max(
            1,
            Math.Min(
                bounds.Width,
                settings.MotionZoneCaptureWidth));
        var requestedHeight = Math.Max(
            1,
            (int)Math.Round(
                bounds.Height *
                requestedWidth /
                (double)Math.Max(1, bounds.Width)));

        _samplesPerCell = Math.Max(
            1,
            settings.MotionZoneSamplesPerCell);
        _columns = Math.Max(
            1,
            (int)Math.Ceiling(
                requestedWidth /
                (double)_samplesPerCell));
        _rows = Math.Max(
            1,
            (int)Math.Ceiling(
                requestedHeight /
                (double)_samplesPerCell));

        _sampleWidth = checked(
            _columns *
            _samplesPerCell);
        _sampleHeight = checked(
            _rows *
            _samplesPerCell);
        _sampleStride = checked(
            _sampleWidth *
            4);

        var cellCount = checked(
            _columns *
            _rows);

        _previousFrame = new byte[checked(
            _sampleStride *
            _sampleHeight)];
        _strongMotion = new bool[cellCount];
        _weakMotion = new bool[cellCount];
        _visited = new bool[cellCount];
        _queue = new int[cellCount];
        _revealUntilTicks = new long[cellCount];
        _cellAlpha = new float[cellCount];
        _manualRevealMask = new bool[cellCount];

        const double targetRenderCellPixels = 8.0;
        _renderColumns = Math.Max(
            _columns,
            (int)Math.Ceiling(
                bounds.Width /
                targetRenderCellPixels));
        _renderRows = Math.Max(
            _rows,
            (int)Math.Ceiling(
                bounds.Height /
                targetRenderCellPixels));
        _renderAlpha = new float[checked(
            _renderColumns *
            _renderRows)];

        _overlay = new OverlayWindow(screen);
        _sampler = new ScreenSampler(
            bounds,
            _sampleWidth,
            _sampleHeight);

        _animationTimer = new DispatcherTimer(
            DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _animationTimer.Tick +=
            OnAnimationTick;
    }

    public bool ExcludedFromCapture =>
        _overlay.ExcludedFromCapture;

    public string ScreenDeviceName =>
        _screen.DeviceName;

    public void Start(
        bool enabled)
    {
        _overlay.EnsureVisible();
        SetEnabled(enabled);

        if (!_animationTimer.IsEnabled)
        {
            _animationTimer.Start();
        }

        _captureLoop = Task.Run(
            CaptureLoopAsync);
    }

    public void SetEnabled(
        bool enabled)
    {
        var now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            _enabled = enabled;
            _hasPrevious = false;
            _lastForegroundWindow =
                IntPtr.Zero;
            _lastForegroundTitle =
                string.Empty;
            _revealAllUntilTicks = 0;
            _lastAnimationTicks = 0;

            Array.Clear(
                _previousFrame,
                0,
                _previousFrame.Length);
            Array.Clear(
                _strongMotion,
                0,
                _strongMotion.Length);
            Array.Clear(
                _weakMotion,
                0,
                _weakMotion.Length);
            Array.Clear(
                _revealUntilTicks,
                0,
                _revealUntilTicks.Length);
            Array.Clear(
                _cellAlpha,
                0,
                _cellAlpha.Length);
            Array.Clear(
                _renderAlpha,
                0,
                _renderAlpha.Length);

            ResetMouseState();

            if (enabled)
            {
                RevealAllCellsUntil(
                    now +
                    ActivityHoldTicks());
            }
        }

        PushCurrentMask();
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
        }
    }

    public void SetManualRevealZones(
        IReadOnlyList<Rect> zones)
    {
        lock (_sync)
        {
            Array.Clear(
                _manualRevealMask,
                0,
                _manualRevealMask.Length);

            foreach (var zone in zones)
            {
                var left = Math.Clamp(
                    zone.Left,
                    0.0,
                    1.0);
                var top = Math.Clamp(
                    zone.Top,
                    0.0,
                    1.0);
                var right = Math.Clamp(
                    zone.Right,
                    left,
                    1.0);
                var bottom = Math.Clamp(
                    zone.Bottom,
                    top,
                    1.0);

                if (right <= left ||
                    bottom <= top)
                {
                    continue;
                }

                var minimumColumn = Math.Clamp(
                    (int)Math.Floor(
                        left *
                        _columns),
                    0,
                    _columns - 1);
                var maximumColumn = Math.Clamp(
                    (int)Math.Ceiling(
                        right *
                        _columns) - 1,
                    minimumColumn,
                    _columns - 1);
                var minimumRow = Math.Clamp(
                    (int)Math.Floor(
                        top *
                        _rows),
                    0,
                    _rows - 1);
                var maximumRow = Math.Clamp(
                    (int)Math.Ceiling(
                        bottom *
                        _rows) - 1,
                    minimumRow,
                    _rows - 1);

                for (var row = minimumRow;
                     row <= maximumRow;
                     row++)
                {
                    var rowOffset =
                        row *
                        _columns;

                    for (var column = minimumColumn;
                         column <= maximumColumn;
                         column++)
                    {
                        _manualRevealMask[
                            rowOffset +
                            column] = true;
                    }
                }
            }
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
                    var current =
                        _sampler.Capture();

                    AnalyzeCapture(
                        current,
                        foregroundWindow,
                        foregroundTitle);
                }

                await Task.Delay(
                        Math.Max(
                            10,
                            _settings
                                .MotionZoneSamplingMilliseconds),
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
                            200,
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
        IntPtr foregroundWindow,
        string foregroundTitle)
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
                CopyCurrentToPrevious(
                    current);
                _hasPrevious = true;
                UpdateForegroundIdentity(
                    foregroundWindow,
                    foregroundTitle);
                RevealAllCellsUntil(
                    now +
                    ActivityHoldTicks());
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

            DetectMotion(
                current);

            if (foregroundChanged)
            {
                RevealWindow(
                    foregroundWindow,
                    now +
                    ActivityHoldTicks());
            }
            else if (titleChanged &&
                     HasMeaningfulForegroundChange(
                         foregroundWindow))
            {
                RevealWindow(
                    foregroundWindow,
                    now +
                    ActivityHoldTicks());
            }

            RevealMotionComponents(
                now);

            UpdateForegroundIdentity(
                foregroundWindow,
                foregroundTitle);
            CopyCurrentToPrevious(
                current);
        }
    }

    private void DetectMotion(
        byte[] current)
    {
        Array.Clear(
            _strongMotion,
            0,
            _strongMotion.Length);
        Array.Clear(
            _weakMotion,
            0,
            _weakMotion.Length);

        var strongThreshold = Math.Max(
            1,
            _settings
                .MotionZonePixelThreshold);
        var weakThreshold = Math.Max(
            1,
            strongThreshold - 2);
        var samplesPerCell = checked(
            _samplesPerCell *
            _samplesPerCell);
        var minimumChangedSamples = Math.Max(
            1,
            (int)Math.Ceiling(
                samplesPerCell *
                Math.Clamp(
                    _settings
                        .MotionZoneChangedFraction,
                    0.01,
                    1.0)));

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
                var strongCount = 0;
                var weakCount = 0;

                for (var sampleY = 0;
                     sampleY < _samplesPerCell;
                     sampleY++)
                {
                    var pixelOffset =
                        (sampleTop + sampleY) *
                        _sampleStride +
                        sampleLeft *
                        4;

                    for (var sampleX = 0;
                         sampleX < _samplesPerCell;
                         sampleX++)
                    {
                        var blueDifference = Math.Abs(
                            current[pixelOffset] -
                            _previousFrame[pixelOffset]);
                        var greenDifference = Math.Abs(
                            current[pixelOffset + 1] -
                            _previousFrame[pixelOffset + 1]);
                        var redDifference = Math.Abs(
                            current[pixelOffset + 2] -
                            _previousFrame[pixelOffset + 2]);
                        var difference = Math.Max(
                            blueDifference,
                            Math.Max(
                                greenDifference,
                                redDifference));

                        if (difference >=
                            weakThreshold)
                        {
                            weakCount++;

                            if (difference >=
                                strongThreshold)
                            {
                                strongCount++;
                            }
                        }

                        pixelOffset += 4;
                    }
                }

                var index =
                    row *
                    _columns +
                    column;
                _weakMotion[index] =
                    weakCount >=
                    minimumChangedSamples;
                _strongMotion[index] =
                    strongCount >=
                    minimumChangedSamples;
            }
        }
    }

    private void RevealMotionComponents(
        long now)
    {
        Array.Clear(
            _visited,
            0,
            _visited.Length);

        var until =
            now +
            ActivityHoldTicks();
        var bounds =
            _screen.Bounds;
        var padding = Math.Clamp(
            _settings
                .MotionZonePaddingCells,
            0,
            3);

        for (var seed = 0;
             seed < _strongMotion.Length;
             seed++)
        {
            if (!_strongMotion[seed] ||
                _visited[seed])
            {
                continue;
            }

            var queueStart = 0;
            var queueEnd = 0;
            _queue[queueEnd++] =
                seed;
            _visited[seed] =
                true;

            var minimumRow =
                seed /
                _columns;
            var maximumRow =
                minimumRow;
            var minimumColumn =
                seed %
                _columns;
            var maximumColumn =
                minimumColumn;

            while (queueStart <
                   queueEnd)
            {
                var current =
                    _queue[queueStart++];
                var row =
                    current /
                    _columns;
                var column =
                    current %
                    _columns;

                minimumRow = Math.Min(
                    minimumRow,
                    row);
                maximumRow = Math.Max(
                    maximumRow,
                    row);
                minimumColumn = Math.Min(
                    minimumColumn,
                    column);
                maximumColumn = Math.Max(
                    maximumColumn,
                    column);

                for (var offsetRow = -1;
                     offsetRow <= 1;
                     offsetRow++)
                {
                    var neighbourRow =
                        row +
                        offsetRow;

                    if (neighbourRow < 0 ||
                        neighbourRow >=
                            _rows)
                    {
                        continue;
                    }

                    for (var offsetColumn = -1;
                         offsetColumn <= 1;
                         offsetColumn++)
                    {
                        if (offsetRow == 0 &&
                            offsetColumn == 0)
                        {
                            continue;
                        }

                        var neighbourColumn =
                            column +
                            offsetColumn;

                        if (neighbourColumn < 0 ||
                            neighbourColumn >=
                                _columns)
                        {
                            continue;
                        }

                        var neighbour =
                            neighbourRow *
                            _columns +
                            neighbourColumn;

                        if (_visited[neighbour] ||
                            !_weakMotion[neighbour])
                        {
                            continue;
                        }

                        _visited[neighbour] =
                            true;
                        _queue[queueEnd++] =
                            neighbour;
                    }
                }
            }

            var componentWidth =
                maximumColumn -
                minimumColumn +
                1;
            var componentHeight =
                maximumRow -
                minimumRow +
                1;
            var componentArea =
                componentWidth *
                componentHeight;
            var fillRatio =
                queueEnd /
                (double)Math.Max(
                    1,
                    componentArea);
            var widthPixels =
                componentWidth *
                bounds.Width /
                (double)Math.Max(
                    1,
                    _columns);
            var heightPixels =
                componentHeight *
                bounds.Height /
                (double)Math.Max(
                    1,
                    _rows);
            var areaPixels =
                widthPixels *
                heightPixels;

            var compactSurface =
                widthPixels <= 900.0 &&
                heightPixels <= 1_000.0 &&
                areaPixels <= 450_000.0 &&
                queueEnd <= 12_000 &&
                fillRatio >= 0.04;

            if (compactSurface)
            {
                RevealRectangle(
                    minimumRow - padding,
                    maximumRow + padding,
                    minimumColumn - padding,
                    maximumColumn + padding,
                    until);
                continue;
            }

            for (var item = 0;
                 item < queueEnd;
                 item++)
            {
                var index =
                    _queue[item];
                var row =
                    index /
                    _columns;
                var column =
                    index %
                    _columns;

                RevealRectangle(
                    row - padding,
                    row + padding,
                    column - padding,
                    column + padding,
                    until);
            }
        }
    }

    private bool HasMeaningfulForegroundChange(
        IntPtr foregroundWindow)
    {
        if (!TryGetWindowCellBounds(
                foregroundWindow,
                out var minimumRow,
                out var maximumRow,
                out var minimumColumn,
                out var maximumColumn))
        {
            return false;
        }

        var strongCells = 0;
        var totalCells = Math.Max(
            1,
            (maximumRow -
             minimumRow +
             1) *
            (maximumColumn -
             minimumColumn +
             1));

        for (var row = minimumRow;
             row <= maximumRow;
             row++)
        {
            var rowOffset =
                row *
                _columns;

            for (var column = minimumColumn;
                 column <= maximumColumn;
                 column++)
            {
                if (_strongMotion[
                        rowOffset +
                        column])
                {
                    strongCells++;
                }
            }
        }

        var required = Math.Max(
            48,
            (int)Math.Ceiling(
                totalCells *
                0.005));

        return strongCells >=
            required;
    }

    private void RevealWindow(
        IntPtr foregroundWindow,
        long until)
    {
        if (!TryGetWindowCellBounds(
                foregroundWindow,
                out var minimumRow,
                out var maximumRow,
                out var minimumColumn,
                out var maximumColumn))
        {
            return;
        }

        RevealRectangle(
            minimumRow,
            maximumRow,
            minimumColumn,
            maximumColumn,
            until);
    }

    private bool TryGetWindowCellBounds(
        IntPtr window,
        out int minimumRow,
        out int maximumRow,
        out int minimumColumn,
        out int maximumColumn)
    {
        minimumRow = 0;
        maximumRow = 0;
        minimumColumn = 0;
        maximumColumn = 0;

        if (window == IntPtr.Zero ||
            !GetWindowRect(
                window,
                out var nativeRectangle))
        {
            return false;
        }

        var screenBounds =
            _screen.Bounds;
        var windowBounds =
            System.Drawing.Rectangle.FromLTRB(
                nativeRectangle.Left,
                nativeRectangle.Top,
                nativeRectangle.Right,
                nativeRectangle.Bottom);
        var visible =
            System.Drawing.Rectangle.Intersect(
                screenBounds,
                windowBounds);

        if (visible.Width < 20 ||
            visible.Height < 20)
        {
            return false;
        }

        minimumColumn = Math.Clamp(
            (int)Math.Floor(
                (visible.Left -
                 screenBounds.Left) *
                _columns /
                (double)Math.Max(
                    1,
                    screenBounds.Width)),
            0,
            _columns - 1);
        maximumColumn = Math.Clamp(
            (int)Math.Ceiling(
                (visible.Right -
                 screenBounds.Left) *
                _columns /
                (double)Math.Max(
                    1,
                    screenBounds.Width)) - 1,
            minimumColumn,
            _columns - 1);
        minimumRow = Math.Clamp(
            (int)Math.Floor(
                (visible.Top -
                 screenBounds.Top) *
                _rows /
                (double)Math.Max(
                    1,
                    screenBounds.Height)),
            0,
            _rows - 1);
        maximumRow = Math.Clamp(
            (int)Math.Ceiling(
                (visible.Bottom -
                 screenBounds.Top) *
                _rows /
                (double)Math.Max(
                    1,
                    screenBounds.Height)) - 1,
            minimumRow,
            _rows - 1);

        return true;
    }

    private void RevealRectangle(
        int minimumRow,
        int maximumRow,
        int minimumColumn,
        int maximumColumn,
        long until)
    {
        minimumRow = Math.Clamp(
            minimumRow,
            0,
            _rows - 1);
        maximumRow = Math.Clamp(
            maximumRow,
            minimumRow,
            _rows - 1);
        minimumColumn = Math.Clamp(
            minimumColumn,
            0,
            _columns - 1);
        maximumColumn = Math.Clamp(
            maximumColumn,
            minimumColumn,
            _columns - 1);

        for (var row = minimumRow;
             row <= maximumRow;
             row++)
        {
            var rowOffset =
                row *
                _columns;

            for (var column = minimumColumn;
                 column <= maximumColumn;
                 column++)
            {
                var index =
                    rowOffset +
                    column;

                if (_revealUntilTicks[index] <
                    until)
                {
                    _revealUntilTicks[index] =
                        until;
                }
            }
        }
    }

    private void RevealAllCellsUntil(
        long until)
    {
        for (var index = 0;
             index < _revealUntilTicks.Length;
             index++)
        {
            _revealUntilTicks[index] =
                until;
        }
    }

    private void OnAnimationTick(
        object? sender,
        EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var now =
            Stopwatch.GetTimestamp();

        lock (_sync)
        {
            var elapsedMilliseconds =
                _lastAnimationTicks == 0
                    ? _animationTimer
                        .Interval
                        .TotalMilliseconds
                    : FromStopwatchTicks(
                        now -
                        _lastAnimationTicks);
            _lastAnimationTicks =
                now;

            if (!_enabled)
            {
                Array.Clear(
                    _renderAlpha,
                    0,
                    _renderAlpha.Length);
            }
            else
            {
                UpdateMouseState(
                    now);
                HoldCompactRevealUnderCursor(
                    now);
                UpdateCellAlpha(
                    now,
                    elapsedMilliseconds);
                BuildRenderMask(
                    now);
            }
        }

        _overlay.SetMask(
            _renderAlpha,
            _renderColumns,
            _renderRows);
    }

    private void UpdateMouseState(
        long now)
    {
        if (!_settings
                .MouseVisualEnabled ||
            !NativeMethods.GetCursorPos(
                out var cursor))
        {
            ResetMouseState();
            return;
        }

        var bounds =
            _screen.Bounds;

        if (!bounds.Contains(
                cursor.X,
                cursor.Y))
        {
            _hasCursor = false;
            PruneMouseTrail(
                now);
            return;
        }

        var localX =
            cursor.X -
            bounds.Left;
        var localY =
            cursor.Y -
            bounds.Top;

        if (!_hasCursor)
        {
            _hasCursor = true;
            _cursorX = localX;
            _cursorY = localY;
            _lastCursorX = localX;
            _lastCursorY = localY;
            _lastCursorTicks = now;
            PruneMouseTrail(
                now);
            return;
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
            AddInterpolatedMouseSamples(
                _cursorX,
                _cursorY,
                localX,
                localY,
                _lastCursorTicks,
                now);

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

        PruneMouseTrail(
            now);
    }

    private void AddInterpolatedMouseSamples(
        double startX,
        double startY,
        double endX,
        double endY,
        long startTicks,
        long endTicks)
    {
        var lifetimeMilliseconds =
            _settings
                .MouseTrailMilliseconds;

        if (lifetimeMilliseconds <= 0)
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
        var spacing = Math.Max(
            2,
            _settings
                .MouseTrailSpacingPixels);
        var steps = Math.Clamp(
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
            var timestamp =
                startTicks +
                (long)(
                    (endTicks -
                     startTicks) *
                    ratio);

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
                    timestamp));
        }

        if (_mouseTrail.Count > 24)
        {
            _mouseTrail.RemoveRange(
                0,
                _mouseTrail.Count -
                24);
        }
    }

    private void PruneMouseTrail(
        long now)
    {
        var lifetimeTicks =
            ToStopwatchTicks(
                Math.Max(
                    0,
                    _settings
                        .MouseTrailMilliseconds));

        while (_mouseTrail.Count > 0 &&
               (lifetimeTicks <= 0 ||
                now -
                    _mouseTrail[0]
                        .Timestamp >=
                    lifetimeTicks))
        {
            _mouseTrail.RemoveAt(
                0);
        }
    }

    private void HoldCompactRevealUnderCursor(
        long now)
    {
        if (!_hasCursor)
        {
            return;
        }

        var bounds =
            _screen.Bounds;
        var cursorColumn = Math.Clamp(
            (int)(
                _cursorX *
                _columns /
                Math.Max(
                    1.0,
                    bounds.Width)),
            0,
            _columns - 1);
        var cursorRow = Math.Clamp(
            (int)(
                _cursorY *
                _rows /
                Math.Max(
                    1.0,
                    bounds.Height)),
            0,
            _rows - 1);

        var seed =
            FindActiveSeedNearCursor(
                cursorRow,
                cursorColumn,
                now);

        if (seed < 0)
        {
            return;
        }

        Array.Clear(
            _visited,
            0,
            _visited.Length);

        var queueStart = 0;
        var queueEnd = 0;
        _queue[queueEnd++] =
            seed;
        _visited[seed] =
            true;

        var minimumRow =
            seed /
            _columns;
        var maximumRow =
            minimumRow;
        var minimumColumn =
            seed %
            _columns;
        var maximumColumn =
            minimumColumn;
        const int maximumCells =
            12_000;

        while (queueStart <
               queueEnd)
        {
            if (queueEnd >
                maximumCells)
            {
                return;
            }

            var current =
                _queue[queueStart++];
            var row =
                current /
                _columns;
            var column =
                current %
                _columns;

            minimumRow = Math.Min(
                minimumRow,
                row);
            maximumRow = Math.Max(
                maximumRow,
                row);
            minimumColumn = Math.Min(
                minimumColumn,
                column);
            maximumColumn = Math.Max(
                maximumColumn,
                column);

            for (var offsetRow = -1;
                 offsetRow <= 1;
                 offsetRow++)
            {
                var neighbourRow =
                    row +
                    offsetRow;

                if (neighbourRow < 0 ||
                    neighbourRow >=
                        _rows)
                {
                    continue;
                }

                for (var offsetColumn = -1;
                     offsetColumn <= 1;
                     offsetColumn++)
                {
                    if (offsetRow == 0 &&
                        offsetColumn == 0)
                    {
                        continue;
                    }

                    var neighbourColumn =
                        column +
                        offsetColumn;

                    if (neighbourColumn < 0 ||
                        neighbourColumn >=
                            _columns)
                    {
                        continue;
                    }

                    var neighbour =
                        neighbourRow *
                        _columns +
                        neighbourColumn;

                    if (_visited[neighbour] ||
                        _revealUntilTicks[
                            neighbour] <=
                            now)
                    {
                        continue;
                    }

                    _visited[neighbour] =
                        true;
                    _queue[queueEnd++] =
                        neighbour;
                }
            }
        }

        var widthPixels =
            (maximumColumn -
             minimumColumn +
             1) *
            bounds.Width /
            (double)Math.Max(
                1,
                _columns);
        var heightPixels =
            (maximumRow -
             minimumRow +
             1) *
            bounds.Height /
            (double)Math.Max(
                1,
                _rows);
        var areaPixels =
            widthPixels *
            heightPixels;

        if (widthPixels > 900.0 ||
            heightPixels > 1_000.0 ||
            areaPixels > 450_000.0)
        {
            return;
        }

        var until =
            now +
            ActivityHoldTicks();

        for (var item = 0;
             item < queueEnd;
             item++)
        {
            var index =
                _queue[item];

            if (_revealUntilTicks[index] <
                until)
            {
                _revealUntilTicks[index] =
                    until;
            }
        }
    }

    private int FindActiveSeedNearCursor(
        int cursorRow,
        int cursorColumn,
        long now)
    {
        for (var radius = 0;
             radius <= 1;
             radius++)
        {
            for (var row = cursorRow - radius;
                 row <= cursorRow + radius;
                 row++)
            {
                if (row < 0 ||
                    row >= _rows)
                {
                    continue;
                }

                for (var column = cursorColumn - radius;
                     column <= cursorColumn + radius;
                     column++)
                {
                    if (column < 0 ||
                        column >= _columns)
                    {
                        continue;
                    }

                    var index =
                        row *
                        _columns +
                        column;

                    if (_revealUntilTicks[index] >
                        now)
                    {
                        return index;
                    }
                }
            }
        }

        return -1;
    }

    private void UpdateCellAlpha(
        long now,
        double elapsedMilliseconds)
    {
        var revealEverything =
            _revealAllUntilTicks != 0 &&
            now <
                _revealAllUntilTicks;
        var maximumOpacity =
            (float)Math.Clamp(
                _settings
                    .MaximumMaskOpacity,
                0.0,
                1.0);
        var revealDuration =
            Math.Clamp(
                _settings
                    .MotionZoneTransientFadeMilliseconds /
                4.0,
                40.0,
                120.0);
        var darkenDuration =
            Math.Max(
                100.0,
                _settings
                    .MotionZoneDimDurationMilliseconds);

        for (var index = 0;
             index < _cellAlpha.Length;
             index++)
        {
            var visible =
                revealEverything ||
                _manualRevealMask[index] ||
                _revealUntilTicks[index] >
                    now;
            var target =
                visible
                    ? 0.0f
                    : maximumOpacity;
            var duration =
                target <
                _cellAlpha[index]
                    ? revealDuration
                    : darkenDuration;
            var blend =
                CalculateBlendFactor(
                    elapsedMilliseconds,
                    duration);

            _cellAlpha[index] =
                Lerp(
                    _cellAlpha[index],
                    target,
                    blend);

            if (Math.Abs(
                    _cellAlpha[index] -
                    target) <
                0.001f)
            {
                _cellAlpha[index] =
                    target;
            }
        }
    }

    private void BuildRenderMask(
        long now)
    {
        var bounds =
            _screen.Bounds;
        var mouseEnabled =
            _settings
                .MouseVisualEnabled &&
            _hasCursor;
        var baseRadius = Math.Max(
            0.0,
            _settings
                .MouseVisualRadiusPixels);
        var trailLifetimeTicks =
            Math.Max(
                1L,
                ToStopwatchTicks(
                    Math.Max(
                        1,
                        _settings
                            .MouseTrailMilliseconds)));

        for (var renderRow = 0;
             renderRow < _renderRows;
             renderRow++)
        {
            var gridY =
                (renderRow + 0.5) *
                _rows /
                _renderRows -
                0.5;
            var y0 = Math.Clamp(
                (int)Math.Floor(
                    gridY),
                0,
                _rows - 1);
            var y1 = Math.Min(
                _rows - 1,
                y0 + 1);
            var ty = Math.Clamp(
                gridY -
                y0,
                0.0,
                1.0);
            var pixelY =
                (renderRow + 0.5) *
                bounds.Height /
                _renderRows;

            for (var renderColumn = 0;
                 renderColumn < _renderColumns;
                 renderColumn++)
            {
                var gridX =
                    (renderColumn + 0.5) *
                    _columns /
                    _renderColumns -
                    0.5;
                var x0 = Math.Clamp(
                    (int)Math.Floor(
                        gridX),
                    0,
                    _columns - 1);
                var x1 = Math.Min(
                    _columns - 1,
                    x0 + 1);
                var tx = Math.Clamp(
                    gridX -
                    x0,
                    0.0,
                    1.0);
                var top = Lerp(
                    _cellAlpha[
                        y0 *
                        _columns +
                        x0],
                    _cellAlpha[
                        y0 *
                        _columns +
                        x1],
                    (float)tx);
                var bottom = Lerp(
                    _cellAlpha[
                        y1 *
                        _columns +
                        x0],
                    _cellAlpha[
                        y1 *
                        _columns +
                        x1],
                    (float)tx);
                var value = Lerp(
                    top,
                    bottom,
                    (float)ty);

                if (mouseEnabled)
                {
                    var pixelX =
                        (renderColumn + 0.5) *
                        bounds.Width /
                        _renderColumns;
                    var revealStrength =
                        CursorRevealStrength(
                            pixelX,
                            pixelY,
                            _cursorX,
                            _cursorY,
                            baseRadius);

                    foreach (var sample in
                             _mouseTrail)
                    {
                        var age =
                            now -
                            sample.Timestamp;

                        if (age < 0 ||
                            age >=
                                trailLifetimeTicks)
                        {
                            continue;
                        }

                        var life =
                            1.0 -
                            age /
                            (double)trailLifetimeTicks;
                        var radius =
                            baseRadius *
                            (0.35 +
                             0.65 *
                             life);
                        revealStrength = Math.Max(
                            revealStrength,
                            CursorRevealStrength(
                                pixelX,
                                pixelY,
                                sample.X,
                                sample.Y,
                                radius));
                    }

                    value *=
                        1.0f -
                        revealStrength;
                }

                if (value < 0.001f)
                {
                    value = 0.0f;
                }

                _renderAlpha[
                    renderRow *
                    _renderColumns +
                    renderColumn] =
                    Math.Clamp(
                        value,
                        0.0f,
                        1.0f);
            }
        }
    }

    private static float CursorRevealStrength(
        double pixelX,
        double pixelY,
        double centerX,
        double centerY,
        double radius)
    {
        if (radius <= 0.0)
        {
            return 0.0f;
        }

        const double featherPixels =
            4.0;
        var deltaX =
            pixelX -
            centerX;
        var deltaY =
            pixelY -
            centerY;
        var distanceSquared =
            deltaX *
            deltaX +
            deltaY *
            deltaY;
        var innerSquared =
            radius *
            radius;

        if (distanceSquared <=
            innerSquared)
        {
            return 1.0f;
        }

        var outer =
            radius +
            featherPixels;
        var outerSquared =
            outer *
            outer;

        if (distanceSquared >=
            outerSquared)
        {
            return 0.0f;
        }

        var distance =
            Math.Sqrt(
                distanceSquared);
        var fraction =
            (distance -
             radius) /
            featherPixels;

        return 1.0f -
            SmoothStep(
                (float)fraction);
    }

    private void PushCurrentMask()
    {
        lock (_sync)
        {
            if (!_enabled)
            {
                Array.Clear(
                    _renderAlpha,
                    0,
                    _renderAlpha.Length);
            }
            else
            {
                BuildRenderMask(
                    Stopwatch.GetTimestamp());
            }
        }

        _overlay.SetMask(
            _renderAlpha,
            _renderColumns,
            _renderRows);
    }

    private void ResetMouseState()
    {
        _hasCursor = false;
        _cursorX = 0;
        _cursorY = 0;
        _lastCursorX = 0;
        _lastCursorY = 0;
        _lastCursorTicks = 0;
        _mouseTrail.Clear();
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

    private long ActivityHoldTicks()
    {
        return ToStopwatchTicks(
            Math.Clamp(
                _settings
                    .ForegroundWindowRevealMilliseconds,
                500,
                10_000));
    }

    private static float CalculateBlendFactor(
        double elapsedMilliseconds,
        double durationMilliseconds)
    {
        if (durationMilliseconds <=
            0.0)
        {
            return 1.0f;
        }

        var fraction =
            Math.Max(
                0.0,
                elapsedMilliseconds) /
            durationMilliseconds;

        return 1.0f -
            MathF.Exp(
                -4.60517f *
                (float)fraction);
    }

    private static float SmoothStep(
        float value)
    {
        var clamped =
            Math.Clamp(
                value,
                0.0f,
                1.0f);

        return clamped *
            clamped *
            (3.0f -
             2.0f *
             clamped);
    }

    private static float Lerp(
        float start,
        float end,
        float amount)
    {
        return start +
            (end -
             start) *
            amount;
    }

    private static long ToStopwatchTicks(
        double milliseconds)
    {
        return (long)(
            milliseconds *
            Stopwatch.Frequency /
            1000.0);
    }

    private static double FromStopwatchTicks(
        long ticks)
    {
        return ticks *
            1000.0 /
            Stopwatch.Frequency;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool
        GetWindowRect(
            IntPtr window,
            out NativeWindowRect rectangle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _animationTimer.Stop();
        _animationTimer.Tick -=
            OnAnimationTick;
        _cancellation.Cancel();

        try
        {
            _captureLoop?.Wait(
                500);
        }
        catch
        {
            // Shutdown continues even if the capture loop was already ending.
        }

        _sampler.Dispose();
        _overlay.Close();
        _cancellation.Dispose();
    }
}
