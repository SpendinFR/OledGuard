using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfImage = System.Windows.Controls.Image;
using WpfWindow = System.Windows.Window;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OledGuard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        NativeMethods.TryEnableBestDpiAwareness();

        using var mutex = new Mutex(true, @"Local\OledGuard.Native.Singleton", out var createdNew);
        if (!createdNew)
        {
            Forms.MessageBox.Show(
                "OledGuard est dÃ©jÃ  lancÃ©.",
                "OledGuard",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Information);
            return;
        }

        var app = new WpfApplication
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        GuardController? controller = null;
        try
        {
            controller = new GuardController(app);
            app.Run();
        }
        catch (Exception exception)
        {
            Forms.MessageBox.Show(
                $"OledGuard n'a pas pu dÃ©marrer.\n\n{exception.Message}",
                "OledGuard",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
        finally
        {
            controller?.Dispose();
        }
    }
}

internal sealed class GuardSettings
{
    [Browsable(false)]
    public int SchemaVersion { get; set; } = 1;

    [Browsable(false)]
    public bool Enabled { get; set; } = true;

    [Browsable(false)]
    public string MonitorDeviceName { get; set; } = string.Empty;

    [Browsable(false)]
    public bool StartWithWindows { get; set; }

    [Category("DÃ©tection")]
    [DisplayName("DÃ©lai avant assombrissement (secondes)")]
    [Description("Temps pendant lequel un dÃ©tail lumineux doit rester immobile avant d'Ãªtre protÃ©gÃ©.")]
    public int StaticDelaySeconds { get; set; } = 30;

    [Category("DÃ©tection")]
    [DisplayName("Pleine protection aprÃ¨s (secondes)")]
    [Description("La force du masque augmente progressivement jusqu'Ã  ce dÃ©lai total.")]
    public int FullStrengthSeconds { get; set; } = 300;

    [Category("DÃ©tection")]
    [DisplayName("Seuil de mouvement")]
    [Description("Plus la valeur est basse, plus un petit changement retire vite le masque.")]
    public int DifferenceThreshold { get; set; } = 7;

    [Category("DÃ©tection")]
    [DisplayName("Contraste local minimal")]
    [Description("Seuil servant Ã  reconnaÃ®tre les textes, icÃ´nes, bordures et croix.")]
    public int DetailThreshold { get; set; } = 10;

    [Category("LuminositÃ©")]
    [DisplayName("LuminositÃ© minimale Ã  protÃ©ger")]
    [Description("Les pixels dÃ©jÃ  sombres ne sont pas assombris inutilement.")]
    public int MinimumLuminance { get; set; } = 62;

    [Category("LuminositÃ©")]
    [DisplayName("Assombrir aussi les grandes zones blanches")]
    public bool DimLargeBrightAreas { get; set; } = true;

    [Category("LuminositÃ©")]
    [DisplayName("Force maximale du masque")]
    [Description("0,60 signifie un voile noir maximal de 60 %.")]
    public double MaximumOpacity { get; set; } = 0.60;

    [Category("Animation")]
    [DisplayName("Fondu d'assombrissement (ms)")]
    public int DarkenFadeMilliseconds { get; set; } = 4_000;

    [Category("Animation")]
    [DisplayName("Fondu de rÃ©vÃ©lation (ms)")]
    public int RevealFadeMilliseconds { get; set; } = 180;

    [Category("Souris")]
    [DisplayName("Rayon rÃ©vÃ©lÃ© autour du curseur (pixels)")]
    [Description("Mettre 0 pour dÃ©sactiver la rÃ©vÃ©lation locale autour de la souris.")]
    public int CursorRevealRadiusPixels { get; set; } = 46;

    [Category("Performance")]
    [DisplayName("Largeur de la grille d'analyse")]
    [Description("640 donne environ 6 pixels de prÃ©cision sur un Ã©cran 4K.")]
    public int SampleWidth { get; set; } = 640;

