using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 21;

    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // Coarse grid used for change detection and large rectangular content blocks.
    public int DetectionCellSizePixels { get; set; } = 32;
    public int SamplesPerCell { get; set; } = 3;
    public int VisibleSamplingMilliseconds { get; set; } = 1000;
    public int MaskedSamplingMilliseconds { get; set; } = 250;

    // Fine visual grid used by the original pixel trail and opacity fade.
    public int VisualCellSizePixels { get; set; } = 16;
    public int OpacitySteps { get; set; } = 33;
    public double MaximumMaskOpacity { get; set; } = 0.88;
    public int StaticDelaySeconds { get; set; } = 30;
    public int DarkenFadeMilliseconds { get; set; } = 4200;
    public int RevealFadeMilliseconds { get; set; } = 140;
    public byte MinimumLuminanceToDim { get; set; } = 0;

    // Version 0.5 content grouping: nearby changes become one stable large block.
    public int ContentActivationPaddingCells { get; set; } = 1;
    public int ContentMergeGapCells { get; set; } = 1;
    public int MinimumActivityComponentCells { get; set; } = 2;
    public int MinimumRevealBlockWidthCells { get; set; } = 2;
    public int MinimumRevealBlockHeightCells { get; set; } = 2;
    public int ContentFeatherRadiusPixels { get; set; } = 56;
    public int ContentCornerRoundnessPercent { get; set; } = 18;

    // Original 0.1 mouse behaviour: each cursor position refreshes a circular set
    // of fine cells. Old positions expire independently, creating a dynamic trail.
    public int MouseRevealRadiusPixels { get; set; } = 170;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;
    public bool MouseRevealWhileStationary { get; set; } = true;

    // Change detector.
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

        DetectionCellSizePixels = 32;
        SamplesPerCell = 3;
        VisibleSamplingMilliseconds = 1000;
        MaskedSamplingMilliseconds = 250;
        VisualCellSizePixels = 16;
        OpacitySteps = 33;
        MaximumMaskOpacity = 0.88;
        StaticDelaySeconds = 30;
        DarkenFadeMilliseconds = 4200;
        RevealFadeMilliseconds = 140;
        MinimumLuminanceToDim = 0;
        ContentActivationPaddingCells = 1;
        ContentMergeGapCells = 1;
        MinimumActivityComponentCells = 2;
        MinimumRevealBlockWidthCells = 2;
        MinimumRevealBlockHeightCells = 2;
        ContentFeatherRadiusPixels = 56;
        ContentCornerRoundnessPercent = 18;
        MouseRevealRadiusPixels = 170;
        MouseRevealHoldMilliseconds = 30_000;
        MouseRevealWhileStationary = true;
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
        DetectionCellSizePixels = Math.Clamp(DetectionCellSizePixels, 16, 96);
        VisualCellSizePixels = Math.Clamp(VisualCellSizePixels, 8, 48);
        VisualCellSizePixels = Math.Min(VisualCellSizePixels, DetectionCellSizePixels);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 6);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 250, 10_000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 100, 5_000);
        OpacitySteps = Math.Clamp(OpacitySteps, 4, 64);
        MaximumMaskOpacity = Math.Clamp(MaximumMaskOpacity, 0.35, 1.0);
        StaticDelaySeconds = Math.Clamp(StaticDelaySeconds, 5, 600);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 250, 30_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 3_000);
        MinimumLuminanceToDim = (byte)Math.Clamp(MinimumLuminanceToDim, (byte)0, (byte)80);
        ContentActivationPaddingCells = Math.Clamp(ContentActivationPaddingCells, 0, 5);
        ContentMergeGapCells = Math.Clamp(ContentMergeGapCells, 0, 5);
        MinimumActivityComponentCells = Math.Clamp(MinimumActivityComponentCells, 1, 12);
        MinimumRevealBlockWidthCells = Math.Clamp(MinimumRevealBlockWidthCells, 1, 10);
        MinimumRevealBlockHeightCells = Math.Clamp(MinimumRevealBlockHeightCells, 1, 10);
        ContentFeatherRadiusPixels = Math.Clamp(ContentFeatherRadiusPixels, 0, 240);
        ContentCornerRoundnessPercent = Math.Clamp(ContentCornerRoundnessPercent, 0, 100);
        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 20, 500);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
        DifferenceThreshold = Math.Clamp(DifferenceThreshold, 0.5, 50.0);
        ChangedSampleFraction = Math.Clamp(ChangedSampleFraction, 0.01, 1.0);
        StrongDifferenceThreshold = Math.Clamp(StrongDifferenceThreshold, DifferenceThreshold, 100.0);
        StrongChangedSampleFraction = Math.Clamp(StrongChangedSampleFraction, ChangedSampleFraction, 1.0);
        WeakChangeConfirmationSamples = Math.Clamp(WeakChangeConfirmationSamples, 1, 6);
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
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // A settings failure must never leave a dark overlay stuck on screen.
        }
    }
}
