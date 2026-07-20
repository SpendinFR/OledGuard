using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 32;

    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // Capture blocks and independent sub-zones. Example: 128 px / 8 samples
    // gives about 16 px of effective spatial precision.
    public int DetectionCellSizePixels { get; set; } = 64;
    public int SamplesPerCell { get; set; } = 4;
    public int VisibleSamplingMilliseconds { get; set; } = 1000;
    public int MaskedSamplingMilliseconds { get; set; } = 500;

    // Multi-scale stability detector.
    public int ShortReferenceSeconds { get; set; } = 2;
    public int MediumReferenceSeconds { get; set; } = 15;
    public int LongReferenceSeconds { get; set; } = 60;
    public int StableConfirmationSamples { get; set; } = 3;
    public double DifferenceThreshold { get; set; } = 4.0;
    public double ChangedSampleFraction { get; set; } = 0.08;

    // Cumulative exposure engine. Exposure is expressed as equivalent seconds
    // at full-white luminance. Brief motion reveals the content but does not erase
    // the accumulated debt.
    public int StaticEligibilitySeconds { get; set; } = 30;
    public int ReapplyDelaySeconds { get; set; } = 12;
    public double ExposureStartMinutes { get; set; } = 8.0;
    public double ExposureFullMinutes { get; set; } = 25.0;
    public double MovementExposureDecayRate { get; set; } = 0.20;
    public double UncertainExposureDecayRate { get; set; } = 0.03;
    public int ExposureSaveMinutes { get; set; } = 5;

    public int DarkenFadeMilliseconds { get; set; } = 12_000;
    public int RevealFadeMilliseconds { get; set; } = 1_200;
    public double MaximumMaskOpacity { get; set; } = 0.35;

    // Dark content already emits little light. This threshold is also used when
    // converting luminance to cumulative exposure.
    public byte MinimumLuminanceToDim { get; set; } = 18;

    // Bidirectional cleanup removes isolated dim islands and isolated bright holes.
    // Connected regions share one opacity to avoid internal grid bands.
    public int MajorityFilterPasses { get; set; } = 2;
    public int MajorityDimThreshold { get; set; } = 6;
    public int MinimumDimRegionCells { get; set; } = 4;
    public int MaximumBrightHoleCells { get; set; } = 3;

    public bool StartWithWindows { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

        // Keep the spatial and sensitivity choices introduced by v30/v31.
        if (SchemaVersion < 30)
        {
            DetectionCellSizePixels = 64;
            SamplesPerCell = 4;
            VisibleSamplingMilliseconds = 1000;
            MaskedSamplingMilliseconds = 500;
            ShortReferenceSeconds = 2;
            MediumReferenceSeconds = 15;
            LongReferenceSeconds = 60;
            StableConfirmationSamples = 3;
            DifferenceThreshold = 4.0;
            ChangedSampleFraction = 0.08;
            MinimumLuminanceToDim = 18;
            MajorityFilterPasses = 2;
            MajorityDimThreshold = 6;
            MinimumDimRegionCells = 4;
            MaximumBrightHoleCells = 3;
        }

        if (SchemaVersion < 32)
        {
            StaticEligibilitySeconds = 30;
            ReapplyDelaySeconds = 12;
            ExposureStartMinutes = 8.0;
            ExposureFullMinutes = 25.0;
            MovementExposureDecayRate = 0.20;
            UncertainExposureDecayRate = 0.03;
            ExposureSaveMinutes = 5;
            DarkenFadeMilliseconds = 12_000;
            RevealFadeMilliseconds = 1_200;

            // Previous versions commonly used an 85% black mask. The cumulative
            // engine intentionally migrates to a less intrusive default.
            if (MaximumMaskOpacity <= 0.0 || MaximumMaskOpacity > 0.45)
            {
                MaximumMaskOpacity = 0.35;
            }
        }

        SchemaVersion = CurrentSchemaVersion;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        DetectionCellSizePixels = Math.Clamp(DetectionCellSizePixels, 32, 160);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 8);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 250, 10_000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 100, 5_000);

        ShortReferenceSeconds = Math.Clamp(ShortReferenceSeconds, 1, 15);
        MediumReferenceSeconds = Math.Clamp(MediumReferenceSeconds, ShortReferenceSeconds + 1, 120);
        LongReferenceSeconds = Math.Clamp(LongReferenceSeconds, MediumReferenceSeconds + 1, 600);
        StableConfirmationSamples = Math.Clamp(StableConfirmationSamples, 1, 12);
        DifferenceThreshold = Math.Clamp(DifferenceThreshold, 0.5, 50.0);
        ChangedSampleFraction = Math.Clamp(ChangedSampleFraction, 0.01, 1.0);

        StaticEligibilitySeconds = Math.Clamp(StaticEligibilitySeconds, 5, 600);
        ReapplyDelaySeconds = Math.Clamp(ReapplyDelaySeconds, 1, 120);
        ReapplyDelaySeconds = Math.Min(ReapplyDelaySeconds, StaticEligibilitySeconds);
        ExposureStartMinutes = Math.Clamp(ExposureStartMinutes, 1.0, 120.0);
        ExposureFullMinutes = Math.Clamp(ExposureFullMinutes, ExposureStartMinutes + 1.0, 360.0);
        MovementExposureDecayRate = Math.Clamp(MovementExposureDecayRate, 0.0, 2.0);
        UncertainExposureDecayRate = Math.Clamp(UncertainExposureDecayRate, 0.0, 1.0);
        ExposureSaveMinutes = Math.Clamp(ExposureSaveMinutes, 1, 60);

        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 500, 120_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 100, 10_000);
        MaximumMaskOpacity = Math.Clamp(MaximumMaskOpacity, 0.10, 0.90);
        MinimumLuminanceToDim = (byte)Math.Clamp(MinimumLuminanceToDim, (byte)0, (byte)120);

        MajorityFilterPasses = Math.Clamp(MajorityFilterPasses, 0, 5);
        MajorityDimThreshold = Math.Clamp(MajorityDimThreshold, 5, 8);
        MinimumDimRegionCells = Math.Clamp(MinimumDimRegionCells, 1, 100);
        MaximumBrightHoleCells = Math.Clamp(MaximumBrightHoleCells, 0, 100);
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
