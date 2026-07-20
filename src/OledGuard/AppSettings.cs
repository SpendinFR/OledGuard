using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 31;

    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // Analysis grid. A 64 px grid is about 60 x 34 cells on a 4K display.
    public int DetectionCellSizePixels { get; set; } = 64;
    public int SamplesPerCell { get; set; } = 4;
    public int VisibleSamplingMilliseconds { get; set; } = 1000;
    public int MaskedSamplingMilliseconds { get; set; } = 500;

    // A cell is accepted as static only when it agrees with references from
    // several time scales and has accumulated enough stable confirmations.
    public int ShortReferenceSeconds { get; set; } = 2;
    public int MediumReferenceSeconds { get; set; } = 15;
    public int LongReferenceSeconds { get; set; } = 60;
    public int StableConfirmationSamples { get; set; } = 3;
    public double DifferenceThreshold { get; set; } = 4.0;
    public double ChangedSampleFraction { get; set; } = 0.08;

    // Time-based protection.
    public int StaticDelaySeconds { get; set; } = 120;
    public int DarkenFadeMilliseconds { get; set; } = 20_000;
    public int RevealFadeMilliseconds { get; set; } = 150;
    public double MaximumMaskOpacity { get; set; } = 0.85;

    // Dark content already emits little light. The threshold is evaluated on
    // the average luminance of an entire cleaned region, not cell by cell.
    public byte MinimumLuminanceToDim { get; set; } = 12;

    // Bidirectional cleanup: majority passes remove isolated dim islands and
    // isolated bright holes. Component limits finish the cleanup.
    public int MajorityFilterPasses { get; set; } = 2;
    public int MajorityDimThreshold { get; set; } = 6;
    public int MinimumDimRegionCells { get; set; } = 4;
    public int MaximumBrightHoleCells { get; set; } = 3;

    // Mouse engine copied from build 3 (v0.5.0 / e1ddf1ed).
    public int MouseRevealRadiusPixels { get; set; } = 40;
    public int MouseFeatherRadiusPixels { get; set; } = 72;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;
    public int MouseStrokeIdleMilliseconds { get; set; } = 650;
    public int MouseHoverRadiusPixels { get; set; } = 8;
    public int MouseHoverRefreshMilliseconds { get; set; } = 500;
    public int MouseRevealFadeMilliseconds { get; set; } = 140;
    public int MouseReturnFadeMilliseconds { get; set; } = 5_000;

    public bool StartWithWindows { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

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
            StaticDelaySeconds = 120;
            DarkenFadeMilliseconds = 20_000;
            RevealFadeMilliseconds = 150;
            MaximumMaskOpacity = 0.85;
            MinimumLuminanceToDim = 12;
            MajorityFilterPasses = 2;
            MajorityDimThreshold = 6;
            MinimumDimRegionCells = 4;
            MaximumBrightHoleCells = 3;
        }

        MouseRevealRadiusPixels = 40;
        MouseFeatherRadiusPixels = 72;
        MouseRevealHoldMilliseconds = 30_000;
        MouseStrokeIdleMilliseconds = 650;
        MouseHoverRadiusPixels = 8;
        MouseHoverRefreshMilliseconds = 500;
        MouseRevealFadeMilliseconds = 140;
        MouseReturnFadeMilliseconds = 5_000;
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

        StaticDelaySeconds = Math.Clamp(StaticDelaySeconds, 5, 3600);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 500, 120_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 5_000);
        MaximumMaskOpacity = Math.Clamp(MaximumMaskOpacity, 0.25, 0.98);
        MinimumLuminanceToDim = (byte)Math.Clamp(MinimumLuminanceToDim, (byte)0, (byte)100);

        MajorityFilterPasses = Math.Clamp(MajorityFilterPasses, 0, 5);
        MajorityDimThreshold = Math.Clamp(MajorityDimThreshold, 5, 8);
        MinimumDimRegionCells = Math.Clamp(MinimumDimRegionCells, 1, 100);
        MaximumBrightHoleCells = Math.Clamp(MaximumBrightHoleCells, 0, 100);

        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 0, 200);
        MouseFeatherRadiusPixels = Math.Clamp(MouseFeatherRadiusPixels, 1, 240);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
        MouseStrokeIdleMilliseconds = Math.Clamp(MouseStrokeIdleMilliseconds, 150, 3_000);
        MouseHoverRadiusPixels = Math.Clamp(MouseHoverRadiusPixels, 0, 96);
        MouseHoverRefreshMilliseconds = Math.Clamp(MouseHoverRefreshMilliseconds, 100, 2_000);
        MouseRevealFadeMilliseconds = Math.Clamp(MouseRevealFadeMilliseconds, 40, 2_000);
        MouseReturnFadeMilliseconds = Math.Clamp(MouseReturnFadeMilliseconds, 500, 15_000);
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
