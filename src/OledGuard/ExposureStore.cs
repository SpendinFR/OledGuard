using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OledGuard;

internal static class ExposureStore
{
    private const int Magic = 0x5845474F; // OGEX
    private const int FormatVersion = 1;

    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OledGuard",
        "exposure");

    public static float[] Load(string identity, int columns, int rows, float maximumExposureSeconds)
    {
        var result = new float[checked(columns * rows)];

        try
        {
            var path = GetPath(identity);
            if (!File.Exists(path))
            {
                return result;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
            {
                return result;
            }

            var storedColumns = reader.ReadInt32();
            var storedRows = reader.ReadInt32();
            var count = reader.ReadInt32();
            _ = reader.ReadInt64(); // UTC save timestamp, reserved for future migrations.

            if (storedColumns != columns || storedRows != rows || count != result.Length)
            {
                return result;
            }

            for (var index = 0; index < result.Length; index++)
            {
                var value = reader.ReadSingle();
                result[index] = float.IsFinite(value)
                    ? Math.Clamp(value, 0f, maximumExposureSeconds)
                    : 0f;
            }
        }
        catch
        {
            Array.Clear(result, 0, result.Length);
        }

        return result;
    }

    public static void Save(string identity, int columns, int rows, ReadOnlySpan<float> exposure)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = GetPath(identity);
            var temporaryPath = path + ".tmp";

            using (var stream = File.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(columns);
                writer.Write(rows);
                writer.Write(exposure.Length);
                writer.Write(DateTime.UtcNow.Ticks);

                foreach (var value in exposure)
                {
                    writer.Write(float.IsFinite(value) ? Math.Max(0f, value) : 0f);
                }
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // Protection continues in memory if persistence is temporarily unavailable.
        }
    }

    private static string GetPath(string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var name = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(DirectoryPath, name + ".bin");
    }
}
