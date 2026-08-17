using System.Text.Json;

namespace OrandOverlay;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(AppSettings settings) =>
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Options));
}
