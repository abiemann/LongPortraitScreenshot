using System.Security;
using System.Text.Json;

namespace LongPortraitScreenshot.Configuration;

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public bool CropVerticalScrollIndicator { get; set; } = true;

    public bool TrimEmptyHorizontalSpace { get; set; } = true;

    public string? LastSaveDirectory { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException or
                                          SecurityException or
                                          JsonException or
                                          NotSupportedException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        string directory = SettingsDirectory;
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LongPortraitScreenshot");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException or
                                          SecurityException)
        {
            // A failed cleanup must not hide the original save failure.
        }
    }
}
