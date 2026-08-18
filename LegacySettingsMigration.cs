using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrandOverlay;

/// <summary>
/// settingsSchemaVersion 기반 1회 레거시 설정 마이그레이션.
/// v1→v2: 현재 AppSettings 스키마에 없는 키를 모두 버린다(스키마 주도 — 레거시 키 이름을 코드에 남기지 않음).
/// 인식 경로는 메모리 단일 경로로 고정되므로 구 인식 관련 키는 이 규칙에 따라 자연 제거된다.
/// </summary>
public static class LegacySettingsMigration
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 레거시 설정 JSON을 현재 스키마로 변환한다.
    /// 변경이 없으면 Changed=false; 변경 있으면 Changed=true이고 Json에 정제된 JSON이 담긴다.
    /// </summary>
    public static (string Json, bool Changed) Run(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return (json, false);
            if (GetSchemaVersion(obj) >= CurrentSchemaVersion) return (json, false);

            var knownKeys = new HashSet<string>(
                typeof(AppSettings).GetProperties().Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                if (!knownKeys.Contains(key))
                    obj.Remove(key);
            }

            BumpSchemaVersion(obj);

            return (obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), true);
        }
        catch
        {
            return (json, false);
        }
    }

    private static int GetSchemaVersion(JsonObject obj) =>
        (obj["SettingsSchemaVersion"] ?? obj["settingsSchemaVersion"])?.GetValue<int>() ?? 1;

    private static void BumpSchemaVersion(JsonObject obj)
    {
        obj.Remove("settingsSchemaVersion");
        obj["SettingsSchemaVersion"] = CurrentSchemaVersion;
    }
}
