using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrandOverlay;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemoryLocatorKind
{
    SignatureRelative,
    ModuleOffset
}

public sealed class MemoryProfile
{
    public int ProfileSchemaVersion { get; init; } = 1;
    public string ProfileId { get; init; } = "";
    public int ProfileRevision { get; init; } = 1;
    public string FileVersion { get; init; } = "";
    public string ModuleName { get; init; } = "Warcraft III.exe";
    public bool Enabled { get; init; }
    public bool Verified { get; init; }
    public string ExecutableSha256 { get; init; } = "";
    public MemoryLocatorKind LocatorKind { get; init; } = MemoryLocatorKind.SignatureRelative;
    public long ModuleOffset { get; init; }
    public string Signature { get; init; } = "";
    public int RelativeDisplacementOffset { get; init; }
    public int InstructionLength { get; init; }
    public int[] PointerOffsets { get; init; } = [];
    public int CountOffset { get; init; }
    public int EntriesPointerOffset { get; init; }
    public bool EntriesAreInline { get; init; }
    public int EntryStride { get; init; } = 8;
    public int EntryPointerOffset { get; init; }
    public bool EntriesContainPointers { get; init; } = true;
    public int[] OwnerPointerOffsets { get; init; } = [];
    public int OwnerOffset { get; init; }
    public int[] RawcodePointerOffsets { get; init; } = [];
    public int RawcodeOffset { get; init; }
    public byte LocalPlayerSlot { get; init; }
    public int MaximumUnits { get; init; } = 5000;
    public bool RequireNonEmptyInventory { get; init; } = true;
    public double MinimumCatalogMatchRatio { get; init; } = 0.6;
}

public static class MemoryProfileValidator
{
    public static IReadOnlyList<string> Validate(MemoryProfile profile)
    {
        var errors = new List<string>();
        if (profile.ProfileSchemaVersion != 1) errors.Add("profileSchemaVersion은 1이어야 합니다");
        if (string.IsNullOrWhiteSpace(profile.ProfileId)) errors.Add("profileId가 비어 있습니다");
        if (profile.ProfileRevision < 1) errors.Add("profileRevision은 1 이상이어야 합니다");
        if (string.IsNullOrWhiteSpace(profile.FileVersion)) errors.Add("fileVersion이 비어 있습니다");
        if (string.IsNullOrWhiteSpace(profile.ModuleName)) errors.Add("moduleName이 비어 있습니다");
        if (profile.ExecutableSha256.Length != 64 || profile.ExecutableSha256.Any(x => !Uri.IsHexDigit(x)))
            errors.Add("executableSha256은 64자리 SHA-256이어야 합니다");

        if (profile.LocatorKind == MemoryLocatorKind.ModuleOffset)
        {
            if (profile.ModuleOffset < 0) errors.Add("moduleOffset은 음수일 수 없습니다");
        }
        else
        {
            try
            {
                var tokens = profile.Signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var fixedBytes = tokens.Count(x => x is not "?" and not "??");
                _ = SignaturePattern.Parse(profile.Signature);
                if (tokens.Length is < 8 or > 128 || fixedBytes < 8)
                    errors.Add("signature는 고정 바이트 8개 이상인 8~128바이트 패턴이어야 합니다");
            }
            catch { errors.Add("signature 형식이 잘못되었습니다"); }
            if (profile.RelativeDisplacementOffset is < 0 or > 128)
                errors.Add("relativeDisplacementOffset 범위가 잘못되었습니다");
            if (profile.InstructionLength is < 4 or > 128)
                errors.Add("instructionLength 범위가 잘못되었습니다");
        }

        ValidatePointerOffsets(profile.PointerOffsets, "pointerOffsets", errors);
        ValidatePointerOffsets(profile.OwnerPointerOffsets, "ownerPointerOffsets", errors);
        ValidatePointerOffsets(profile.RawcodePointerOffsets, "rawcodePointerOffsets", errors);
        ValidateOffset(profile.CountOffset, "countOffset", errors);
        ValidateOffset(profile.EntriesPointerOffset, "entriesPointerOffset", errors);
        ValidateOffset(profile.EntryPointerOffset, "entryPointerOffset", errors);
        ValidateOffset(profile.OwnerOffset, "ownerOffset", errors);
        ValidateOffset(profile.RawcodeOffset, "rawcodeOffset", errors);
        if (profile.EntryStride is < 1 or > 4096) errors.Add("entryStride는 1~4096이어야 합니다");
        if (profile.MaximumUnits is < 1 or > 10000) errors.Add("maximumUnits는 1~10000이어야 합니다");
        if (profile.LocalPlayerSlot > 27) errors.Add("localPlayerSlot은 0~27이어야 합니다");
        if (double.IsNaN(profile.MinimumCatalogMatchRatio) || profile.MinimumCatalogMatchRatio is < 0 or > 1)
            errors.Add("minimumCatalogMatchRatio는 0~1이어야 합니다");
        return errors;
    }

