using System.Diagnostics;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

internal sealed class MonitorSession : IDisposable
{
    private readonly FormsScreen _screen;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly ScreenSampler _sampler;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly byte[] _previousFrame;
    private readonly byte[] _previousLuminance;
    private readonly byte[] _currentLuminance;
    private readonly byte[] _revealLevel;
    private readonly bool[] _motion;
    private readonly bool[] _activity;
    private readonly bool[] _ink;
    private readonly bool[] _scratch;
    private readonly float[] _renderAlpha;
    private readonly object _sync = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _captureLoop;
    private bool _enabled;
    private bool _hasPrevious;
    private bool _maskDirty;
    private bool _disposed;
    private long _lastCaptureTicks;
    private long _revealAllUntilTicks;
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;
    private bool _lastRevealAll;

    public MonitorSession(FormsScreen screen, AppSettings settings)
    {
        _screen = screen;
        _settings = settings;

        var bounds = screen.Bounds;
        var maximumWidth = Math.Clamp(
            settings.ActiveInkCaptureWidth,
            640,
            2560);
        var scale = Math.Min(
            1.0,
            maximumWidth / (double)Math.Max(1, bounds.Width));

        _width = Math.Max(
            1,
            (int)Math.Round(bounds.Width * scale));
        _height = Math.Max(
            1,
            (int)Math.Round(bounds.Height * scale));
        _stride = checked(_width * 4);

        var pixelCount = checked(_width * _height);
        var frameBytes = checked(_stride * _height);

        _previousFrame = new byte[frameBytes];
        _previousLuminance = new byte[pixelCount];
        _currentLuminance = new byte[pixelCount];
        _revealLevel = new byte[pixelCount];
        _motion = new bool[pixelCount];
        _activity = new bool[pixelCount];
        _ink = new bool[pixelCount];
        _scratch = new bool[pixelCount];
        _renderAlpha = new float[pixelCount];

        _overlay = new OverlayWindow(screen);
        _sampler = new ScreenSampler(
            bounds,
            _width,
            _height);

        _renderTimer = new DispatcherTimer(
            DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _renderTimer.Tick += OnRenderTick;
    }

    public bool ExcludedFromCapture =>
        _overlay.ExcludedFromCapture;

    public void Start(bool enabled)
    {
        _overlay.EnsureVisible();
        SetEnabled(enabled);
        _captureLoop = Task.Run(CaptureLoopAsync);
        _renderTimer.Start();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _hasPrevious = false;
            _maskDirty = true;
            _lastCaptureTicks = 0;
            _lastCursorX = int.MinValue;
            _lastCursorY = int.MinValue;
            _lastRevealAll = false;

            Array.Clear(
                _previousFrame,
                0,
                _previousFrame.Length);
            Array.Clear(
                _previousLuminance,
                0,
                _previousLuminance.Length);
            Array.Clear(
                _currentLuminance,
                0,
                _currentLuminance.Length);
            Array.Clear(
                _revealLevel,
                0,
                _revealLevel.Length);
            Array.Clear(
                _motion,
                0,
                _motion.Length);
            Array.Clear(
                _activity,
                0,
                _activity.Length);
            Array.Clear(
                _ink,
                0,
                _ink.Length);
            Array.Clear(
                _scratch,
                0,
                _scratch.Length);

            var now = Stopwatch.GetTimestamp();
            _revealAllUntilTicks = enabled
                ? now + ToStopwatchTicks(1500)
                : now;
        }

        PushMask();
    }

