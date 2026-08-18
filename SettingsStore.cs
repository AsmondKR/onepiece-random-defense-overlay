using System.Text.Json;

namespace OrandOverlay;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return new();
            var raw = File.ReadAllText(AppPaths.SettingsFile);
            var (migrated, changed) = LegacySettingsMigration.Run(raw);
            if (changed) File.WriteAllText(AppPaths.SettingsFile, migrated);
            return JsonSerializer.Deserialize<AppSettings>(migrated, Options) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(AppSettings settings) =>
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Options));
}
