using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 41;

    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; }

    public double MaximumMaskOpacity { get; set; } = 0.85;

    public int MotionZoneCaptureWidth { get; set; } = 1280;
    public int MotionZoneSamplesPerCell { get; set; } = 4;
    public int MotionZoneSamplingMilliseconds { get; set; } = 20;
    public int MotionZonePixelThreshold { get; set; } = 10;
    public double MotionZoneChangedFraction { get; set; } = 0.08;

    public int MotionZonePaddingCells { get; set; } = 1;
    public int MotionZoneMinimumMotionCells { get; set; } = 3;
    public int MotionZoneMinimumVisibleAreaCells { get; set; } = 5;
    public int MotionZoneRenderMergeGapCells { get; set; } = 1;
    public int MotionZoneTrackingGapCells { get; set; } = 2;

    public double MotionZoneSceneChangeFraction { get; set; } = 0.12;
    public double MotionZoneSceneChangeOverlapFraction { get; set; } = 0.35;
    public int MotionZoneSceneSettleMilliseconds { get; set; } = 80;

    public int MotionZoneOneShotHoldMilliseconds { get; set; } = 3_000;
    public int MotionZoneRecurringWindowMilliseconds { get; set; } = 5_000;
    public int MotionZoneRecurringMinimumSpanMilliseconds { get; set; } = 180;
    public int MotionZoneRecurringHits { get; set; } = 3;
    public int MotionZoneRecurringHoldMilliseconds { get; set; } = 30_000;

    public int MotionZoneDimDurationMilliseconds { get; set; } = 1_200;
    public int MotionZoneDimSteps { get; set; } = 6;

    public bool MouseVisualEnabled { get; set; } = true;
    public int MouseVisualRadiusPixels { get; set; } = 16;
    public int MouseTrailMilliseconds { get; set; } = 70;
    public int MouseTrailSpacingPixels { get; set; } = 7;

    public AppSettings Clone() =>
        (AppSettings)MemberwiseClone();

    public void Migrate()
    {
        if (SchemaVersion < 41)
        {
            // Reset only the engine values that were affected by the
            // experimental 4.6.6-4.6.8 builds. Personal choices such as
            // enabled state, opacity and Windows startup remain untouched.
            MotionZoneCaptureWidth = 1280;
            MotionZoneSamplesPerCell = 4;
            MotionZoneSamplingMilliseconds = 20;
            MotionZonePixelThreshold = 10;
            MotionZoneChangedFraction = 0.08;

            MotionZonePaddingCells = 1;
            MotionZoneMinimumMotionCells = 3;
            MotionZoneMinimumVisibleAreaCells = 5;
            MotionZoneRenderMergeGapCells = 1;
            MotionZoneTrackingGapCells = 2;

            MotionZoneSceneChangeFraction = 0.12;
            MotionZoneSceneChangeOverlapFraction = 0.35;
            MotionZoneSceneSettleMilliseconds = 80;

            MotionZoneOneShotHoldMilliseconds = 3_000;
            MotionZoneRecurringWindowMilliseconds = 5_000;
            MotionZoneRecurringMinimumSpanMilliseconds = 180;
            MotionZoneRecurringHits = 3;
            MotionZoneRecurringHoldMilliseconds = 30_000;

            MotionZoneDimDurationMilliseconds = 1_200;
            MotionZoneDimSteps = 6;

            MouseVisualEnabled = true;
            MouseVisualRadiusPixels = 16;
            MouseTrailMilliseconds = 70;
            MouseTrailSpacingPixels = 7;
        }

        SchemaVersion = CurrentSchemaVersion;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;

        MaximumMaskOpacity = Math.Clamp(
            MaximumMaskOpacity,
            0.25,
            0.95);

        MotionZoneCaptureWidth = Math.Clamp(
            MotionZoneCaptureWidth,
            640,
            1920);
        MotionZoneSamplesPerCell = Math.Clamp(
            MotionZoneSamplesPerCell,
            2,
            8);
        MotionZoneSamplingMilliseconds = Math.Clamp(
            MotionZoneSamplingMilliseconds,
            16,
            100);
        MotionZonePixelThreshold = Math.Clamp(
            MotionZonePixelThreshold,
            4,
            40);
        MotionZoneChangedFraction = Math.Clamp(
            MotionZoneChangedFraction,
            0.02,
            0.50);

        MotionZonePaddingCells = Math.Clamp(
            MotionZonePaddingCells,
            0,
            3);
        MotionZoneMinimumMotionCells = Math.Clamp(
            MotionZoneMinimumMotionCells,
            2,
            30);
        MotionZoneMinimumVisibleAreaCells = Math.Clamp(
            MotionZoneMinimumVisibleAreaCells,
            3,
            100);
        MotionZoneRenderMergeGapCells = Math.Clamp(
            MotionZoneRenderMergeGapCells,
            0,
            3);
        MotionZoneTrackingGapCells = Math.Clamp(
            MotionZoneTrackingGapCells,
            0,
            6);

        MotionZoneSceneChangeFraction = Math.Clamp(
            MotionZoneSceneChangeFraction,
            0.05,
            0.40);
        MotionZoneSceneChangeOverlapFraction = Math.Clamp(
            MotionZoneSceneChangeOverlapFraction,
            0.10,
            0.90);
        MotionZoneSceneSettleMilliseconds = Math.Clamp(
            MotionZoneSceneSettleMilliseconds,
            40,
            500);

        MotionZoneOneShotHoldMilliseconds = Math.Clamp(
            MotionZoneOneShotHoldMilliseconds,
            500,
            15_000);
        MotionZoneRecurringWindowMilliseconds = Math.Clamp(
            MotionZoneRecurringWindowMilliseconds,
            500,
            15_000);
        MotionZoneRecurringMinimumSpanMilliseconds = Math.Clamp(
            MotionZoneRecurringMinimumSpanMilliseconds,
            60,
            2_000);
        MotionZoneRecurringHits = Math.Clamp(
            MotionZoneRecurringHits,
            2,
            12);
        MotionZoneRecurringHoldMilliseconds = Math.Clamp(
            MotionZoneRecurringHoldMilliseconds,
            2_000,
            120_000);

        MotionZoneDimDurationMilliseconds = Math.Clamp(
            MotionZoneDimDurationMilliseconds,
            0,
            10_000);
        MotionZoneDimSteps = Math.Clamp(
            MotionZoneDimSteps,
            2,
            12);

        MouseVisualRadiusPixels = Math.Clamp(
            MouseVisualRadiusPixels,
            8,
            48);
        MouseTrailMilliseconds = Math.Clamp(
            MouseTrailMilliseconds,
            0,
            250);
        MouseTrailSpacingPixels = Math.Clamp(
            MouseTrailSpacingPixels,
            3,
            24);
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "OledGuard");

    public string SettingsPath =>
        Path.Combine(
            SettingsDirectory,
            "settings.json");

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
                var json =
                    File.ReadAllText(SettingsPath);
                settings =
                    JsonSerializer.Deserialize<AppSettings>(
                        json,
                        JsonOptions) ??
                    new AppSettings();
            }

            var previousSchema =
                settings.SchemaVersion;

            settings.Migrate();
            settings.Normalize();

            if (previousSchema != settings.SchemaVersion ||
                !File.Exists(SettingsPath))
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            var settings =
                new AppSettings();

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
            Directory.CreateDirectory(
                SettingsDirectory);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(
                    settings,
                    JsonOptions));
        }
        catch
        {
            // A settings failure must never leave a dark overlay stuck.
        }
    }
}
