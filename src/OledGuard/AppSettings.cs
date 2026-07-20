using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 5;

    // Keep zero as the deserialization default so older settings files without
    // SchemaVersion are migrated instead of silently keeping prototype values.
    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // A changed or mouse-revealed area remains active for this long.
    public int StaticDelaySeconds { get; set; } = 30;

    // Detection grid. The rendered mask is internally upscaled and interpolated,
    // so this value mostly controls analysis cost and the minimum activity block.
    public int CellSizePixels { get; set; } = 32;
    public int SamplesPerCell { get; set; } = 3;
    public int VisibleSamplingMilliseconds { get; set; } = 1000;
    public int MaskedSamplingMilliseconds { get; set; } = 250;

    // Temporal transitions.
    public int DarkenFadeMilliseconds { get; set; } = 5000;
    public int RevealFadeMilliseconds { get; set; } = 140;

    // Content activity is grouped into rectangular components. Large components
    // receive a compact square-edged feather; isolated blinking pixels use a much
    // smaller feather so they do not light a large circular area.
    public int ContentFeatherRadiusPixels { get; set; } = 72;
    public int MicroFeatherRadiusPixels { get; set; } = 18;
    public int ContentActivationPaddingCells { get; set; } = 0;
    public int ContentMergeGapCells { get; set; } = 2;
    public int MicroChangeMaxCells { get; set; } = 3;

    // Mouse movement is collected as one rectangular stroke. Every cell in that
    // stroke receives the same expiry time, so the whole block fades uniformly.
    public int MouseRevealRadiusPixels { get; set; } = 40;
    public int MouseFeatherRadiusPixels { get; set; } = 72;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;
    public int MouseStrokeIdleMilliseconds { get; set; } = 650;
    public int MouseHoverRadiusPixels { get; set; } = 8;
    public int MouseHoverRefreshMilliseconds { get; set; } = 500;

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

        // v5 replaces circular islands and time-staggered mouse trails with
        // rectangular activity components, a compact micro-change mask and one
        // common expiry for each continuous mouse stroke.
        CellSizePixels = 32;
        SamplesPerCell = 3;
        VisibleSamplingMilliseconds = 1000;
        MaskedSamplingMilliseconds = 250;
        DarkenFadeMilliseconds = 5000;
        RevealFadeMilliseconds = 140;
        ContentFeatherRadiusPixels = 72;
        MicroFeatherRadiusPixels = 18;
        ContentActivationPaddingCells = 0;
        ContentMergeGapCells = 2;
        MicroChangeMaxCells = 3;
        MouseRevealRadiusPixels = 40;
        MouseFeatherRadiusPixels = 72;
        MouseRevealHoldMilliseconds = 30_000;
        MouseStrokeIdleMilliseconds = 650;
        MouseHoverRadiusPixels = 8;
        MouseHoverRefreshMilliseconds = 500;
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
        ContentFeatherRadiusPixels = Math.Clamp(ContentFeatherRadiusPixels, 16, 240);
        MicroFeatherRadiusPixels = Math.Clamp(MicroFeatherRadiusPixels, 0, 96);
        ContentActivationPaddingCells = Math.Clamp(ContentActivationPaddingCells, 0, 3);
        ContentMergeGapCells = Math.Clamp(ContentMergeGapCells, 1, 4);
        MicroChangeMaxCells = Math.Clamp(MicroChangeMaxCells, 1, 4);
        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 0, 200);
        MouseFeatherRadiusPixels = Math.Clamp(MouseFeatherRadiusPixels, 16, 240);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
        MouseStrokeIdleMilliseconds = Math.Clamp(MouseStrokeIdleMilliseconds, 150, 3_000);
        MouseHoverRadiusPixels = Math.Clamp(MouseHoverRadiusPixels, 0, 96);
        MouseHoverRefreshMilliseconds = Math.Clamp(MouseHoverRefreshMilliseconds, 100, 2_000);
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
