using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 11;

    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // A foreground window starts fully visible. Static zones begin to dim after
    // this grace period and then fade uniformly by coarse zones.
    public int StaticDelaySeconds { get; set; } = 30;
    public int StaticFadeSeconds { get; set; } = 20;
    public double MaximumStaticOpacity { get; set; } = 0.94;

    // Low-resolution analysis. On a 4K monitor this is roughly 360 x 204 BGRA
    // with the defaults, so the working buffers remain tiny.
    public int CellSizePixels { get; set; } = 32;
    public int SamplesPerCell { get; set; } = 3;
    public int StaticZoneSpanCells { get; set; } = 2;
    public int VisibleSamplingMilliseconds { get; set; } = 750;
    public int MaskedSamplingMilliseconds { get; set; } = 250;

    // Window and temporal transitions.
    public int WindowEdgeFeatherPixels { get; set; } = 20;
    public int DarkenFadeMilliseconds { get; set; } = 1800;
    public int RevealFadeMilliseconds { get; set; } = 120;

    // Mouse reveal keeps the original smooth, circular trail behaviour. The
    // overlay remains click-through; the mouse only changes the protection map.
    public int MouseRevealRadiusPixels { get; set; } = 72;
    public int MouseRevealFeatherPixels { get; set; } = 72;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;
    public int MouseStampDistancePixels { get; set; } = 10;

    // A moving dark graphite band gives continuously active pixels a brief rest.
    // It never adds light or colour, which is safer for OLED than a bright sweep.
    public bool RestSweepEnabled { get; set; } = true;
    public int RestSweepIntervalSeconds { get; set; } = 90;
    public int RestSweepDurationSeconds { get; set; } = 7;
    public int RestSweepWidthPixels { get; set; } = 260;
    public double RestSweepOpacity { get; set; } = 0.28;

    // Change detector thresholds.
    public double DifferenceThreshold { get; set; } = 3.0;
    public double ChangedSampleFraction { get; set; } = 0.10;
    public double StrongDifferenceThreshold { get; set; } = 9.0;
    public double StrongChangedSampleFraction { get; set; } = 0.24;
    public int WeakChangeConfirmationSamples { get; set; } = 2;

    public bool StartWithWindows { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

        StaticDelaySeconds = 30;
        StaticFadeSeconds = 20;
        MaximumStaticOpacity = 0.94;
        CellSizePixels = 32;
        SamplesPerCell = 3;
        StaticZoneSpanCells = 2;
        VisibleSamplingMilliseconds = 750;
        MaskedSamplingMilliseconds = 250;
        WindowEdgeFeatherPixels = 20;
        DarkenFadeMilliseconds = 1800;
        RevealFadeMilliseconds = 120;
        MouseRevealRadiusPixels = 72;
        MouseRevealFeatherPixels = 72;
        MouseRevealHoldMilliseconds = 30_000;
        MouseStampDistancePixels = 10;
        RestSweepEnabled = true;
        RestSweepIntervalSeconds = 90;
        RestSweepDurationSeconds = 7;
        RestSweepWidthPixels = 260;
        RestSweepOpacity = 0.28;
        DifferenceThreshold = 3.0;
        ChangedSampleFraction = 0.10;
        StrongDifferenceThreshold = 9.0;
        StrongChangedSampleFraction = 0.24;
        WeakChangeConfirmationSamples = 2;
        SchemaVersion = CurrentSchemaVersion;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        StaticDelaySeconds = Math.Clamp(StaticDelaySeconds, 5, 600);
        StaticFadeSeconds = Math.Clamp(StaticFadeSeconds, 1, 180);
        MaximumStaticOpacity = Math.Clamp(MaximumStaticOpacity, 0.50, 1.0);
        CellSizePixels = Math.Clamp(CellSizePixels, 24, 96);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 6);
        StaticZoneSpanCells = Math.Clamp(StaticZoneSpanCells, 1, 4);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 250, 10_000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 100, 5_000);
        WindowEdgeFeatherPixels = Math.Clamp(WindowEdgeFeatherPixels, 0, 96);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 250, 10_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 2_000);
        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 16, 240);
        MouseRevealFeatherPixels = Math.Clamp(MouseRevealFeatherPixels, 0, 240);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
        MouseStampDistancePixels = Math.Clamp(MouseStampDistancePixels, 2, 48);
        RestSweepIntervalSeconds = Math.Clamp(RestSweepIntervalSeconds, 20, 600);
        RestSweepDurationSeconds = Math.Clamp(RestSweepDurationSeconds, 2, 30);
        RestSweepWidthPixels = Math.Clamp(RestSweepWidthPixels, 64, 800);
        RestSweepOpacity = Math.Clamp(RestSweepOpacity, 0.05, 0.60);
        DifferenceThreshold = Math.Clamp(DifferenceThreshold, 0.5, 50.0);
        ChangedSampleFraction = Math.Clamp(ChangedSampleFraction, 0.01, 1.0);
        StrongDifferenceThreshold = Math.Clamp(StrongDifferenceThreshold, DifferenceThreshold, 100.0);
        StrongChangedSampleFraction = Math.Clamp(StrongChangedSampleFraction, ChangedSampleFraction, 1.0);
        WeakChangeConfirmationSamples = Math.Clamp(WeakChangeConfirmationSamples, 1, 5);
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OledGuard");

    public string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            AppSettings settings;
            if (!File.Exists(SettingsPath))
            {
                settings = new AppSettings();
            }
            else
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }

            var previousSchema = settings.SchemaVersion;
            settings.Migrate();
            settings.Normalize();

            if (previousSchema != settings.SchemaVersion || !File.Exists(SettingsPath))
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            var settings = new AppSettings();
            settings.Migrate();
            settings.Normalize();
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            settings.Normalize();
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // A settings write failure must never leave a dark overlay stuck on screen.
        }
    }
}