    public static bool CanActivate(MemoryProfile profile, out IReadOnlyList<string> errors)
    {
        var result = Validate(profile).ToList();
        if (!profile.Enabled) result.Add("enabled가 false입니다");
        if (!profile.Verified) result.Add("verified가 false입니다");
        errors = result;
        return result.Count == 0;
    }

    private static void ValidatePointerOffsets(IReadOnlyCollection<int> offsets, string name, ICollection<string> errors)
    {
        if (offsets.Count > 16) errors.Add($"{name} 단계가 16개를 초과합니다");
        if (offsets.Any(x => x is < -0x1000000 or > 0x1000000)) errors.Add($"{name} 오프셋 범위가 너무 큽니다");
    }

    private static void ValidateOffset(int offset, string name, ICollection<string> errors)
    {
        if (offset is < -0x1000000 or > 0x1000000) errors.Add($"{name} 범위가 너무 큽니다");
    }
}

internal sealed class MemoryProfileRepository
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();
    private string _stamp = "";
    private MemoryProfilesSnapshot _snapshot = new([], "", 0, "프로필을 아직 읽지 않았습니다.");
    private long _generation;

    public MemoryProfilesSnapshot GetProfiles()
    {
        var userPath = Path.Combine(AppPaths.UserDataDirectory, "memory-profiles.json");
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Data", "memory-profiles.json");
        var path = File.Exists(userPath) ? userPath : bundledPath;
        var source = File.Exists(userPath) ? $"사용자:{Path.GetFileName(userPath)}" : $"번들:{Path.GetFileName(bundledPath)}";
        string stamp;
        try
        {
            var info = new FileInfo(path);
            stamp = $"{path}|{info.Exists}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exception)
        {
            return new MemoryProfilesSnapshot([], source, _generation, exception.Message);
        }

        lock (_gate)
        {
            if (stamp == _stamp) return _snapshot;
            _stamp = stamp;
            _generation++;
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("memory-profiles.json을 찾을 수 없습니다.", path);
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                List<MemoryProfile>? profiles = document.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => document.RootElement.Deserialize<List<MemoryProfile>>(Options),
                    JsonValueKind.Object when document.RootElement.TryGetProperty("profiles", out var items)
                        => items.Deserialize<List<MemoryProfile>>(Options),
                    _ => null
                };
                if (profiles is null) throw new InvalidDataException("프로필 JSON은 배열 또는 profiles 배열을 포함한 객체여야 합니다.");
                var duplicate = profiles.GroupBy(x => $"{x.FileVersion}|{x.ModuleName}", StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(x => x.Count() > 1);
                if (duplicate is not null) throw new InvalidDataException($"중복 메모리 프로필: {duplicate.Key}");
                _snapshot = new MemoryProfilesSnapshot(profiles, source, _generation, null);
            }
            catch (Exception exception)
            {
                _snapshot = new MemoryProfilesSnapshot([], source, _generation, exception.Message);
            }
            return _snapshot;
        }
    }
}

internal sealed record MemoryProfilesSnapshot(IReadOnlyList<MemoryProfile> Profiles, string Source,
    long Generation, string? Error);

public static class RawcodeCodec
{
    public static bool TryParse(string text, out uint rawcode)
    {
        rawcode = 0;
        if (text.Length != 4 || text.Any(x => x is < (char)0x20 or > (char)0x7E)) return false;
        // TMO copies the four raw bytes from Warcraft unchanged. A catalog key such as
        // "300h" is therefore bytes 33 30 30 68 and ReadUInt32 == 0x68303033.
        rawcode = text[0] | ((uint)text[1] << 8) | ((uint)text[2] << 16) | ((uint)text[3] << 24);
        return true;
    }

