using System.IO;
using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 6;

    public int SchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;

    // A cell that changes remains visible for this long before it may darken.
    public int StaticDelaySeconds { get; set; } = 30;

    // Small square grid: close to the original look, but finer than the first MVP.
    public int CellSizePixels { get; set; } = 48;
    public int SamplesPerCell { get; set; } = 4;
    public int VisibleSamplingMilliseconds { get; set; } = 500;
    public int MaskedSamplingMilliseconds { get; set; } = 250;

    // Temporal fades only. There is intentionally no spatial blur.
    public int DarkenFadeMilliseconds { get; set; } = 1200;
    public int RevealFadeMilliseconds { get; set; } = 120;

    // Mouse cells are collected into one stroke. When the stroke ends, every
    // touched square receives the same expiry time and therefore fades together.
    public int MouseRevealRadiusPixels { get; set; } = 120;
    public int MouseRevealHoldMilliseconds { get; set; } = 30_000;
    public int MouseStrokeIdleMilliseconds { get; set; } = 450;

    // Content activity reveals only the changed square. A tiny hole-closing pass
    // fills isolated gaps between active squares without creating large halos.
    public int ContentRevealHoldMilliseconds { get; set; } = 30_000;
    public int HoleFillNeighbourCount { get; set; } = 3;

    public double DifferenceThreshold { get; set; } = 3.0;
    public double ChangedSampleFraction { get; set; } = 0.04;
    public byte MinimumLuminanceToMask { get; set; } = 3;

    public bool StartWithWindows { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

        // v6 deliberately returns to the original square-pixel visual model.
        // It keeps only the useful later improvement: a mouse stroke expires as
        // one group, so the path darkens uniformly rather than as a trailing wave.
        StaticDelaySeconds = 30;
        CellSizePixels = 48;
        SamplesPerCell = 4;
        VisibleSamplingMilliseconds = 500;
        MaskedSamplingMilliseconds = 250;
        DarkenFadeMilliseconds = 1200;
        RevealFadeMilliseconds = 120;
        MouseRevealRadiusPixels = 120;
        MouseRevealHoldMilliseconds = 30_000;
        MouseStrokeIdleMilliseconds = 450;
        ContentRevealHoldMilliseconds = 30_000;
        HoleFillNeighbourCount = 3;
        DifferenceThreshold = 3.0;
        ChangedSampleFraction = 0.04;
        MinimumLuminanceToMask = 3;
        SchemaVersion = CurrentSchemaVersion;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        StaticDelaySeconds = Math.Clamp(StaticDelaySeconds, 5, 600);
        CellSizePixels = Math.Clamp(CellSizePixels, 24, 96);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 8);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 200, 10_000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 100, 5_000);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 200, 10_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 2_000);
        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 24, 400);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 120_000);
        MouseStrokeIdleMilliseconds = Math.Clamp(MouseStrokeIdleMilliseconds, 150, 2_000);
        ContentRevealHoldMilliseconds = Math.Clamp(ContentRevealHoldMilliseconds, 0, 120_000);
        HoleFillNeighbourCount = Math.Clamp(HoleFillNeighbourCount, 2, 8);
        DifferenceThreshold = Math.Clamp(DifferenceThreshold, 0.5, 50.0);
        ChangedSampleFraction = Math.Clamp(ChangedSampleFraction, 0.01, 1.0);
        MinimumLuminanceToMask = (byte)Math.Clamp(MinimumLuminanceToMask, (byte)0, (byte)40);
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
