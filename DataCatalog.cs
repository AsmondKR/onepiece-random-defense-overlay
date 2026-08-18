using System.Globalization;
using System.Text.Json;

namespace OrandOverlay;

public sealed class DataCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public GameData Data { get; private set; } = new();
    public IReadOnlyDictionary<string, UnitDefinition> UnitsById { get; private set; }
        = new Dictionary<string, UnitDefinition>();
    public IReadOnlyDictionary<string, RawcodeCatalogEntry> RawcodeCatalog { get; private set; }
        = new Dictionary<string, RawcodeCatalogEntry>();
    public IReadOnlyList<UnitDefinition> AllUnits { get; private set; } = [];
    private IReadOnlyDictionary<string, string> _unitIdsByRawcode =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public void Load()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Data", "game-data.demo.json");
        var overridePath = Path.Combine(AppPaths.UserDataDirectory, "game-data.json");
        var selected = File.Exists(overridePath) ? overridePath : bundled;
        var json = File.ReadAllText(selected);
        Data = JsonSerializer.Deserialize<GameData>(json, JsonOptions)
            ?? throw new InvalidDataException("게임 데이터를 읽을 수 없습니다.");
        if (Data.SchemaVersion != 1)
            throw new InvalidDataException($"지원하지 않는 데이터 스키마: {Data.SchemaVersion}");
        RawcodeCatalog = WithAliasKeys(ApplyGuideOverrides(LoadRawcodeCatalog()));
        _unitIdsByRawcode = Data.Units
            .SelectMany(unit => unit.Rawcodes.Select(rawcode => (rawcode, unit.Id)))
            .GroupBy(x => x.rawcode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
        UnitsById = Data.Units
            .Select(EnrichKnownUnit)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        AllUnits = UnitsById.Values
            .Concat(RawcodeCatalog.Keys.Select(rawcode =>
                Unit(_unitIdsByRawcode.GetValueOrDefault(rawcode, "rawcode:" + rawcode))))
            .GroupBy(unit => unit.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// 별칭 rawcode(강화 폼)를 카탈로그 키에도 등록해 메모리 인식이 필드의 강화
    /// 유닛(예: 발라티에 강화 상디 G90H)을 카드로 인정하게 한다. 항목 자체는
    /// 대표 코드 항목을 공유한다.
    /// </summary>
    private static IReadOnlyDictionary<string, RawcodeCatalogEntry> WithAliasKeys(
        IReadOnlyDictionary<string, RawcodeCatalogEntry> catalog)
    {
        var expanded = new Dictionary<string, RawcodeCatalogEntry>(catalog, StringComparer.Ordinal);
        foreach (var (alias, canonical) in RawcodeAliases.Map)
            if (!expanded.ContainsKey(alias) && expanded.TryGetValue(canonical, out var entry))
                expanded[alias] = entry;
        return expanded;
    }

    public UnitDefinition Unit(string id)
    {
        if (UnitsById.TryGetValue(id, out var unit)) return KoreanUnit(unit);
        const string prefix = "rawcode:";
        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            // 강화 폼 rawcode는 대표 코드로 통일해 같은 유닛 정의를 돌려준다.
            var rawcode = RawcodeAliases.Canonical(id[prefix.Length..]);
            id = prefix + rawcode;
            if (RawcodeCatalog.TryGetValue(rawcode, out var entry))
                return new UnitDefinition
                {
                    Id = id,
                    Name = KoreanLabels.ContainsLatin(entry.Name) ? KoreanLabels.RemoveLatin(entry.Name) : entry.Name,
                    Tier = entry.Tier,
                    Recipe = RecipeFor(rawcode, entry),
                    Rawcodes = RawcodeAliases.WithSharedStats(rawcode).ToList(),
                    Tags = ["rawcode-catalog"],
                    Image = entry.Image,
                    OfficialAbilities = AbilitiesFor(entry),
                    Description = KoreanText(entry.Description)
                };
            return new UnitDefinition { Id = id, Name = "이름 미등록 유닛", Rawcodes = [rawcode] };
        }
        return new UnitDefinition { Id = id, Name = "이름 미등록 항목" };
    }

    private UnitDefinition EnrichKnownUnit(UnitDefinition unit)
    {
        var catalogEntry = unit.Rawcodes
            .Select(rawcode => RawcodeCatalog.GetValueOrDefault(rawcode))
            .FirstOrDefault(entry => entry is not null);
        var enriched = new UnitDefinition
        {
            Id = unit.Id,
            Name = unit.Name,
            Tier = unit.Tier,
            Roles = unit.Roles,
            Recipe = unit.Recipe.Count > 0 || catalogEntry is null
                ? unit.Recipe
                : RecipeFor(unit.Rawcodes.FirstOrDefault() ?? "", catalogEntry),
            Tags = unit.Tags,
            Rawcodes = unit.Rawcodes,
            Image = string.IsNullOrWhiteSpace(unit.Image) ? catalogEntry?.Image ?? "" : unit.Image,
            OfficialAbilities = unit.OfficialAbilities.Count > 0 || catalogEntry is null
                ? unit.OfficialAbilities
                : AbilitiesFor(catalogEntry),
            Description = !string.IsNullOrWhiteSpace(unit.Description) || catalogEntry is null
                ? KoreanText(unit.Description)
                : KoreanText(catalogEntry.Description)
        };
        return KoreanUnit(enriched);
    }

    // 세라핌 = 해당 캐릭터의 전설·히든 유닛 + 그린블러드(맵 그린블러드 툴팁:
    // "특정 유닛은 세라핌으로 변경됩니다"). 재료 유닛의 트리까지 완성률에 반영된다.
    private static readonly Dictionary<string, string> SeraphimMaterialRawcodes =
        new(StringComparer.Ordinal)
        {
            ["3A0h"] = "340h", // S-호크 ← 미호크 히든
            ["0A0h"] = "G30h", // S-샤크 ← 징베 전설
            ["Y90h"] = "230h", // S-스네이크 ← 핸콕 전설
            ["1A0h"] = "030h", // S-베어 ← 쿠마 전설
        };

    private Dictionary<string, int> RecipeFor(string rawcode, RawcodeCatalogEntry entry)
    {
        var recipe = entry.Recipe
            .Where(item => item.Count > 0 && !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => (UnitId: _unitIdsByRawcode.GetValueOrDefault(item.Id, "rawcode:" + item.Id), item.Count))
            .GroupBy(item => item.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count),
                StringComparer.OrdinalIgnoreCase);
        if (recipe.Count == 0 && entry.Tier.Split('[', 2)[0].Trim() == "세라핌")
        {
            if (SeraphimMaterialRawcodes.TryGetValue(rawcode, out var material))
                recipe[_unitIdsByRawcode.GetValueOrDefault(material, "rawcode:" + material)] = 1;
            recipe["item_greenblood"] = 1;
        }
        return recipe;
    }

    private static List<UnitAbilityDisplay> AbilitiesFor(RawcodeCatalogEntry entry) => entry.Abilities
        .Select(pair => new UnitAbilityDisplay
        {
            Name = CanonicalAbilityName(KoreanText(pair.Key)),
            DisplayValue = AbilityDisplayValue(pair.Value)
        })
        .Where(ability => !string.IsNullOrWhiteSpace(ability.Name) &&
                          !string.IsNullOrWhiteSpace(ability.DisplayValue))
        .ToList();

    private static string CanonicalAbilityName(string name) => name switch
    {
        // Guide 43747 uses the shorter key while the base TMO catalog uses
        // '광폭화 잡기'. Normalize both so strategy aggregation never loses it.
        "광폭화" => "광폭화 잡기",
        _ => name
    };

    private static string AbilityDisplayValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True) return "가능";
        if (value.ValueKind == JsonValueKind.False) return "불가";
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number.ToString("0.##", CultureInfo.InvariantCulture);
        if (value.ValueKind != JsonValueKind.String) return "";

        var text = value.GetString()?.Trim() ?? "";
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("ture", StringComparison.OrdinalIgnoreCase)) return "가능";
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return "불가";
        return KoreanText(text);
    }

    private static string KoreanText(string value)
    {
        var safe = KoreanLabels.ContainsLatin(value) ? KoreanLabels.RemoveLatin(value) : value;
        return safe.Replace("%", "퍼센트", StringComparison.Ordinal);
    }

    private static UnitDefinition KoreanUnit(UnitDefinition unit)
    {
        if (!KoreanLabels.ContainsLatin(unit.Name) &&
            !unit.Id.Equals("item_greenblood", StringComparison.OrdinalIgnoreCase)) return unit;
        return new UnitDefinition
        {
            Id = unit.Id,
            Name = unit.Id.Equals("item_greenblood", StringComparison.OrdinalIgnoreCase)
                ? "그린블러드"
                : KoreanLabels.RemoveLatin(unit.Name),
            Tier = unit.Tier,
            Roles = unit.Roles,
            Recipe = unit.Recipe,
            Tags = unit.Tags,
            Rawcodes = unit.Rawcodes,
            Image = unit.Image,
            OfficialAbilities = unit.OfficialAbilities,
            Description = unit.Description
        };
    }

    private static IReadOnlyDictionary<string, RawcodeCatalogEntry> LoadRawcodeCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "tmo-unit-catalog.json");
        if (!File.Exists(path)) return new Dictionary<string, RawcodeCatalogEntry>(StringComparer.Ordinal);
        try
        {
            var document = JsonSerializer.Deserialize<RawcodeCatalogDocument>(File.ReadAllText(path), JsonOptions);
            return (document?.Units ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.Rawcode))
                .GroupBy(x => x.Rawcode, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        }
        catch
        {
            // The supplemental name catalog must never prevent the core recommendation data from loading.
            return new Dictionary<string, RawcodeCatalogEntry>(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, RawcodeCatalogEntry> ApplyGuideOverrides(
        IReadOnlyDictionary<string, RawcodeCatalogEntry> catalog)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "tmo-guide-43747.json");
        if (!File.Exists(path))
            throw new InvalidDataException("티모지지 43747 기준 데이터가 없습니다.");

        var document = JsonSerializer.Deserialize<WarcraftGuideOverrideDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("티모지지 43747 기준 데이터를 읽을 수 없습니다.");
        if (document.SchemaVersion != 1 || document.GuideId != 43747 || document.UnitOverrides.Count < 200)
            throw new InvalidDataException("티모지지 43747 기준 데이터가 손상되었습니다.");

        var bonClay = document.UnitOverrides.FirstOrDefault(x => x.Rawcode == "O30h");
        if (bonClay is null || !bonClay.Abilities.TryGetValue("스턴", out var stun) ||
            !stun.TryGetDouble(out var stunValue) || Math.Abs(stunValue - 0.5) > 0.0001)
            throw new InvalidDataException("티모지지 43747 봉쿠레 기준값을 확인할 수 없습니다.");

        var merged = catalog.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var guide in document.UnitOverrides)
        {
            if (!merged.TryGetValue(guide.Rawcode, out var original)) continue;
            merged[guide.Rawcode] = new RawcodeCatalogEntry
            {
                Rawcode = original.Rawcode,
                Name = original.Name,
                // Guide 43747 is the current build authority. The base asset still labels
                // several distortion units as their removed legacy grades (hidden/changed).
                Tier = CanonicalGuideTier(guide.Tier),
                Image = original.Image,
                // Guide-specific distortion entries may have replaced the legacy recipe.
                // Use an explicit guide recipe when captured; otherwise retain the base asset.
                Recipe = guide.Recipe.Count > 0 ? guide.Recipe : original.Recipe,
                Abilities = guide.Abilities,
                Description = guide.Description
            };
        }
        return merged;
    }

    private static string CanonicalGuideTier(string tier)
    {
        var safe = tier.Trim();
        return safe.StartsWith("해적선", StringComparison.Ordinal) ? "해적선" : safe;
    }
}

