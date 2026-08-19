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

    /// <summary>익명 텔레메트리 ID가 없으면 만들어 저장한다(설치 후 1회).</summary>
    public static AppSettings EnsureTelemetryAnonId(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TelemetryAnonId)) return settings;
        settings.TelemetryAnonId = Guid.NewGuid().ToString();
        try { Save(settings); } catch { /* 다음 저장 때 함께 */ }
        return settings;
    }

    public static void Save(AppSettings settings) =>
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Options));
}
