using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 10;

    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // A meaningful content region stays visible for this long after its last change.
    public int StaticDelaySeconds { get; set; } = 30;

    // A newly focused or moved foreground window is briefly revealed so the user
    // immediately understands where the current working area is.
    public int WindowRevealSeconds { get; set; } = 5;

    // Low-resolution analysis grid. No full-resolution desktop frame is retained.
    public int CellSizePixels { get; set; } = 24;
    public int SamplesPerCell { get; set; } = 3;
    public int VisibleSamplingMilliseconds { get; set; } = 900;
    public int MaskedSamplingMilliseconds { get; set; } = 250;

    // Temporal fades.
    public int DarkenFadeMilliseconds { get; set; } = 2600;
    public int RevealFadeMilliseconds { get; set; } = 100;

    // Crisp square spatial gradient around active content.
    public int ContentCoreCells { get; set; } = 1;
    public int ContentFeatherCells { get; set; } = 5;
    public int GradientSteps { get; set; } = 7;
    public int ContentActivationPaddingCells { get; set; } = 1;

    // Ignore tiny isolated animations such as one blinking indicator.
    public int MinimumActivityComponentCells { get; set; } = 2;

    // Mouse discovery is independent of the foreground window and can reveal any
    // black area without moving focus.
    public int MouseCoreRadiusPixels { get; set; } = 48;
    public int MouseFeatherCells { get; set; } = 4;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;

    // Change detector thresholds.
    public double DifferenceThreshold { get; set; } = 3.0;
    public double ChangedSampleFraction { get; set; } = 0.10;
    public double StrongDifferenceThreshold { get; set; } = 9.0;
    public double StrongChangedSampleFraction { get; set; } = 0.24;
    public int WeakChangeConfirmationSamples { get; set; } = 2;

    // A moving black rest band periodically gives even continuously changing
    // pixels a short dark pause. It only increases black opacity; it never emits
    // extra colour or brightness.
    public bool RestCycleEnabled { get; set; } = true;
    public int RestCycleIntervalSeconds { get; set; } = 120;
    public int RestCycleDurationMilliseconds { get; set; } = 7000;
    public int RestCycleBandCells { get; set; } = 6;
    public double RestCycleStrength { get; set; } = 0.92;

    public bool StartWithWindows { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

        // V1 resets experimental visual parameters while preserving the enabled
        // state and startup preference. This avoids carrying prototype halos and
        // rectangle modes into the stable engine.
        StaticDelaySeconds = 30;
        WindowRevealSeconds = 5;
        CellSizePixels = 24;
        SamplesPerCell = 3;
        VisibleSamplingMilliseconds = 900;
        MaskedSamplingMilliseconds = 250;
        DarkenFadeMilliseconds = 2600;
        RevealFadeMilliseconds = 100;
        ContentCoreCells = 1;
        ContentFeatherCells = 5;
        GradientSteps = 7;
        ContentActivationPaddingCells = 1;
        MinimumActivityComponentCells = 2;
        MouseCoreRadiusPixels = 48;
        MouseFeatherCells = 4;
        MouseRevealHoldMilliseconds = 30_000;
        DifferenceThreshold = 3.0;
        ChangedSampleFraction = 0.10;
        StrongDifferenceThreshold = 9.0;
        StrongChangedSampleFraction = 0.24;
        WeakChangeConfirmationSamples = 2;
        RestCycleEnabled = true;
        RestCycleIntervalSeconds = 120;
        RestCycleDurationMilliseconds = 7000;
        RestCycleBandCells = 6;
        RestCycleStrength = 0.92;
        SchemaVersion = CurrentSchemaVersion;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        StaticDelaySeconds = Math.Clamp(StaticDelaySeconds, 5, 600);
        WindowRevealSeconds = Math.Clamp(WindowRevealSeconds, 1, 30);
        CellSizePixels = Math.Clamp(CellSizePixels, 16, 64);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 5);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 250, 5000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 100, 2000);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 300, 10_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 1000);
        ContentCoreCells = Math.Clamp(ContentCoreCells, 0, 4);
        ContentFeatherCells = Math.Clamp(ContentFeatherCells, 1, 12);
        GradientSteps = Math.Clamp(GradientSteps, 3, 16);
        ContentActivationPaddingCells = Math.Clamp(ContentActivationPaddingCells, 0, 4);
        MinimumActivityComponentCells = Math.Clamp(MinimumActivityComponentCells, 1, 12);
        MouseCoreRadiusPixels = Math.Clamp(MouseCoreRadiusPixels, 16, 240);
        MouseFeatherCells = Math.Clamp(MouseFeatherCells, 1, 12);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
        DifferenceThreshold = Math.Clamp(DifferenceThreshold, 0.5, 50.0);
        ChangedSampleFraction = Math.Clamp(ChangedSampleFraction, 0.01, 1.0);
        StrongDifferenceThreshold = Math.Clamp(StrongDifferenceThreshold, DifferenceThreshold, 100.0);
        StrongChangedSampleFraction = Math.Clamp(StrongChangedSampleFraction, ChangedSampleFraction, 1.0);
        WeakChangeConfirmationSamples = Math.Clamp(WeakChangeConfirmationSamples, 1, 5);
        RestCycleIntervalSeconds = Math.Clamp(RestCycleIntervalSeconds, 20, 900);
        RestCycleDurationMilliseconds = Math.Clamp(RestCycleDurationMilliseconds, 1500, 20_000);
        RestCycleBandCells = Math.Clamp(RestCycleBandCells, 2, 20);
        RestCycleStrength = Math.Clamp(RestCycleStrength, 0.10, 1.0);
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
