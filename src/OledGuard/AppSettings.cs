using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 44;

    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; }

    public double MaximumMaskOpacity { get; set; } = 0.85;

    public int MotionZoneCaptureWidth { get; set; } = 1280;
    public int MotionZoneSamplesPerCell { get; set; } = 4;
    public int MotionZoneSamplingMilliseconds { get; set; } = 20;
    public int MotionZonePixelThreshold { get; set; } = 8;
    public double MotionZoneChangedFraction { get; set; } = 0.08;

    public int MotionZonePaddingCells { get; set; } = 1;
    public int MotionZoneMinimumMotionCells { get; set; } = 2;
    public int MotionZoneMinimumVisibleAreaCells { get; set; } = 4;
    public int MotionZoneMinimumOutputAreaPixels { get; set; } = 90;
    public int MotionZoneMinimumOutputDimensionPixels { get; set; } = 5;
    public int MotionZoneRenderMergeGapCells { get; set; } = 1;
    public int MotionZoneRenderJoinGapPixels { get; set; } = 18;
    public int MotionZoneTrackingGapCells { get; set; } = 2;

    public double MotionZoneSceneChangeFraction { get; set; } = 0.12;
    public double MotionZoneSceneChangeOverlapFraction { get; set; } = 0.35;
    public int MotionZoneSceneSettleMilliseconds { get; set; } = 60;

    public int MotionZoneOneShotHoldMilliseconds { get; set; } = 500;
    public int MotionZoneTransientFadeMilliseconds { get; set; } = 300;
    public int MotionZoneRecurringWindowMilliseconds { get; set; } = 5_000;
    public int MotionZoneRecurringMinimumSpanMilliseconds { get; set; } = 600;
    public int MotionZoneRecurringHits { get; set; } = 4;
    public int MotionZoneRecurringHoldMilliseconds { get; set; } = 30_000;

    public int MotionZoneDimDurationMilliseconds { get; set; } = 1_200;
    public int MotionZoneDimSteps { get; set; } = 6;

    public int ForegroundWindowRevealMilliseconds { get; set; } = 1_500;
    public int ForegroundWindowFadeMilliseconds { get; set; } = 500;

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

        if (SchemaVersion < 42)
        {
            MotionZoneCaptureWidth = 1280;
            MotionZoneSamplesPerCell = 4;
            MotionZoneSamplingMilliseconds = 20;
            MotionZonePixelThreshold = 8;
            MotionZoneChangedFraction = 0.08;

            MotionZonePaddingCells = 1;
            MotionZoneMinimumMotionCells = 2;
            MotionZoneMinimumVisibleAreaCells = 4;
            MotionZoneMinimumOutputAreaPixels = 90;
            MotionZoneMinimumOutputDimensionPixels = 5;
            MotionZoneRenderMergeGapCells = 1;
            MotionZoneRenderJoinGapPixels = 18;
            MotionZoneTrackingGapCells = 2;

            MotionZoneSceneChangeFraction = 0.12;
            MotionZoneSceneChangeOverlapFraction = 0.35;
            MotionZoneSceneSettleMilliseconds = 60;

            MotionZoneOneShotHoldMilliseconds = 500;
            MotionZoneTransientFadeMilliseconds = 300;
            MotionZoneRecurringWindowMilliseconds = 5_000;
            MotionZoneRecurringMinimumSpanMilliseconds = 600;
            MotionZoneRecurringHits = 4;
            MotionZoneRecurringHoldMilliseconds = 30_000;

            MotionZoneDimDurationMilliseconds = 1_200;
            MotionZoneDimSteps = 6;

            ForegroundWindowRevealMilliseconds = 1_500;
            ForegroundWindowFadeMilliseconds = 500;
        }

        // 4.8.0 forced several detection values upward. Restore only exact
        // forced values, preserving unrelated choices and larger custom values.
        if (SchemaVersion == 43)
        {
            if (MotionZonePixelThreshold == 10)
            {
                MotionZonePixelThreshold = 8;
            }

            if (MotionZoneMinimumMotionCells == 3)
            {
                MotionZoneMinimumMotionCells = 2;
            }

            if (MotionZoneRenderJoinGapPixels == 20)
            {
                MotionZoneRenderJoinGapPixels = 18;
            }

            if (MotionZoneRecurringMinimumSpanMilliseconds == 900)
            {
                MotionZoneRecurringMinimumSpanMilliseconds = 600;
            }

            if (MotionZoneRecurringHits == 6)
            {
                MotionZoneRecurringHits = 4;
            }

            if (MotionZoneDimSteps == 8)
            {
                MotionZoneDimSteps = 6;
            }
        }

        SchemaVersion = CurrentSchemaVersion;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;

        MaximumMaskOpacity = Math.Clamp(MaximumMaskOpacity, 0.25, 0.95);

        MotionZoneCaptureWidth = Math.Clamp(MotionZoneCaptureWidth, 640, 1920);
        MotionZoneSamplesPerCell = Math.Clamp(MotionZoneSamplesPerCell, 2, 8);
        MotionZoneSamplingMilliseconds = Math.Clamp(MotionZoneSamplingMilliseconds, 16, 100);
        MotionZonePixelThreshold = Math.Clamp(MotionZonePixelThreshold, 4, 40);
        MotionZoneChangedFraction = Math.Clamp(MotionZoneChangedFraction, 0.02, 0.50);

        MotionZonePaddingCells = Math.Clamp(MotionZonePaddingCells, 0, 3);
        MotionZoneMinimumMotionCells = Math.Clamp(MotionZoneMinimumMotionCells, 1, 30);
        MotionZoneMinimumVisibleAreaCells = Math.Clamp(MotionZoneMinimumVisibleAreaCells, 2, 100);
        MotionZoneMinimumOutputAreaPixels = Math.Clamp(MotionZoneMinimumOutputAreaPixels, 24, 2_000);
        MotionZoneMinimumOutputDimensionPixels = Math.Clamp(MotionZoneMinimumOutputDimensionPixels, 2, 40);
        MotionZoneRenderMergeGapCells = Math.Clamp(MotionZoneRenderMergeGapCells, 0, 3);
        MotionZoneRenderJoinGapPixels = Math.Clamp(MotionZoneRenderJoinGapPixels, 0, 60);
        MotionZoneTrackingGapCells = Math.Clamp(MotionZoneTrackingGapCells, 0, 6);

        MotionZoneSceneChangeFraction = Math.Clamp(MotionZoneSceneChangeFraction, 0.05, 0.40);
        MotionZoneSceneChangeOverlapFraction = Math.Clamp(MotionZoneSceneChangeOverlapFraction, 0.10, 0.90);
        MotionZoneSceneSettleMilliseconds = Math.Clamp(MotionZoneSceneSettleMilliseconds, 20, 500);

        MotionZoneOneShotHoldMilliseconds = Math.Clamp(MotionZoneOneShotHoldMilliseconds, 150, 5_000);
        MotionZoneTransientFadeMilliseconds = Math.Clamp(MotionZoneTransientFadeMilliseconds, 80, 2_000);
        MotionZoneRecurringWindowMilliseconds = Math.Clamp(MotionZoneRecurringWindowMilliseconds, 500, 15_000);
        MotionZoneRecurringMinimumSpanMilliseconds = Math.Clamp(MotionZoneRecurringMinimumSpanMilliseconds, 100, 3_000);
        MotionZoneRecurringHits = Math.Clamp(MotionZoneRecurringHits, 2, 12);
        MotionZoneRecurringHoldMilliseconds = Math.Clamp(MotionZoneRecurringHoldMilliseconds, 2_000, 120_000);

        MotionZoneDimDurationMilliseconds = Math.Clamp(MotionZoneDimDurationMilliseconds, 0, 10_000);
        MotionZoneDimSteps = Math.Clamp(MotionZoneDimSteps, 2, 12);

        ForegroundWindowRevealMilliseconds = Math.Clamp(ForegroundWindowRevealMilliseconds, 400, 5_000);
        ForegroundWindowFadeMilliseconds = Math.Clamp(ForegroundWindowFadeMilliseconds, 100, 2_000);

        MouseVisualRadiusPixels = Math.Clamp(MouseVisualRadiusPixels, 8, 48);
        MouseTrailMilliseconds = Math.Clamp(MouseTrailMilliseconds, 0, 250);
        MouseTrailSpacingPixels = Math.Clamp(MouseTrailSpacingPixels, 3, 24);
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
                settings =
                    JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ??
                    new AppSettings();
            }

            var previousSchema = settings.SchemaVersion;
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
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // A settings failure must never leave a dark overlay stuck.
        }
    }
}
