using Microsoft.Win32;
using System.Windows;
using DrawingPoint = System.Drawing.Point;
using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuard;

public sealed class ProtectionController : IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly object _sync = new();
    private readonly List<MonitorSession>
        _sessions = new();
    private readonly Dictionary<string, List<Rect>>
        _manualRevealZones =
            new(StringComparer.OrdinalIgnoreCase);

    private bool _started;
    private bool _disposed;

    public ProtectionController(
        AppSettings settings,
        SettingsStore settingsStore)
    {
        Settings = settings;
        _settingsStore =
            settingsStore;
    }

    public AppSettings Settings
    {
        get;
        private set;
    }

    public bool Enabled =>
        Settings.Enabled;

    public bool CaptureExclusionAvailable
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Count > 0 &&
                       _sessions.All(
                           session =>
                               session
                                   .ExcludedFromCapture);
            }
        }
    }

    public event EventHandler? StateChanged;
    public event EventHandler? SettingsChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        RecreateSessions();

        SystemEvents.DisplaySettingsChanged +=
            OnDisplaySettingsChanged;
    }

    public void Toggle()
    {
        SetEnabled(
            !Enabled);
    }

    public void SetEnabled(
        bool enabled)
    {
        Settings.Enabled =
            enabled;
        _settingsStore.Save(
            Settings);

        lock (_sync)
        {
            foreach (var session in
                     _sessions)
            {
                session.SetEnabled(
                    enabled);
            }
        }

        StateChanged?.Invoke(
            this,
            EventArgs.Empty);
    }

    public void RevealAll(
        TimeSpan? duration = null)
    {
        var actualDuration =
            duration ??
            TimeSpan.FromSeconds(
                10);

        lock (_sync)
        {
            foreach (var session in
                     _sessions)
            {
                session.RevealAll(
                    actualDuration);
            }
        }
    }

    public void BeginManualZoneSelection()
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            new Action(
                () =>
                {
                    if (!NativeMethods.GetCursorPos(
                            out var cursor))
                    {
                        return;
                    }

                    var screen =
                        FormsScreen.FromPoint(
                            new DrawingPoint(
                                cursor.X,
                                cursor.Y));

                    RevealAll(
                        TimeSpan.FromMinutes(
                            1));

                    try
                    {
                        var selector =
                            new ManualZoneSelectionWindow(
                                screen);
                        var result =
                            selector.ShowDialog();

                        if (result != true ||
                            selector.SelectedNormalizedBounds is not
                                Rect selected)
                        {
                            return;
                        }

                        if (!_manualRevealZones.TryGetValue(
                                screen.DeviceName,
                                out var zones))
                        {
                            zones =
                                new List<Rect>();
                            _manualRevealZones[
                                screen.DeviceName] =
                                zones;
                        }

                        zones.Add(
                            selected);

                        foreach (var session in
                                 _sessions)
                        {
                            if (string.Equals(
                                    session.ScreenDeviceName,
                                    screen.DeviceName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                session.SetManualRevealZones(
                                    zones);
                            }
                        }
                    }
                    finally
                    {
                        RevealAll(
                            TimeSpan.Zero);
                    }
                }));
    }

    public void ClearManualRevealZones()
    {
        lock (_sync)
        {
            _manualRevealZones.Clear();

            foreach (var session in
                     _sessions)
            {
                session.SetManualRevealZones(
                    Array.Empty<Rect>());
            }
        }
    }

    public void SetDelaySeconds(
        int seconds)
    {
        var updated =
            Settings.Clone();

        updated
            .MotionZoneRecurringHoldMilliseconds =
            seconds *
            1000;

        ApplySettings(
            updated);
    }

    public void ApplySettings(
        AppSettings updated)
    {
        updated.Normalize();
        Settings = updated;

        _settingsStore.Save(
            Settings);
        StartupManager.Apply(
            Settings.StartWithWindows);

        RecreateSessions();

        SettingsChanged?.Invoke(
            this,
            EventArgs.Empty);
        StateChanged?.Invoke(
            this,
            EventArgs.Empty);
    }

    private void OnDisplaySettingsChanged(
        object? sender,
        EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            new Action(
                RecreateSessions));
    }

    private void RecreateSessions()
    {
        lock (_sync)
        {
            foreach (var session in
                     _sessions)
            {
                session.Dispose();
            }

            _sessions.Clear();

            foreach (var screen in
                     FormsScreen.AllScreens)
            {
                try
                {
                    var session =
                        new MonitorSession(
                            screen,
                            Settings);

                    if (_manualRevealZones.TryGetValue(
                            screen.DeviceName,
                            out var zones))
                    {
                        session.SetManualRevealZones(
                            zones);
                    }

                    _sessions.Add(
                        session);
                    session.Start(
                        Settings.Enabled);
                }
                catch
                {
                    // Continue protecting other monitors if one fails.
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SystemEvents.DisplaySettingsChanged -=
            OnDisplaySettingsChanged;

        lock (_sync)
        {
            foreach (var session in
                     _sessions)
            {
                session.Dispose();
            }

            _sessions.Clear();
        }
    }
}