public static class KoreanLabels
{
    private static readonly IReadOnlyDictionary<string, string> Tags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["greenblood"] = "그린블러드",
            ["health-recovery"] = "체력 회복"
        };

    public static string Tag(string tag) => Tags.GetValueOrDefault(tag, "특수 조건");

    public static bool ContainsLatin(string value) => value.Any(character =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    // User-provided data can contain internal English labels. Do not leak those labels into the
    // overlay: remove the Latin token and retain the meaningful Korean portion.
    public static string RemoveLatin(string value)
    {
        value = value.Replace("Green Blood", "그린블러드", StringComparison.OrdinalIgnoreCase)
            .Replace("rawcode", "유닛 코드", StringComparison.OrdinalIgnoreCase)
            .Replace("pool", "유닛 목록", StringComparison.OrdinalIgnoreCase);
        var filtered = new string(value.Where(character =>
            !(character is >= 'A' and <= 'Z') && !(character is >= 'a' and <= 'z')).ToArray());
        while (filtered.Contains("  ", StringComparison.Ordinal)) filtered = filtered.Replace("  ", " ");
        filtered = filtered.Trim(' ', '-', '.', '_', '[', ']');
        return string.IsNullOrWhiteSpace(filtered) ? "이름 미등록 항목" : filtered;
    }
}