    [Category("Performance")]
    [DisplayName("Intervalle de capture (ms)")]
    public int CaptureIntervalMilliseconds { get; set; } = 750;

    public GuardSettings Clone() => (GuardSettings)MemberwiseClone();

    public void Normalize()
    {
        SchemaVersion = 1;
        StaticDelaySeconds = Math.Clamp(StaticDelaySeconds, 5, 3_600);
        FullStrengthSeconds = Math.Clamp(FullStrengthSeconds, StaticDelaySeconds + 5, 14_400);
        DifferenceThreshold = Math.Clamp(DifferenceThreshold, 1, 40);
        DetailThreshold = Math.Clamp(DetailThreshold, 1, 80);
        MinimumLuminance = Math.Clamp(MinimumLuminance, 0, 240);
        MaximumOpacity = Math.Clamp(MaximumOpacity, 0.10, 0.90);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 250, 60_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 50, 5_000);
        CursorRevealRadiusPixels = Math.Clamp(CursorRevealRadiusPixels, 0, 300);
        SampleWidth = Math.Clamp(SampleWidth, 160, 960);
        CaptureIntervalMilliseconds = Math.Clamp(CaptureIntervalMilliseconds, 250, 3_000);
    }
}

internal static class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OledGuard");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings-native.json");

    public static GuardSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<GuardSettings>(File.ReadAllText(FilePath));
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch
        {
            // A damaged settings file must never prevent protection from starting.
        }

        var defaults = new GuardSettings();
        defaults.Normalize();
        return defaults;
    }

    public static void Save(GuardSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(temporary, json);
        File.Move(temporary, FilePath, true);
    }
}

internal sealed class MonitorChoice
{
    public MonitorChoice(Forms.Screen screen)
    {
        Screen = screen;
    }

    public Forms.Screen Screen { get; }

    public override string ToString()
    {
        var bounds = Screen.Bounds;
        var primary = Screen.Primary ? " â€” principal" : string.Empty;
        return $"{Screen.DeviceName} â€” {bounds.Width} Ã— {bounds.Height}{primary}";
    }
}

internal static class MonitorPicker
{
    public static Forms.Screen Choose(Forms.Screen? current)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0)
        {
            throw new InvalidOperationException("Aucun Ã©cran Windows n'a Ã©tÃ© dÃ©tectÃ©.");
        }

        if (screens.Length == 1)
        {
            return screens[0];
        }

        using var form = new Forms.Form
        {
            Text = "OledGuard â€” choisir la TV OLED",
            Width = 560,
            Height = 180,
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = Forms.FormStartPosition.CenterScreen,
            AutoScaleMode = Forms.AutoScaleMode.Dpi
        };

        var label = new Forms.Label
        {
            Text = "Choisis l'Ã©cran OLED Ã  protÃ©ger :",
            AutoSize = true,
            Left = 18,
            Top = 18
        };

        var combo = new Forms.ComboBox
        {
            Left = 18,
            Top = 45,
            Width = 510,
            DropDownStyle = Forms.ComboBoxStyle.DropDownList
        };

        var choices = screens.Select(screen => new MonitorChoice(screen)).ToArray();
        combo.Items.AddRange(choices.Cast<object>().ToArray());
        var currentIndex = Array.FindIndex(
            choices,
            choice => current is not null && choice.Screen.DeviceName == current.DeviceName);
        combo.SelectedIndex = currentIndex >= 0 ? currentIndex : Array.FindIndex(choices, choice => choice.Screen.Primary);
        if (combo.SelectedIndex < 0)
        {
            combo.SelectedIndex = 0;
        }

        var ok = new Forms.Button
        {
            Text = "ProtÃ©ger cet Ã©cran",
            DialogResult = Forms.DialogResult.OK,
            Width = 150,
            Left = 378,
            Top = 86
        };

        var cancel = new Forms.Button
        {
            Text = "Annuler",
            DialogResult = Forms.DialogResult.Cancel,
            Width = 100,
            Left = 268,
            Top = 86
        };

        form.Controls.AddRange(new Forms.Control[] { label, combo, cancel, ok });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == Forms.DialogResult.OK
            ? ((MonitorChoice)combo.SelectedItem!).Screen
            : current ?? screens.FirstOrDefault(screen => screen.Primary) ?? screens[0];
    }
}

