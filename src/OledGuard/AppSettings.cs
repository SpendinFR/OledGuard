using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 3;

    // Keep zero as the deserialization default so older settings files without
    // SchemaVersion are migrated instead of silently keeping prototype values.
    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;
    public int StaticDelaySeconds { get; set; } = 30;
    public int CellSizePixels { get; set; } = 16;
    public int SamplesPerCell { get; set; } = 3;
    public int VisibleSamplingMilliseconds { get; set; } = 1000;
    public int MaskedSamplingMilliseconds { get; set; } = 250;
    public int DarkenFadeMilliseconds { get; set; } = 900;
    public int RevealFadeMilliseconds { get; set; } = 120;
    public int MouseRevealRadiusPixels { get; set; } = 170;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;
    public int ContentRevealPaddingCells { get; set; } = 1;
    public int ContentRevealHoldMilliseconds { get; set; } = 1200;

    // Weak changes need confirmation so tiny rendering noise does not punch
    // permanent pinholes in an otherwise static image.
    public double DifferenceThreshold { get; set; } = 3.0;
    public double ChangedSampleFraction { get; set; } = 0.10;
    public double StrongDifferenceThreshold { get; set; } = 9.0;
    public double StrongChangedSampleFraction { get; set; } = 0.24;
    public int WeakChangeConfirmationSamples { get; set; } = 2;

    public bool StartWithWindows { get; set; } = false;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

        // v3 replaces the coarse prototype mask with a denser grid, smoother
        // rendering, robust noise handling, and a 30-second mouse reveal hold.
        CellSizePixels = 16;
        SamplesPerCell = 3;
        VisibleSamplingMilliseconds = 1000;
        MaskedSamplingMilliseconds = 250;
        DarkenFadeMilliseconds = 900;
        RevealFadeMilliseconds = 120;
        MouseRevealRadiusPixels = 170;
        MouseRevealHoldMilliseconds = 30_000;
        ContentRevealPaddingCells = 1;
        ContentRevealHoldMilliseconds = 1200;
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
        CellSizePixels = Math.Clamp(CellSizePixels, 12, 96);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 6);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 250, 10_000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 100, 5_000);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 100, 5_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 2_000);
        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 60, 500);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
        ContentRevealPaddingCells = Math.Clamp(ContentRevealPaddingCells, 0, 3);
        ContentRevealHoldMilliseconds = Math.Clamp(ContentRevealHoldMilliseconds, 0, 10_000);
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