public sealed class RawcodeCatalogDocument
{
    public List<RawcodeCatalogEntry> Units { get; init; } = [];
}

public sealed class RawcodeCatalogEntry
{
    public string Rawcode { get; init; } = "";
    public string Name { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Image { get; init; } = "";
    public List<RawcodeRecipeEntry> Recipe { get; init; } = [];
    public Dictionary<string, JsonElement> Abilities { get; init; } = [];
    public string Description { get; init; } = "";
}

public sealed class RawcodeRecipeEntry
{
    public string Id { get; init; } = "";
    public int Count { get; init; }
}

public sealed class WarcraftGuideOverrideDocument
{
    public int SchemaVersion { get; init; }
    public int GuideId { get; init; }
    public string Source { get; init; } = "";
    public string CapturedAt { get; init; } = "";
    public List<WarcraftGuideUnitOverride> UnitOverrides { get; init; } = [];
}

public sealed class WarcraftGuideUnitOverride
{
    public string Rawcode { get; init; } = "";
    public string GuideUnitId { get; init; } = "";
    public string GuideName { get; init; } = "";
    public string Tier { get; init; } = "";
    public List<RawcodeRecipeEntry> Recipe { get; init; } = [];
    public Dictionary<string, JsonElement> Abilities { get; init; } = [];
    public string Description { get; init; } = "";
}

public static class AppPaths
{
    public static string UserDataDirectory
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrandOverlay");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string SettingsFile => Path.Combine(UserDataDirectory, "settings.json");
    public static string TemplateDirectory
    {
        get
        {
            var path = Path.Combine(UserDataDirectory, "templates");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