internal static class SettingsEditor
{
    public static GuardSettings? Edit(GuardSettings current)
    {
        var edited = current.Clone();

        using var form = new Forms.Form
        {
            Text = "OledGuard â€” paramÃ¨tres",
            Width = 610,
            Height = 640,
            StartPosition = Forms.FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoScaleMode = Forms.AutoScaleMode.Dpi
        };

        var grid = new Forms.PropertyGrid
        {
            Dock = Forms.DockStyle.Fill,
            SelectedObject = edited,
            PropertySort = Forms.PropertySort.Categorized,
            HelpVisible = true,
            ToolbarVisible = false
        };

        var buttons = new Forms.FlowLayoutPanel
        {
            Dock = Forms.DockStyle.Bottom,
            Height = 48,
            FlowDirection = Forms.FlowDirection.RightToLeft,
            Padding = new Forms.Padding(8)
        };

        var ok = new Forms.Button
        {
            Text = "Appliquer",
            DialogResult = Forms.DialogResult.OK,
            Width = 100
        };

        var cancel = new Forms.Button
        {
            Text = "Annuler",
            DialogResult = Forms.DialogResult.Cancel,
            Width = 100
        };

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        form.Controls.Add(grid);
        form.Controls.Add(buttons);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog() != Forms.DialogResult.OK)
        {
            return null;
        }

        edited.Normalize();
        return edited;
    }
}

internal sealed class GuardController : IDisposable
{
    private const string StartupValueName = "OledGuard";
    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly WpfApplication _application;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _enabledItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private GuardSettings _settings;
    private MonitorSession? _session;
    private bool _disposed;
    private bool _suppressStartupChange;

    public GuardController(WpfApplication application)
    {
        _application = application;
        _settings = SettingsStore.Load();
        _settings.StartWithWindows = IsStartupEnabled();

        var currentScreen = ResolveScreen(_settings.MonitorDeviceName);
        if (currentScreen is null)
        {
            currentScreen = MonitorPicker.Choose(Forms.Screen.PrimaryScreen);
            _settings.MonitorDeviceName = currentScreen.DeviceName;
            SettingsStore.Save(_settings);
        }

        var menu = new Forms.ContextMenuStrip();
        _enabledItem = new Forms.ToolStripMenuItem("Protection active")
        {
            Checked = _settings.Enabled,
            CheckOnClick = true
        };
        _enabledItem.CheckedChanged += (_, _) => SetEnabled(_enabledItem.Checked);

        var revealItem = new Forms.ToolStripMenuItem("RÃ©vÃ©ler tout pendant 20 secondes");
        revealItem.Click += (_, _) => _session?.RevealAll(TimeSpan.FromSeconds(20));

        var resetItem = new Forms.ToolStripMenuItem("RÃ©initialiser la dÃ©tection");
        resetItem.Click += (_, _) => _session?.Reset();

        var settingsItem = new Forms.ToolStripMenuItem("ParamÃ¨tresâ€¦");
        settingsItem.Click += (_, _) => OpenSettings();

        var monitorItem = new Forms.ToolStripMenuItem("Choisir l'Ã©cran OLEDâ€¦");
        monitorItem.Click += (_, _) => ChooseMonitor();

        _startupItem = new Forms.ToolStripMenuItem("Lancer avec Windows")
        {
            Checked = _settings.StartWithWindows,
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressStartupChange)
            {
                ChangeStartup(_startupItem.Checked);
            }
        };

        var quitItem = new Forms.ToolStripMenuItem("Quitter OledGuard");
        quitItem.Click += (_, _) => Quit();

