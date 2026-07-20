using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 7;

    // Keep zero as the deserialization default so older settings files without
    // SchemaVersion are migrated instead of silently keeping prototype values.
    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // A changed or mouse-revealed area remains active for this long.
    public int StaticDelaySeconds { get; set; } = 30;

    // Detection and rendering grid. Smaller cells create finer square zones while
    // keeping the mask and analysis buffers tiny.
    public int CellSizePixels { get; set; } = 24;
    public int SamplesPerCell { get; set; } = 4;
    public int VisibleSamplingMilliseconds { get; set; } = 1000;
    public int MaskedSamplingMilliseconds { get; set; } = 250;

    // Temporal transitions.
    public int DarkenFadeMilliseconds { get; set; } = 2600;
    public int RevealFadeMilliseconds { get; set; } = 120;

    // Spatial shape of the compact square island around recent activity.
    public int ActivityCoreRadiusPixels { get; set; } = 24;
    public int ActivityFeatherRadiusPixels { get; set; } = 72;
    public int ContentActivationPaddingCells { get; set; } = 0;

    // Mouse behaviour. The mouse directly activates this radius, then the common
    // core and feather are added around it by the distance-field renderer.
    public int MouseRevealRadiusPixels { get; set; } = 72;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;

    // Change detector thresholds. Weak changes need confirmation so tiny rendering
    // noise does not keep isolated pinholes visible.
    public double DifferenceThreshold { get; set; } = 3.5;
    public double ChangedSampleFraction { get; set; } = 0.18;
    public double StrongDifferenceThreshold { get; set; } = 10.0;
    public double StrongChangedSampleFraction { get; set; } = 0.32;
    public int WeakChangeConfirmationSamples { get; set; } = 2;
    public int MinimumChangedSamplesPerCell { get; set; } = 2;
    public int MinimumActivityClusterCells { get; set; } = 2;
    public int SpatialFadeSteps { get; set; } = 4;

    public bool StartWithWindows { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

        // v7 keeps the v4 activity-field behaviour, but uses compact square
        // zones, crisp opacity bands and a micro-animation filter.
        CellSizePixels = 24;
        SamplesPerCell = 4;
        VisibleSamplingMilliseconds = 1000;
        MaskedSamplingMilliseconds = 250;
        DarkenFadeMilliseconds = 2600;
        RevealFadeMilliseconds = 120;
        ActivityCoreRadiusPixels = 24;
        ActivityFeatherRadiusPixels = 72;
        ContentActivationPaddingCells = 0;
        MouseRevealRadiusPixels = 72;
        MouseRevealHoldMilliseconds = 30_000;
        DifferenceThreshold = 3.5;
        ChangedSampleFraction = 0.18;
        StrongDifferenceThreshold = 10.0;
        StrongChangedSampleFraction = 0.32;
        WeakChangeConfirmationSamples = 2;
        MinimumChangedSamplesPerCell = 2;
        MinimumActivityClusterCells = 2;
        SpatialFadeSteps = 4;
        SchemaVersion = CurrentSchemaVersion;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        StaticDelaySeconds = Math.Clamp(StaticDelaySeconds, 5, 600);
        CellSizePixels = Math.Clamp(CellSizePixels, 16, 64);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 6);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 250, 10_000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 100, 5_000);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 500, 15_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 2_000);
        ActivityCoreRadiusPixels = Math.Clamp(ActivityCoreRadiusPixels, 0, 160);
        ActivityFeatherRadiusPixels = Math.Clamp(ActivityFeatherRadiusPixels, 16, 240);
        ContentActivationPaddingCells = Math.Clamp(ContentActivationPaddingCells, 0, 4);
        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 24, 240);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
        DifferenceThreshold = Math.Clamp(DifferenceThreshold, 0.5, 50.0);
        ChangedSampleFraction = Math.Clamp(ChangedSampleFraction, 0.01, 1.0);
        StrongDifferenceThreshold = Math.Clamp(StrongDifferenceThreshold, DifferenceThreshold, 100.0);
        StrongChangedSampleFraction = Math.Clamp(StrongChangedSampleFraction, ChangedSampleFraction, 1.0);
        WeakChangeConfirmationSamples = Math.Clamp(WeakChangeConfirmationSamples, 1, 5);
        MinimumChangedSamplesPerCell = Math.Clamp(MinimumChangedSamplesPerCell, 1, SamplesPerCell * SamplesPerCell);
        MinimumActivityClusterCells = Math.Clamp(MinimumActivityClusterCells, 1, 8);
        SpatialFadeSteps = Math.Clamp(SpatialFadeSteps, 2, 8);
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
