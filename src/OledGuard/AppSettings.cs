using System.Text.Json;

namespace OledGuard;

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public int StaticDelaySeconds { get; set; } = 30;
    public int CellSizePixels { get; set; } = 64;
    public int SamplesPerCell { get; set; } = 4;
    public int VisibleSamplingMilliseconds { get; set; } = 1500;
    public int MaskedSamplingMilliseconds { get; set; } = 500;
    public int DarkenFadeMilliseconds { get; set; } = 900;
    public int RevealFadeMilliseconds { get; set; } = 140;
    public int MouseRevealRadiusPixels { get; set; } = 180;
    public int MouseRevealHoldMilliseconds { get; set; } = 850;
    public int ContentRevealPaddingCells { get; set; } = 1;
    public int ContentRevealHoldMilliseconds { get; set; } = 2500;
    public double DifferenceThreshold { get; set; } = 4.0;
    public double ChangedSampleFraction { get; set; } = 0.06;
    public byte MinimumLuminanceToMask { get; set; } = 5;
    public bool StartWithWindows { get; set; } = false;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        StaticDelaySeconds = Math.Clamp(StaticDelaySeconds, 5, 600);
        CellSizePixels = Math.Clamp(CellSizePixels, 32, 160);
        SamplesPerCell = Math.Clamp(SamplesPerCell, 2, 8);
        VisibleSamplingMilliseconds = Math.Clamp(VisibleSamplingMilliseconds, 500, 10_000);
        MaskedSamplingMilliseconds = Math.Clamp(MaskedSamplingMilliseconds, 250, 5_000);
        DarkenFadeMilliseconds = Math.Clamp(DarkenFadeMilliseconds, 100, 5_000);
        RevealFadeMilliseconds = Math.Clamp(RevealFadeMilliseconds, 40, 2_000);
        MouseRevealRadiusPixels = Math.Clamp(MouseRevealRadiusPixels, 60, 500);
        MouseRevealHoldMilliseconds = Math.Clamp(MouseRevealHoldMilliseconds, 0, 5_000);
        ContentRevealPaddingCells = Math.Clamp(ContentRevealPaddingCells, 0, 3);
        ContentRevealHoldMilliseconds = Math.Clamp(ContentRevealHoldMilliseconds, 0, 10_000);
        DifferenceThreshold = Math.Clamp(DifferenceThreshold, 1.0, 50.0);
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
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
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