        menu.Items.Add(_enabledItem);
        menu.Items.Add(revealItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(monitorItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _tray = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "OledGuard",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) =>
        {
            _enabledItem.Checked = !_enabledItem.Checked;
        };

        StartSession();

        _tray.BalloonTipTitle = "OledGuard est actif";
        _tray.BalloonTipText = "Les textes, icÃ´nes, croix et zones blanches immobiles seront assombris localement.";
        _tray.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _tray.ShowBalloonTip(4_000);
    }

    private Forms.Screen? ResolveScreen(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        return Forms.Screen.AllScreens.FirstOrDefault(
            screen => string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    private void StartSession()
    {
        _session?.Dispose();
        _session = null;

        var screen = ResolveScreen(_settings.MonitorDeviceName)
            ?? Forms.Screen.PrimaryScreen
            ?? Forms.Screen.AllScreens.First();

        _settings.MonitorDeviceName = screen.DeviceName;
        SettingsStore.Save(_settings);

        try
        {
            _session = new MonitorSession(screen, _settings.Clone());
            _session.Faulted += OnSessionFaulted;
            _session.SetEnabled(_settings.Enabled);
            _tray.Text = $"OledGuard â€” {screen.DeviceName}";
        }
        catch (Exception exception)
        {
            _enabledItem.Checked = false;
            _tray.BalloonTipTitle = "OledGuard en pause";
            _tray.BalloonTipText = exception.Message;
            _tray.BalloonTipIcon = Forms.ToolTipIcon.Error;
            _tray.ShowBalloonTip(8_000);
        }
    }

    private void OnSessionFaulted(string message)
    {
        _enabledItem.Checked = false;
        _tray.BalloonTipTitle = "Capture impossible";
        _tray.BalloonTipText = message;
        _tray.BalloonTipIcon = Forms.ToolTipIcon.Error;
        _tray.ShowBalloonTip(8_000);
    }

    private void SetEnabled(bool enabled)
    {
        _settings.Enabled = enabled;
        SettingsStore.Save(_settings);
        _session?.SetEnabled(enabled);
        _enabledItem.Text = enabled ? "Protection active" : "Protection en pause";
    }

    private void OpenSettings()
    {
        var edited = SettingsEditor.Edit(_settings);
        if (edited is null)
        {
            return;
        }

        edited.Enabled = _settings.Enabled;
        edited.MonitorDeviceName = _settings.MonitorDeviceName;
        edited.StartWithWindows = _settings.StartWithWindows;
        _settings = edited;
        SettingsStore.Save(_settings);
        StartSession();
    }

    private void ChooseMonitor()
    {
        var current = ResolveScreen(_settings.MonitorDeviceName);
        var chosen = MonitorPicker.Choose(current);
        _settings.MonitorDeviceName = chosen.DeviceName;
        SettingsStore.Save(_settings);
        StartSession();
    }

    private void ChangeStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath, true);
            if (enabled)
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Chemin de l'exÃ©cutable introuvable.");
                key.SetValue(StartupValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }

            _settings.StartWithWindows = enabled;
            SettingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            _suppressStartupChange = true;
            _startupItem.Checked = !enabled;
            _suppressStartupChange = false;
            Forms.MessageBox.Show(
                $"Impossible de modifier le dÃ©marrage Windows.\n\n{exception.Message}",
                "OledGuard",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Warning);
        }
    }


    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, false);
            return key?.GetValue(StartupValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private void Quit()
    {
        Dispose();
        _application.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session is not null)
        {
            _session.Faulted -= OnSessionFaulted;
            _session.Dispose();
            _session = null;
        }

        _tray.Visible = false;
        _tray.Dispose();
    }
}