    public void RevealAll(TimeSpan duration)
    {
        lock (_sync)
        {
            _revealAllUntilTicks =
                Stopwatch.GetTimestamp() +
                ToStopwatchTicks(duration.TotalMilliseconds);
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

                lock (_sync)
                {
                    shouldCapture = _enabled;
                }

                if (shouldCapture)
                {
                    AnalyzeCapture(_sampler.Capture());
                }

                await Task.Delay(
                    _settings.ActiveInkSamplingMilliseconds,
                    _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(
                    1000,
                    _cancellation.Token).ConfigureAwait(false);
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

            FillLuminance(current, _currentLuminance);

            if (!_hasPrevious)
            {
                Buffer.BlockCopy(
                    current,
                    0,
                    _previousFrame,
                    0,
                    current.Length);
                Buffer.BlockCopy(
                    _currentLuminance,
                    0,
                    _previousLuminance,
                    0,
                    _currentLuminance.Length);
                _hasPrevious = true;
                _lastCaptureTicks = now;
                _maskDirty = true;
                return;
            }

            var elapsedMilliseconds =
                _lastCaptureTicks == 0
                    ? _settings.ActiveInkSamplingMilliseconds
                    : FromStopwatchTicks(
                        now - _lastCaptureTicks);
            _lastCaptureTicks = now;

            DecayRevealLevels(elapsedMilliseconds);
            DetectMotion(current);
            Dilate(
                _motion,
                _activity,
                _scratch,
                _settings.ActiveInkActivityRadius);
            DetectActiveInk();
            Dilate(
                _ink,
                _activity,
                _scratch,
                _settings.ActiveInkInkRadius);

            for (var index = 0;
                 index < _revealLevel.Length;
                 index++)
            {
                if (_activity[index])
                {
                    _revealLevel[index] = byte.MaxValue;
                }
            }

            Buffer.BlockCopy(
                current,
                0,
                _previousFrame,
                0,
                current.Length);
            Buffer.BlockCopy(
                _currentLuminance,
                0,
                _previousLuminance,
                0,
                _currentLuminance.Length);

            _maskDirty = true;
        }
    }

    private void FillLuminance(
        byte[] frame,
        byte[] luminance)
    {
        for (var index = 0;
             index < luminance.Length;
             index++)
        {
            var offset = index * 4;
            var blue = frame[offset];
            var green = frame[offset + 1];
            var red = frame[offset + 2];

            luminance[index] = (byte)(
                (red * 54L +
                 green * 183L +
                 blue * 19L) >> 8);
        }
    }

    private void DecayRevealLevels(
        double elapsedMilliseconds)
    {
        var holdMilliseconds = Math.Max(
            100,
            _settings.ActiveInkHoldMilliseconds);
        var decay = Math.Clamp(
            (int)Math.Ceiling(
                byte.MaxValue *
                elapsedMilliseconds /
                holdMilliseconds),
            1,
            byte.MaxValue);

        for (var index = 0;
             index < _revealLevel.Length;
             index++)
        {
            var value = _revealLevel[index];
            _revealLevel[index] = value <= decay
                ? (byte)0
                : (byte)(value - decay);
        }
    }

    private void DetectMotion(byte[] current)
    {
        Array.Clear(
            _motion,
            0,
            _motion.Length);

        var threshold =
            _settings.ActiveInkMotionThreshold;

        for (var index = 0;
             index < _motion.Length;
             index++)
        {
            var offset = index * 4;
            var blueDifference = Math.Abs(
                current[offset] -
                _previousFrame[offset]);
            var greenDifference = Math.Abs(
                current[offset + 1] -
                _previousFrame[offset + 1]);
            var redDifference = Math.Abs(
                current[offset + 2] -
                _previousFrame[offset + 2]);
            var luminanceDifference = Math.Abs(
                _currentLuminance[index] -
                _previousLuminance[index]);

            _motion[index] =
                Math.Max(
                    redDifference,
                    Math.Max(
                        greenDifference,
                        blueDifference)) >= threshold ||
                luminanceDifference >=
                    Math.Max(3, threshold - 3);
        }
    }

    private void DetectActiveInk()
    {
        Array.Clear(
            _ink,
            0,
            _ink.Length);

        var edgeThreshold =
            _settings.ActiveInkEdgeThreshold;

        for (var row = 1;
             row < _height - 1;
             row++)
        {
            var rowOffset = row * _width;

            for (var column = 1;
                 column < _width - 1;
                 column++)
            {
                var index = rowOffset + column;

                if (!_activity[index])
                {
                    continue;
                }

                if (_motion[index])
                {
                    _ink[index] = true;
                    continue;
                }

                var value = _currentLuminance[index];
                var left = _currentLuminance[index - 1];
                var right = _currentLuminance[index + 1];
                var top =
                    _currentLuminance[index - _width];
                var bottom =
                    _currentLuminance[index + _width];

                var maximumNeighbourDifference =
                    Math.Max(
                        Math.Abs(value - left),
                        Math.Max(
                            Math.Abs(value - right),
                            Math.Max(
                                Math.Abs(value - top),
                                Math.Abs(value - bottom))));

                var neighbourAverage =
                    (left + right + top + bottom) / 4;
                var localContrast = Math.Abs(
                    value - neighbourAverage);

                _ink[index] =
                    maximumNeighbourDifference >=
                        edgeThreshold ||
                    localContrast >=
                        Math.Max(4, edgeThreshold - 3);
            }
        }
    }

    private void Dilate(
        bool[] source,
        bool[] destination,
        bool[] temporary,
        int radius)
    {
        if (radius <= 0)
        {
            Array.Copy(
                source,
                destination,
                source.Length);
            return;
        }

        Array.Clear(
            temporary,
            0,
            temporary.Length);
        Array.Clear(
            destination,
            0,
            destination.Length);

        for (var row = 0;
             row < _height;
             row++)
        {
            var rowStart = row * _width;
            var activeCount = 0;

            for (var column = 0;
                 column <= Math.Min(
                     _width - 1,
                     radius);
                 column++)
            {
                if (source[rowStart + column])
                {
                    activeCount++;
                }
            }

            for (var column = 0;
                 column < _width;
                 column++)
            {
                temporary[rowStart + column] =
                    activeCount > 0;

                var removeColumn =
                    column - radius;
                if (removeColumn >= 0 &&
                    source[rowStart + removeColumn])
                {
                    activeCount--;
                }

                var addColumn =
                    column + radius + 1;
                if (addColumn < _width &&
                    source[rowStart + addColumn])
                {
                    activeCount++;
                }
            }
        }

        for (var column = 0;
             column < _width;
             column++)
        {
            var activeCount = 0;

            for (var row = 0;
                 row <= Math.Min(
                     _height - 1,
                     radius);
                 row++)
            {
                if (temporary[row * _width + column])
                {
                    activeCount++;
                }
            }

            for (var row = 0;
                 row < _height;
                 row++)
            {
                destination[row * _width + column] =
                    activeCount > 0;

                var removeRow =
                    row - radius;
                if (removeRow >= 0 &&
                    temporary[
                        removeRow * _width +
                        column])
                {
                    activeCount--;
                }

                var addRow =
                    row + radius + 1;
                if (addRow < _height &&
                    temporary[
                        addRow * _width +
                        column])
                {
                    activeCount++;
                }
            }
        }
    }

    private void OnRenderTick(
        object? sender,
        EventArgs e)
    {
        NativeMethods.GetCursorPos(out var cursor);
        var revealAll = false;
        var shouldPush = false;

        lock (_sync)
        {
            revealAll =
                _enabled &&
                Stopwatch.GetTimestamp() <
                    _revealAllUntilTicks;

            var cursorChanged =
                cursor.X != _lastCursorX ||
                cursor.Y != _lastCursorY;

            shouldPush =
                _maskDirty ||
                cursorChanged ||
                revealAll != _lastRevealAll;

            _lastCursorX = cursor.X;
            _lastCursorY = cursor.Y;
            _lastRevealAll = revealAll;
            _maskDirty = false;
        }

        if (shouldPush)
        {
            PushMask();
        }
    }

    private void PushMask()
    {
        lock (_sync)
        {
            if (!_enabled ||
                Stopwatch.GetTimestamp() <
                    _revealAllUntilTicks)
            {
                Array.Clear(
                    _renderAlpha,
                    0,
                    _renderAlpha.Length);
            }
            else
            {
                var maximumOpacity =
                    (float)_settings.MaximumMaskOpacity;

                for (var index = 0;
                     index < _renderAlpha.Length;
                     index++)
                {
                    var reveal =
                        _revealLevel[index] /
                        (float)byte.MaxValue;
                    _renderAlpha[index] =
                        maximumOpacity *
                        (1f - reveal);
                }

                ApplyCurrentMouseReveal();
            }
        }

        _overlay.SetMask(
            _renderAlpha,
            _width,
            _height);
    }

    private void ApplyCurrentMouseReveal()
    {
        if (!NativeMethods.GetCursorPos(
                out var cursor))
        {
            return;
        }

        var bounds = _screen.Bounds;

        if (!bounds.Contains(
                cursor.X,
                cursor.Y))
        {
            return;
        }

        var scaleX =
            _width /
            (double)Math.Max(1, bounds.Width);
        var scaleY =
            _height /
            (double)Math.Max(1, bounds.Height);
        var centerX =
            (cursor.X - bounds.Left) * scaleX;
        var centerY =
            (cursor.Y - bounds.Top) * scaleY;

        var outerRadiusPixels = Math.Max(
            12,
            _settings.MouseRevealRadiusPixels);
        var innerRadiusPixels =
            outerRadiusPixels * 0.55;
        var radiusX =
            outerRadiusPixels * scaleX;
        var radiusY =
            outerRadiusPixels * scaleY;

        var minimumColumn = Math.Clamp(
            (int)Math.Floor(centerX - radiusX),
            0,
            _width - 1);
        var maximumColumn = Math.Clamp(
            (int)Math.Ceiling(centerX + radiusX),
            0,
            _width - 1);
        var minimumRow = Math.Clamp(
            (int)Math.Floor(centerY - radiusY),
            0,
            _height - 1);
        var maximumRow = Math.Clamp(
            (int)Math.Ceiling(centerY + radiusY),
            0,
            _height - 1);

        for (var row = minimumRow;
             row <= maximumRow;
             row++)
        {
            var physicalY =
                (row + 0.5 - centerY) /
                Math.Max(0.0001, scaleY);

            for (var column = minimumColumn;
                 column <= maximumColumn;
                 column++)
            {
                var physicalX =
                    (column + 0.5 - centerX) /
                    Math.Max(0.0001, scaleX);
                var distance = Math.Sqrt(
                    physicalX * physicalX +
                    physicalY * physicalY);

                if (distance >= outerRadiusPixels)
                {
                    continue;
                }

                var maskFactor =
                    distance <= innerRadiusPixels
                        ? 0f
                        : SmoothStep(
                            (float)(
                                (distance -
                                 innerRadiusPixels) /
                                Math.Max(
                                    1.0,
                                    outerRadiusPixels -
                                    innerRadiusPixels)));

                var index =
                    row * _width +
                    column;
                _renderAlpha[index] *=
                    maskFactor;
            }
        }
    }

    private static float SmoothStep(float value)
    {
        var clamped = Math.Clamp(
            value,
            0f,
            1f);
        return clamped *
               clamped *
               (3f - 2f * clamped);
    }

    private static long ToStopwatchTicks(
        double milliseconds) =>
        (long)(
            milliseconds *
            Stopwatch.Frequency /
            1000.0);

    private static double FromStopwatchTicks(
        long ticks) =>
        ticks *
        1000.0 /
        Stopwatch.Frequency;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _renderTimer.Stop();
        _sampler.Dispose();
        _overlay.Close();
        _cancellation.Dispose();
    }
}
