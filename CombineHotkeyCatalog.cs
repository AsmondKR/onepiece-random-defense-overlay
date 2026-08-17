using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrandOverlay;

/// <summary>
/// 맵(2.314)에서 추출한 조합 스킬 단축키 카탈로그.
/// 각 항목은 결과 유닛 rawcode 기준이다: 그 유닛을 만드는 조합 스킬의 키(Z/X/C/B)와
/// 맵 기록의 재료 목록. 트리거(먼저 선택) 재료는 앱의 조합 트리가 이미 알고 있으므로
/// 여기서는 "누를 키"만 답한다.
/// </summary>
public sealed class CombineHotkeyCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyDictionary<string, CombineHotkeyEntry> _byResultRawcode;
    private readonly IReadOnlyDictionary<string, CombineHotkeyEntry> _byResultName;

    public IReadOnlyList<CombineHotkeyEntry> Entries { get; }
    public string MapVersion { get; }

    private CombineHotkeyCatalog(IReadOnlyList<CombineHotkeyEntry> entries, string mapVersion)
    {
        Entries = entries;
        MapVersion = mapVersion;
        _byResultRawcode = entries
            .GroupBy(entry => entry.ResultRawcode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _byResultName = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ResultName))
            .GroupBy(entry => NormalizeName(entry.ResultName!), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public static CombineHotkeyCatalog Empty { get; } = new([], "");

    public bool HasData => Entries.Count > 0;

    public static CombineHotkeyCatalog Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.SchemaVersion != 1) return Empty;
            var entries = document.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Result) &&
                                !string.IsNullOrWhiteSpace(entry.Key))
                .Select(entry => new CombineHotkeyEntry(entry.Result!, entry.Key![..1],
                    entry.ResultName, entry.Ingredients ?? []))
                .ToList();
            return new CombineHotkeyCatalog(entries, document.MapVersion ?? "");
        }
        catch
        {
            // 단축키 카탈로그가 없거나 손상돼도 추천 기능은 계속 동작해야 한다.
            return Empty;
        }
    }

    /// <summary>결과 유닛 rawcode들로 조합 키를 찾는다.</summary>
    public CombineHotkeyEntry? FindByResult(IEnumerable<string> resultRawcodes) =>
        resultRawcodes
            .Select(rawcode => _byResultRawcode.GetValueOrDefault(rawcode))
            .FirstOrDefault(entry => entry is not null);

    /// <summary>rawcode 매칭이 안 될 때의 이름 폴백("킹 - 전설" 형식).</summary>
    public CombineHotkeyEntry? FindByResultName(string resultName) =>
        _byResultName.GetValueOrDefault(NormalizeName(resultName));

    /// <summary>"킹 - 전설 [물딜]" → "킹|전설" 로 정규화해 표기 차이를 흡수한다.</summary>
    internal static string NormalizeName(string value)
    {
        var text = value.Trim();
        var parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
        var name = parts[0].Replace(" ", "", StringComparison.Ordinal);
        if (parts.Length == 1) return name;
        var tier = parts[1].Split('[', 2)[0].Trim().Replace(" ", "", StringComparison.Ordinal);
        return string.IsNullOrEmpty(tier) ? name : name + "|" + tier;
    }

    private sealed class Document
    {
        public int SchemaVersion { get; set; }
        public string? MapVersion { get; set; }
        public List<DocumentEntry> Entries { get; set; } = [];
    }

    private sealed class DocumentEntry
    {
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("ability")] public string? Ability { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("resultName")] public string? ResultName { get; set; }
        [JsonPropertyName("ingredients")] public List<string>? Ingredients { get; set; }
    }
}

public sealed record CombineHotkeyEntry(
    string ResultRawcode,
    string Key,
    string? ResultName,
    IReadOnlyList<string> Ingredients);