internal sealed class MonitorSession : IDisposable
{
    private readonly Forms.Screen _screen;
    private readonly GuardSettings _settings;
    private readonly int _width;
    private readonly int _height;
    private readonly int _pixelCount;
    private readonly ScreenSampler _sampler;
    private readonly OverlayWindow _overlay;
    private readonly DispatcherTimer _captureTimer;
    private readonly DispatcherTimer _animationTimer;
    private readonly byte[] _captureBuffer;
    private readonly byte[] _luminance;
    private readonly byte[] _previousLuminance;
    private readonly float[] _staticSeconds;
    private readonly byte[] _candidateAlpha;
    private readonly float[] _targetAlpha;
    private readonly float[] _currentAlpha;
    private readonly byte[] _overlayPixels;
    private bool _enabled;
    private bool _hasPreviousFrame;
    private bool _disposed;
    private bool _faultReported;
    private long _lastCaptureTicks;
    private long _lastAnimationTicks;
    private long _revealAllUntilTicks;

    public MonitorSession(Forms.Screen screen, GuardSettings settings)
    {
        _screen = screen;
        _settings = settings;
        _settings.Normalize();

        var bounds = screen.Bounds;
        _width = Math.Min(_settings.SampleWidth, bounds.Width);
        _height = Math.Max(90, (int)Math.Round(_width * (double)bounds.Height / bounds.Width));
        if (_height > 540)
        {
            _height = 540;
            _width = Math.Max(160, (int)Math.Round(_height * (double)bounds.Width / bounds.Height));
        }

        _pixelCount = checked(_width * _height);
        _captureBuffer = new byte[checked(_pixelCount * 4)];
        _luminance = new byte[_pixelCount];
        _previousLuminance = new byte[_pixelCount];
        _staticSeconds = new float[_pixelCount];
        _candidateAlpha = new byte[_pixelCount];
        _targetAlpha = new float[_pixelCount];
        _currentAlpha = new float[_pixelCount];
        _overlayPixels = new byte[checked(_pixelCount * 4)];

        _sampler = new ScreenSampler(bounds, _width, _height);
        _overlay = new OverlayWindow(screen, _width, _height);
        _overlay.Show();

        _captureTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(_settings.CaptureIntervalMilliseconds)
        };
        _captureTimer.Tick += OnCaptureTick;

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _animationTimer.Tick += OnAnimationTick;
        _animationTimer.Start();
    }

    public event Action<string>? Faulted;

    public void SetEnabled(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            _hasPreviousFrame = false;
            _lastCaptureTicks = 0;
            _captureTimer.Start();
            CaptureFrame();
        }
        else
        {
            _captureTimer.Stop();
            ClearMaskImmediately();
        }
    }

    public void Reset()
    {
        Array.Clear(_staticSeconds);
        Array.Clear(_targetAlpha);
        Array.Clear(_currentAlpha);
        _hasPreviousFrame = false;
        ClearMaskImmediately();
    }

    public void RevealAll(TimeSpan duration)
    {
        _revealAllUntilTicks = Stopwatch.GetTimestamp() + ToStopwatchTicks(duration.TotalMilliseconds);
        Array.Clear(_targetAlpha);
        ClearMaskImmediately();
    }

    private void OnCaptureTick(object? sender, EventArgs e)
    {
        CaptureFrame();
    }

    private void CaptureFrame()
    {
        if (!_enabled || _disposed)
        {
            return;
        }

        try
        {
            _sampler.Capture(_captureBuffer);
            var now = Stopwatch.GetTimestamp();
            var elapsedSeconds = _lastCaptureTicks == 0
                ? _settings.CaptureIntervalMilliseconds / 1000.0
                : (now - _lastCaptureTicks) / (double)Stopwatch.Frequency;
            _lastCaptureTicks = now;
            elapsedSeconds = Math.Clamp(elapsedSeconds, 0.05, 3.0);

            ComputeLuminance();
            if (!_hasPreviousFrame)
            {
                Array.Copy(_luminance, _previousLuminance, _pixelCount);
                _hasPreviousFrame = true;
                return;
            }

            UpdateStability((float)elapsedSeconds);
            BuildTargetMask(now);
        }
        catch (Exception exception)
        {
            _captureTimer.Stop();
            ClearMaskImmediately();
            if (!_faultReported)
            {
                _faultReported = true;
                Faulted?.Invoke(exception.Message);
            }
        }
    }

    private void ComputeLuminance()
    {
        for (var index = 0; index < _pixelCount; index++)
        {
            var offset = index * 4;
            var blue = _captureBuffer[offset];
            var green = _captureBuffer[offset + 1];
            var red = _captureBuffer[offset + 2];
            _luminance[index] = (byte)((red * 54 + green * 183 + blue * 19) >> 8);
        }
    }

    private void UpdateStability(float elapsedSeconds)
    {
        var threshold = _settings.DifferenceThreshold;
        for (var index = 0; index < _pixelCount; index++)
        {
            var current = _luminance[index];
            var previous = _previousLuminance[index];
            if (Math.Abs(current - previous) >= threshold)
            {
                _staticSeconds[index] = 0f;
            }
            else
            {
                _staticSeconds[index] = Math.Min(86_400f, _staticSeconds[index] + elapsedSeconds);
            }

            _previousLuminance[index] = current;
        }
    }

    private void BuildTargetMask(long now)
    {
        Array.Clear(_candidateAlpha);
        var delay = _settings.StaticDelaySeconds;
        var fullStrength = Math.Max(delay + 1, _settings.FullStrengthSeconds);
        var minimum = _settings.MinimumLuminance;
        var detailThreshold = _settings.DetailThreshold;
        var maximumOpacity = _settings.MaximumOpacity;

        for (var y = 1; y < _height - 1; y++)
        {
            var row = y * _width;
            for (var x = 1; x < _width - 1; x++)
            {
                var index = row + x;
                var age = _staticSeconds[index];
                var luminance = _luminance[index];
                if (age < delay || luminance < minimum)
                {
                    continue;
                }

                var localContrast = 0;
                localContrast = Math.Max(localContrast, Math.Abs(luminance - _luminance[index - 1]));
                localContrast = Math.Max(localContrast, Math.Abs(luminance - _luminance[index + 1]));
                localContrast = Math.Max(localContrast, Math.Abs(luminance - _luminance[index - _width]));
                localContrast = Math.Max(localContrast, Math.Abs(luminance - _luminance[index + _width]));

                var isDetail = localContrast >= detailThreshold;
                var isLargeBrightArea = _settings.DimLargeBrightAreas && luminance >= 224;
                if (!isDetail && !isLargeBrightArea)
                {
                    continue;
                }

                var progress = Math.Clamp((age - delay) / (fullStrength - delay), 0f, 1f);
                var smoothProgress = progress * progress * (3f - 2f * progress);
                var startingOpacity = Math.Min(maximumOpacity, 0.18);
                var opacity = startingOpacity + smoothProgress * (maximumOpacity - startingOpacity);
                var brightnessProgress = Math.Clamp((luminance - minimum) / Math.Max(1.0, 255.0 - minimum), 0.0, 1.0);
                opacity *= 0.62 + 0.38 * brightnessProgress;
                _candidateAlpha[index] = (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
            }
        }

        // One-cell expansion covers antialiased edges around text and icons.
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                byte maximum = 0;
                var yStart = Math.Max(0, y - 1);
                var yEnd = Math.Min(_height - 1, y + 1);
                var xStart = Math.Max(0, x - 1);
                var xEnd = Math.Min(_width - 1, x + 1);
                for (var neighbourY = yStart; neighbourY <= yEnd; neighbourY++)
                {
                    var neighbourRow = neighbourY * _width;
                    for (var neighbourX = xStart; neighbourX <= xEnd; neighbourX++)
                    {
                        var candidate = _candidateAlpha[neighbourRow + neighbourX];
                        if (candidate > maximum)
                        {
                            maximum = candidate;
                        }
                    }
                }

                _targetAlpha[y * _width + x] = maximum;
            }
        }

        ApplyCursorReveal();

        if (now < _revealAllUntilTicks)
        {
            Array.Clear(_targetAlpha);
        }
    }

    private void ApplyCursorReveal()
    {
        var physicalRadius = _settings.CursorRevealRadiusPixels;
        if (physicalRadius <= 0 || !NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        var bounds = _screen.Bounds;
        if (!bounds.Contains(point.X, point.Y))
        {
            return;
        }

        var centerX = (point.X - bounds.X) * _width / Math.Max(1, bounds.Width);
        var centerY = (point.Y - bounds.Y) * _height / Math.Max(1, bounds.Height);
        var radius = Math.Max(1, physicalRadius * _width / Math.Max(1, bounds.Width));
        var feather = Math.Max(1, radius / 2);
        var outer = radius + feather;

        var yStart = Math.Max(0, centerY - outer);
        var yEnd = Math.Min(_height - 1, centerY + outer);
        var xStart = Math.Max(0, centerX - outer);
        var xEnd = Math.Min(_width - 1, centerX + outer);

        for (var y = yStart; y <= yEnd; y++)
        {
            for (var x = xStart; x <= xEnd; x++)
            {
                var deltaX = x - centerX;
                var deltaY = y - centerY;
                var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                var index = y * _width + x;
                if (distance <= radius)
                {
                    _targetAlpha[index] = 0f;
                }
                else if (distance < outer)
                {
                    var factor = (distance - radius) / feather;
                    _targetAlpha[index] *= (float)factor;
                }
            }
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = _lastAnimationTicks == 0
            ? 0.05
            : (now - _lastAnimationTicks) / (double)Stopwatch.Frequency;
        _lastAnimationTicks = now;
        elapsedSeconds = Math.Clamp(elapsedSeconds, 0.01, 0.25);

        var riseTime = Math.Max(0.05, _settings.DarkenFadeMilliseconds / 1000.0);
        var fallTime = Math.Max(0.02, _settings.RevealFadeMilliseconds / 1000.0);
        var riseFactor = 1.0 - Math.Exp(-elapsedSeconds / riseTime);
        var fallFactor = 1.0 - Math.Exp(-elapsedSeconds / fallTime);

        for (var index = 0; index < _pixelCount; index++)
        {
            var current = _currentAlpha[index];
            var target = _targetAlpha[index];
            var factor = target > current ? riseFactor : fallFactor;
            current += (float)((target - current) * factor);
            if (current < 0.35f)
            {
                current = 0f;
            }

            _currentAlpha[index] = current;
            _overlayPixels[index * 4 + 3] = (byte)Math.Clamp((int)Math.Round(current), 0, 255);
        }

        _overlay.UpdateMask(_overlayPixels);
    }

    private void ClearMaskImmediately()
    {
        Array.Clear(_targetAlpha);
        Array.Clear(_currentAlpha);
        Array.Clear(_overlayPixels);
        _overlay.UpdateMask(_overlayPixels);
    }

    private static long ToStopwatchTicks(double milliseconds)
    {
        return (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _captureTimer.Stop();
        _animationTimer.Stop();
        _captureTimer.Tick -= OnCaptureTick;
        _animationTimer.Tick -= OnAnimationTick;
        _sampler.Dispose();
        _overlay.ClosePermanently();
    }
}

internal sealed class OverlayWindow : WpfWindow
{
    private readonly Forms.Screen _screen;
    private readonly WriteableBitmap _mask;
    private bool _allowClose;

    public OverlayWindow(Forms.Screen screen, int maskWidth, int maskHeight)
    {
        _screen = screen;
        _mask = new WriteableBitmap(maskWidth, maskHeight, 96, 96, PixelFormats.Bgra32, null);

        var image = new WpfImage
        {
            Source = _mask,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        WindowStyle = System.Windows.WindowStyle.None;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        Content = image;

        var bounds = screen.Bounds;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        SourceInitialized += (_, _) => ApplyNativeWindowSettings();
    }

    public void UpdateMask(byte[] pixels)
    {
        if (_allowClose || pixels.Length != _mask.PixelWidth * _mask.PixelHeight * 4)
        {
            return;
        }

        _mask.WritePixels(
            new Int32Rect(0, 0, _mask.PixelWidth, _mask.PixelHeight),
            pixels,
            _mask.PixelWidth * 4,
            0);
    }

    private void ApplyNativeWindowSettings()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle |= NativeMethods.WsExTransparent;
        extendedStyle |= NativeMethods.WsExNoActivate;
        extendedStyle |= NativeMethods.WsExToolWindow;
        extendedStyle |= NativeMethods.WsExLayered;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(extendedStyle));

        var bounds = _screen.Bounds;
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow | NativeMethods.SwpFrameChanged);

        // Extra safety: the black overlay must not be seen by the detector itself.
        NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }
}

