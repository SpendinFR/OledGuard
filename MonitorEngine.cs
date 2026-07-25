using System.Diagnostics;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuardSimple;

internal sealed class MonitorEngine : IDisposable
{
    private const int CaptureMilliseconds = 20;
    private const int ForegroundHoldMilliseconds = 3_000;
    private const int CursorComponentHoldMilliseconds = 3_000;
    private const int FadeMilliseconds = 300;
    private const int CursorRadiusPixels = 18;
    private const double MaximumMaskOpacity = 0.85;

    private readonly FormsScreen _screen;
    private readonly DrawingRectangle _protectionBounds;
    private readonly OverlayWindow _overlay;
    private readonly ScreenCapture _capture;
    private readonly PixelDetector _detector;
    private readonly CursorProbe _cursorProbe;
    private readonly ZoneTracker _zoneTracker;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();

    private byte[] _previousFrame;
    private byte[] _currentFrame;
    private Task? _captureLoop;
    private bool _renderSubscribed;
    private bool _enabled = true;
    private bool _hasPreviousFrame;
    private bool _dirty = true;
    private bool _disposed;

    private IntPtr _lastForegroundWindow;
    private DrawingRectangle _foregroundBounds;
    private long _foregroundTicks;
    private long _ignoreMotionUntilTicks;

    private bool _hasCursor;
    private DrawingPoint _cursorLocal;
    private DrawingPoint _lastProbeCursor;
    private bool _hasLastProbeCursor;

    private DrawingRectangle _cursorComponentBounds;
    private long _cursorComponentTicks;

    public MonitorEngine(
        FormsScreen screen)
    {
        _screen = screen;
        _protectionBounds = DisplayArea.GetProtectionBounds(screen);

        var sampleWidth = Math.Min(
            960,
            Math.Max(480, _protectionBounds.Width));

        var sampleHeight = Math.Max(
            270,
            (int)Math.Round(
                _protectionBounds.Height *
                sampleWidth /
                (double)Math.Max(1, _protectionBounds.Width)));

        _overlay = new OverlayWindow(
            screen,
            _protectionBounds);

        _capture = new ScreenCapture(
            _protectionBounds,
            sampleWidth,
            sampleHeight);

        _detector = new PixelDetector(
            _capture.Width,
            _capture.Height,
            _capture.Stride,
            _protectionBounds.Width,
            _protectionBounds.Height);

        _cursorProbe = new CursorProbe(
            _capture.Width,
            _capture.Height,
            _capture.Stride,
            _protectionBounds.Width,
            _protectionBounds.Height);

        _zoneTracker = new ZoneTracker(
            _protectionBounds.Width,
            _protectionBounds.Height);

        _previousFrame = new byte[_capture.BufferLength];
        _currentFrame = new byte[_capture.BufferLength];
    }