    public static string Format(uint rawcode)
    {
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)(rawcode & 0xFF);
        chars[1] = (char)((rawcode >> 8) & 0xFF);
        chars[2] = (char)((rawcode >> 16) & 0xFF);
        chars[3] = (char)((rawcode >> 24) & 0xFF);
        return chars.ToArray().All(x => x is >= (char)0x20 and <= (char)0x7E)
            ? new string(chars)
            : $"0x{rawcode:X8}";
    }

    // 강화 폼 rawcode(예: 발라티에 강화 상디 G90H)는 대표 코드 id로 통일해
    // 목표 보유 판정과 클리어 학습이 같은 유닛으로 이어지게 한다.
    public static string DynamicUnitId(uint rawcode) =>
        "rawcode:" + RawcodeAliases.Canonical(Format(rawcode));
}

public sealed class RawcodeUnitMap
{
    private readonly Dictionary<uint, UnitDefinition> _known = [];
    private readonly HashSet<uint> _catalogNamed = [];
    public string? Error { get; }
    public IReadOnlySet<uint> KnownRawcodes => _known.Keys.ToHashSet();

    // The Warcraft world pool also contains map controllers and helper CUnits owned by the
    // local player.  Only rawcodes present in either maintained data source are actual cards
    // that should enter the recommendation inventory.
    public bool IsRecognizedCard(uint rawcode) =>
        _known.ContainsKey(rawcode) || _catalogNamed.Contains(rawcode);

    public RawcodeUnitMap(DataCatalog catalog)
    {
        var errors = new List<string>();
        foreach (var unit in catalog.Data.Units)
        foreach (var text in unit.Rawcodes)
        {
            if (!RawcodeCodec.TryParse(text, out var code)) { errors.Add($"{unit.Id}: 잘못된 rawcode '{text}'"); continue; }
            if (_known.TryGetValue(code, out var existing) && !existing.Id.Equals(unit.Id, StringComparison.OrdinalIgnoreCase))
                errors.Add($"rawcode {text}가 {existing.Id}, {unit.Id}에 중복 지정됨");
            else _known[code] = unit;
        }
        if (catalog.RawcodeCatalog.Count < 250)
            errors.Add($"TMO 카드 카탈로그가 없거나 불완전합니다 ({catalog.RawcodeCatalog.Count}/250+).");
        foreach (var required in new[] { "300h", "200h", "DB0H", "H00I" })
            if (!catalog.RawcodeCatalog.ContainsKey(required))
                errors.Add($"TMO 카드 카탈로그 필수 rawcode가 없습니다: {required}");
        // 키 기준으로 순회해야 별칭 rawcode(강화 폼)도 카드로 인정된다.
        // 항목의 Rawcode 필드는 대표 코드만 담고 있다.
        foreach (var (key, entry) in catalog.RawcodeCatalog)
            if (!entry.Tier.Equals("자원", StringComparison.OrdinalIgnoreCase) &&
                RawcodeCodec.TryParse(key, out var code))
                _catalogNamed.Add(code);
        if (errors.Count > 0) Error = string.Join("; ", errors);
    }

    public RawcodeMappingResult Map(IReadOnlyDictionary<uint, int> counts)
    {
        var entries = new List<InventoryEntry>();
        var unknown = new List<string>();
        var knownCount = 0;
        var catalogNamedCount = 0;
        var unknownCount = 0;
        foreach (var pair in counts.Where(x => x.Value > 0).OrderBy(x => RawcodeCodec.Format(x.Key), StringComparer.Ordinal))
        {
            if (_known.TryGetValue(pair.Key, out var unit))
            {
                entries.Add(new InventoryEntry { UnitId = unit.Id, Count = pair.Value, Confidence = 1 });
                knownCount += pair.Value;
            }
            else
            {
                var formatted = RawcodeCodec.Format(pair.Key);
                entries.Add(new InventoryEntry { UnitId = RawcodeCodec.DynamicUnitId(pair.Key), Count = pair.Value, Confidence = 1 });
                if (_catalogNamed.Contains(pair.Key)) catalogNamedCount += pair.Value;
                else
                {
                    unknown.Add(formatted);
                    unknownCount += pair.Value;
                }
            }
        }
        return new RawcodeMappingResult(entries, knownCount, catalogNamedCount, unknownCount, unknown);
    }
}

public sealed record RawcodeMappingResult(List<InventoryEntry> Entries, int KnownCount, int CatalogNamedCount,
    int UnknownCount, List<string> UnknownRawcodes);

internal static class ExecutableHashCache
{
    private static readonly object Gate = new();
    private static string _stamp = "";
    private static string _hash = "";

    public static string Sha256(string path)
    {
        var info = new FileInfo(path);
        var stamp = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        lock (Gate)
        {
            if (stamp == _stamp) return _hash;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            _hash = Convert.ToHexString(SHA256.HashData(stream));
            _stamp = stamp;
            return _hash;
        }
    }
}