internal sealed class ScreenSampler : IDisposable
{
    private readonly Drawing.Rectangle _bounds;
    private readonly int _width;
    private readonly int _height;
    private readonly int _byteCount;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _oldBitmap;
    private readonly IntPtr _bits;
    private bool _disposed;

    public ScreenSampler(Drawing.Rectangle bounds, int width, int height)
    {
        _bounds = bounds;
        _width = width;
        _height = height;
        _byteCount = checked(width * height * 4);

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible d'accÃ©der Ã  l'Ã©cran.");
        }

        try
        {
            _memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible de crÃ©er la capture mÃ©moire.");
            }

            var bitmapInfo = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BiRgb
                }
            };

            _bitmap = NativeMethods.CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                NativeMethods.DibRgbColors,
                out _bits,
                IntPtr.Zero,
                0);
            if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible de crÃ©er l'image de capture.");
            }

            _oldBitmap = NativeMethods.SelectObject(_memoryDc, _bitmap);
            NativeMethods.SetStretchBltMode(_memoryDc, NativeMethods.ColorOnColor);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Capture(byte[] destination)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ScreenSampler));
        }

        if (destination.Length < _byteCount)
        {
            throw new ArgumentException("Tampon de capture trop petit.", nameof(destination));
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Capture de l'Ã©cran impossible.");
        }

        try
        {
            var success = NativeMethods.StretchBlt(
                _memoryDc,
                0,
                0,
                _width,
                _height,
                screenDc,
                _bounds.X,
                _bounds.Y,
                _bounds.Width,
                _bounds.Height,
                NativeMethods.Srccopy);
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows a refusÃ© la capture de l'Ã©cran.");
            }

            Marshal.Copy(_bits, destination, 0, _byteCount);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_oldBitmap != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memoryDc, _oldBitmap);
        }

        if (_bitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_bitmap);
        }

        if (_memoryDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memoryDc);
        }
    }
}

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const long WsExTransparent = 0x00000020L;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExLayered = 0x00080000L;
    public const long WsExNoActivate = 0x08000000L;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpFrameChanged = 0x0020;
    public static readonly IntPtr HwndTopmost = new(-1);
    public const uint WdaExcludeFromCapture = 0x00000011;
    public const uint BiRgb = 0;
    public const uint DibRgbColors = 0;
    public const int ColorOnColor = 3;
    public const uint Srccopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    public static void TryEnableBestDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
            {
                return;
            }
        }
        catch
        {
            // Older Windows versions do not expose this API.
        }

        try
        {
            SetProcessDPIAware();
        }
        catch
        {
            // The app still works; coordinates may only be less exact on mixed DPI.
        }
    }

    public static IntPtr GetWindowLongPtr(IntPtr window, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));
    }

    public static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr dc,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool StretchBlt(
        IntPtr destinationDc,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        IntPtr sourceDc,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        uint operation);
}