    public void Start()
    {
        _overlay.EnsureVisible();

        if (!_renderSubscribed)
        {
            System.Windows.Media.CompositionTarget.Rendering +=
                OnRendering;
            _renderSubscribed = true;
        }

        _captureLoop = Task.Run(CaptureLoopAsync);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _hasPreviousFrame = false;
            _foregroundBounds = DrawingRectangle.Empty;
            _cursorComponentBounds = DrawingRectangle.Empty;
            _lastForegroundWindow = IntPtr.Zero;
            _hasLastProbeCursor = false;
            _zoneTracker.Clear();
            _dirty = true;
        }
    }

    private async Task CaptureLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                bool enabled;

                lock (_sync)
                {
                    enabled = _enabled;
                }

                if (enabled)
                {
                    _capture.CaptureInto(_currentFrame);
                    AnalyzeCurrentFrame();
                }

                await Task.Delay(
                        CaptureMilliseconds,
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
                            100,
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

    private void AnalyzeCurrentFrame()
    {
        var now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            ReadCursor(now);

            var foregroundWindow = NativeMethods.GetForegroundWindow();

            if (foregroundWindow != _lastForegroundWindow)
            {
                _lastForegroundWindow = foregroundWindow;
                RevealForegroundWindow(
                    foregroundWindow,
                    now);
                _ignoreMotionUntilTicks = now +
                    StopwatchTicks(120);
            }

            if (!_hasPreviousFrame)
            {
                SwapFrames();
                _hasPreviousFrame = true;
                _dirty = true;
                return;
            }

            var detection = _detector.Detect(
                _previousFrame,
                _currentFrame,
                _hasCursor
                    ? _cursorLocal
                    : null);

            if (now < _ignoreMotionUntilTicks)
            {
                SwapFrames();
                return;
            }

            if (detection.ChangedFraction >= 0.18)
            {
                RevealForegroundWindow(
                    foregroundWindow,
                    now);

                _ignoreMotionUntilTicks = now +
                    StopwatchTicks(120);

                SwapFrames();
                return;
            }

            if (_zoneTracker.Update(
                    detection.Components,
                    now))
            {
                _dirty = true;
            }

            UpdateCursorFromMotion(
                detection.Components,
                now);

            ProbeStaticCursorElement(now);
            SwapFrames();
        }
    }

    private void ReadCursor(long now)
    {
        if (!NativeMethods.GetCursorPos(out var cursor) ||
            !_protectionBounds.Contains(cursor.X, cursor.Y))
        {
            if (_hasCursor)
            {
                _hasCursor = false;
                _dirty = true;
            }

            return;
        }

        var local = new DrawingPoint(
            cursor.X - _protectionBounds.Left,
            cursor.Y - _protectionBounds.Top);

        var moved = !_hasCursor ||
                    local != _cursorLocal;

        _hasCursor = true;
        _cursorLocal = local;

        if (!moved)
        {
            return;
        }

        var knownZone = _zoneTracker.RevealUnderCursor(
            local,
            now);

        if (knownZone is not null)
        {
            _cursorComponentBounds = knownZone.Value;
            _cursorComponentTicks = now;
        }

        _dirty = true;
    }

    private void UpdateCursorFromMotion(
        IReadOnlyList<DrawingRectangle> components,
        long now)
    {
        if (!_hasCursor)
        {
            return;
        }

        foreach (var component in components)
        {
            var expanded = component;
            expanded.Inflate(24, 24);

            if (!expanded.Contains(_cursorLocal))
            {
                continue;
            }

            _cursorComponentBounds = component;
            _cursorComponentTicks = now;
            _dirty = true;
            return;
        }
    }

    private void ProbeStaticCursorElement(long now)
    {
        if (!_hasCursor)
        {
            return;
        }

        var movedEnough = !_hasLastProbeCursor ||
                          Math.Abs(
                              _cursorLocal.X -
                              _lastProbeCursor.X) >= 2 ||
                          Math.Abs(
                              _cursorLocal.Y -
                              _lastProbeCursor.Y) >= 2;

        if (!movedEnough)
        {
            return;
        }

        _lastProbeCursor = _cursorLocal;
        _hasLastProbeCursor = true;

        var component = _cursorProbe.Probe(
            _currentFrame,
            _cursorLocal);

        if (component is null ||
            component.Value.Width < 5 ||
            component.Value.Height < 5)
        {
            return;
        }

        _cursorComponentBounds = component.Value;
        _cursorComponentTicks = now;
        _dirty = true;
    }

    private void RevealForegroundWindow(
        IntPtr foregroundWindow,
        long now)
    {
        if (foregroundWindow == IntPtr.Zero ||
            IsShellWindow(foregroundWindow) ||
            !NativeMethods.GetWindowRect(
                foregroundWindow,
                out var nativeRectangle))
        {
            return;
        }

        var absolute = DrawingRectangle.FromLTRB(
            nativeRectangle.Left,
            nativeRectangle.Top,
            nativeRectangle.Right,
            nativeRectangle.Bottom);

        var visible = DrawingRectangle.Intersect(
            absolute,
            _protectionBounds);

        if (visible.Width < 40 ||
            visible.Height < 30)
        {
            return;
        }

        _foregroundBounds = new DrawingRectangle(
            visible.Left - _protectionBounds.Left,
            visible.Top - _protectionBounds.Top,
            visible.Width,
            visible.Height);

        _foregroundTicks = now;
        _dirty = true;
    }

    private static bool IsShellWindow(IntPtr window)
    {
        var className = new System.Text.StringBuilder(128);

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

    private void OnRendering(
        object? sender,
        EventArgs eventArgs)
    {
        var now = Stopwatch.GetTimestamp();
        List<RevealRegion> regions;
        List<CursorHole> cursorHoles;
        double maximumOpacity;
        bool shouldPush;

        lock (_sync)
        {
            ReadCursor(now);

            var trackerRegions = _zoneTracker.BuildRegions(
                now,
                MaximumMaskOpacity,
                out var trackerAnimation,
                out var trackerChanged);

            if (trackerChanged)
            {
                _dirty = true;
            }

            regions = trackerRegions.ToList();
            var animationActive = trackerAnimation;

            AppendTimedRegion(
                regions,
                ref _foregroundBounds,
                ref _foregroundTicks,
                now,
                ForegroundHoldMilliseconds,
                ref animationActive);

            AppendTimedRegion(
                regions,
                ref _cursorComponentBounds,
                ref _cursorComponentTicks,
                now,
                CursorComponentHoldMilliseconds,
                ref animationActive);

            cursorHoles = BuildCursorHoles();

            maximumOpacity = _enabled
                ? MaximumMaskOpacity
                : 0.0;

            shouldPush = _dirty ||
                         animationActive ||
                         cursorHoles.Count > 0;

            _dirty = false;
        }

        if (shouldPush)
        {
            _overlay.SetScene(
                maximumOpacity,
                regions,
                cursorHoles);
        }
    }

    private void AppendTimedRegion(
        ICollection<RevealRegion> regions,
        ref DrawingRectangle bounds,
        ref long timestamp,
        long now,
        int holdMilliseconds,
        ref bool animationActive)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        var elapsed = now - timestamp;
        var expiry = StopwatchTicks(
            holdMilliseconds + FadeMilliseconds);

        if (elapsed >= expiry)
        {
            bounds = DrawingRectangle.Empty;
            timestamp = 0;
            _dirty = true;
            return;
        }

        if (elapsed >= StopwatchTicks(holdMilliseconds))
        {
            animationActive = true;
        }

        regions.Add(
            new RevealRegion(
                ToNormalized(bounds),
                ComputeOpacity(
                    elapsed,
                    holdMilliseconds,
                    FadeMilliseconds,
                    MaximumMaskOpacity)));
    }

    private List<CursorHole> BuildCursorHoles()
    {
        if (!_hasCursor)
        {
            return new List<CursorHole>();
        }

        return new List<CursorHole>
        {
            new(
                new System.Windows.Point(
                    _cursorLocal.X /
                    Math.Max(1.0, _protectionBounds.Width),
                    _cursorLocal.Y /
                    Math.Max(1.0, _protectionBounds.Height)),
                CursorRadiusPixels /
                Math.Max(1.0, _protectionBounds.Width),
                CursorRadiusPixels /
                Math.Max(1.0, _protectionBounds.Height))
        };
    }

    private System.Windows.Rect ToNormalized(
        DrawingRectangle rectangle)
    {
        return new System.Windows.Rect(
            rectangle.Left /
            (double)Math.Max(1, _protectionBounds.Width),
            rectangle.Top /
            (double)Math.Max(1, _protectionBounds.Height),
            rectangle.Width /
            (double)Math.Max(1, _protectionBounds.Width),
            rectangle.Height /
            (double)Math.Max(1, _protectionBounds.Height));
    }

    private void SwapFrames()
    {
        (_previousFrame, _currentFrame) =
            (_currentFrame, _previousFrame);
    }

    private static double ComputeOpacity(
        long elapsedTicks,
        int holdMilliseconds,
        int fadeMilliseconds,
        double maximumOpacity)
    {
        var holdTicks = StopwatchTicks(holdMilliseconds);

        if (elapsedTicks <= holdTicks)
        {
            return 0.0;
        }

        var fadeTicks = Math.Max(
            1L,
            StopwatchTicks(fadeMilliseconds));

        var progress = Math.Clamp(
            (elapsedTicks - holdTicks) /
            (double)fadeTicks,
            0.0,
            1.0);

        return maximumOpacity * progress;
    }

    private static long StopwatchTicks(
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

        _disposed = true;
        _cancellation.Cancel();

        if (_renderSubscribed)
        {
            System.Windows.Media.CompositionTarget.Rendering -=
                OnRendering;
            _renderSubscribed = false;
        }

        _capture.Dispose();
        _overlay.Close();
        _cancellation.Dispose();
    }
}
