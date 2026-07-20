using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 4;

    // Keep zero as the deserialization default so older settings files without
    // SchemaVersion are migrated instead of silently keeping prototype values.
    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // A changed or mouse-revealed area remains active for this long.
    public int StaticDelaySeconds { get; set; } = 30;

    // Detection grid. The rendered mask is internally upscaled and interpolated,
    // so this value controls analysis cost rather than visible square size.
    public int CellSizePixels { get; set; } = 32;
    public int SamplesPerCell { get; set; } = 3;
    public int VisibleSamplingMilliseconds { get; set; } = 1000;
    public int MaskedSamplingMilliseconds { get; set; } = 250;

    // Temporal transitions.
    public int DarkenFadeMilliseconds { get; set; } = 5000;
    public int RevealFadeMilliseconds { get; set; } = 140;

    // Spatial shape of the visible island around recent activity.
    public int ActivityCoreRadiusPixels { get; set; } = 110;
    public int ActivityFeatherRadiusPixels { get; set; } = 220;
    public int ContentActivationPaddingCells { get; set; } = 1;

    // Mouse behaviour. The mouse directly activates this radius, then the common
    // core and feather are added around it by the distance-field renderer.
    public int MouseRevealRadiusPixels { get; set; } = 120;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;

    // Change detector thresholds. Weak changes need confirmation so tiny rendering
    // noise does not keep isolated pinholes visible.
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

        // v4 abandons independent dark squares. It renders a continuous activity
        // field: recent activity stays clear, the surroundings fade smoothly, and
        // distant inactive areas become fully black.
        CellSizePixels = 32;
        SamplesPerCell = 3;
        VisibleSamplingMilliseconds = 1000;
        MaskedSamplingMilliseconds = 250;
        DarkenFadeMilliseconds = 5000;
        RevealFadeMilliseconds = 140;
        ActivityCoreRadiusPixels = 110;
        ActivityFeatherRadiusPixels = 220;
        ContentActivationPaddingCells = 1;
        MouseRevealRadiusPixels = 120;
        MouseRevealHoldMilliseconds = 30_000;
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
        CellSizePixels = Math.Clamp(CellSizePixels, 20, 96);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 6);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 250, 10_000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 100, 5_000);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 500, 15_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 2_000);
        ActivityCoreRadiusPixels = Math.Clamp(ActivityCoreRadiusPixels, 32, 400);
        ActivityFeatherRadiusPixels = Math.Clamp(ActivityFeatherRadiusPixels, 32, 600);
        ContentActivationPaddingCells = Math.Clamp(ContentActivationPaddingCells, 0, 4);
        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 32, 500);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
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
