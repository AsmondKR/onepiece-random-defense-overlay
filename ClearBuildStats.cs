using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrandOverlay;

/// <summary>
/// 티모지지 공개 클리어 기록(신 이상 난이도)을 목표 상위별 지원 유닛 채용률로 집계한다.
/// 품질 가중치는 사용자 기준을 따른다: 같은 클리어라도 유닛 수가 적을수록 잘 짠 빌드다.
/// 최신성 가중치는 데이터셋 안의 최신 기록 시각을 기준으로 감쇠해 시스템 시계에 의존하지 않는다.
/// </summary>
public sealed class ClearBuildStats
{
    public const int MinimumGoalSamples = 12;
    public const double CoreShareThreshold = 0.25;
    public const int CoreCandidateLimit = 3;
    private const double QualityWeightFloor = 0.5;
    private const double QualityWeightCeiling = 2.0;
    private const double RecencyHalfLifeDays = 14;

    private static readonly string[] GodPlusDifficulties = ["신", "악몽"];
    private static readonly string[] GoalGradePrefixes = ["초월", "불멸", "영원", "제한"];

    // 범용 유닛은 취향 신호가 아니다: 목표 클리어의 절반 이상에 등장하면 앵커 제외.
    public const double AnchorShareCeiling = 0.5;

    private readonly IReadOnlyDictionary<string, GoalClearProfile> _goals;
    private readonly IReadOnlyDictionary<string, GoalClearProfile> _soloTopGoals;
    private readonly IReadOnlyDictionary<string, GoalClearProfile> _multiTopGoals;
    // 보유 패 조건부 재집계용 원본 표본(신+ 중복 제거본).
    private readonly IReadOnlyList<ClearSample> _samples;

    public int TotalGodPlusSamples { get; }
    public int MedianUnitCount { get; }
    public DateTimeOffset? NewestSampleAt { get; }
    public DateTimeOffset? OldestSampleAt { get; }

    private ClearBuildStats(IReadOnlyDictionary<string, GoalClearProfile> goals,
        IReadOnlyDictionary<string, GoalClearProfile> soloTopGoals,
        IReadOnlyDictionary<string, GoalClearProfile> multiTopGoals,
        IReadOnlyList<ClearSample> samples,
        int totalSamples, int medianUnitCount,
        DateTimeOffset? newestSampleAt, DateTimeOffset? oldestSampleAt)
    {
        _goals = goals;
        _soloTopGoals = soloTopGoals;
        _multiTopGoals = multiTopGoals;
        _samples = samples;
        TotalGodPlusSamples = totalSamples;
        MedianUnitCount = medianUnitCount;
        NewestSampleAt = newestSampleAt;
        OldestSampleAt = oldestSampleAt;
    }

    public static ClearBuildStats Empty { get; } = new(
        new Dictionary<string, GoalClearProfile>(StringComparer.Ordinal),
        new Dictionary<string, GoalClearProfile>(StringComparer.Ordinal),
        new Dictionary<string, GoalClearProfile>(StringComparer.Ordinal), [], 0, 0, null, null);

    public bool HasData => TotalGodPlusSamples > 0;

    /// <summary>
    /// 목표 rawcode들 중 표본이 가장 많은 목표의 클리어 프로필을 돌려준다.
    /// 클리어 API에는 항법 필드가 없으므로 상위 기수 구조로 조건화한다:
    /// 1상위 항법(바운티헌터·로얄로더)은 상위 유닛이 하나뿐인 클리어만,
    /// 다상위 항법(긴급소집 등)은 상위 유닛이 둘 이상인 클리어만 집계한 전용
    /// 프로필을 우선 쓰고, 표본이 부족하면 전체 프로필로 후퇴한다.
    /// </summary>
    public GoalClearProfile? GoalProfile(IEnumerable<string> goalRawcodes,
        TopScope scope = TopScope.Any)
    {
        var codes = goalRawcodes as ICollection<string> ?? goalRawcodes.ToList();
        if (scope != TopScope.Any)
        {
            var scoped = Lookup(scope == TopScope.SoloTop ? _soloTopGoals : _multiTopGoals, codes);
            if (scoped is not null && scoped.SampleCount >= MinimumGoalSamples) return scoped;
        }
        return Lookup(_goals, codes);

        static GoalClearProfile? Lookup(IReadOnlyDictionary<string, GoalClearProfile> goals,
            IEnumerable<string> codes) => codes
            .Select(rawcode => goals.GetValueOrDefault(rawcode))
            .Where(profile => profile is not null)
            .OrderByDescending(profile => profile!.SampleCount)
            .FirstOrDefault();
    }

    /// <summary>
    /// 목표 표본이 임계값 이상일 때만 0~100 정수 채용률 점수를 돌려준다.
    /// 표본이 부족하면 null을 돌려 기존 수작업 커뮤니티 테이블로 후퇴시킨다.
    /// </summary>
    public int? PriorityScore(IEnumerable<string> goalRawcodes,
        IEnumerable<string> candidateRawcodes, TopScope scope = TopScope.Any)
    {
        var profile = GoalProfile(goalRawcodes, scope);
        if (profile is null || profile.SampleCount < MinimumGoalSamples) return null;
        var share = candidateRawcodes
            .Select(rawcode => profile.SupportShare.GetValueOrDefault(rawcode))
            .DefaultIfEmpty()
            .Max();
        return (int)Math.Round(share * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>같은 조건이면 클리어 채용률 상위 3개(25퍼센트 이상)를 커뮤니티 코어로 본다.</summary>
    public bool IsCore(IEnumerable<string> goalRawcodes, IEnumerable<string> candidateRawcodes,
        TopScope scope = TopScope.Any)
    {
        var profile = GoalProfile(goalRawcodes, scope);
        if (profile is null || profile.SampleCount < MinimumGoalSamples) return false;
        return candidateRawcodes.Any(rawcode => profile.CoreRawcodes.Contains(rawcode));
    }

    /// <summary>UI 근거 표기용: 표본 수와 채용률 퍼센트.</summary>
    public ClearEvidence? Evidence(IEnumerable<string> goalRawcodes,
        IEnumerable<string> candidateRawcodes, TopScope scope = TopScope.Any)
    {
        var profile = GoalProfile(goalRawcodes, scope);
        if (profile is null || profile.SampleCount < MinimumGoalSamples) return null;
        var share = candidateRawcodes
            .Select(rawcode => profile.SupportShare.GetValueOrDefault(rawcode))
            .DefaultIfEmpty()
            .Max();
        if (share <= 0) return null;
        return new ClearEvidence(profile.SampleCount,
            (int)Math.Round(share * 100, MidpointRounding.AwayFromZero),
            Scope: profile.Scope);
    }

    public static ClearBuildStats FromSamples(IEnumerable<ClearSample> samples)
    {
        var godPlus = samples
            .Select(CanonicalizeSample)
            .Where(sample => GodPlusDifficulties.Contains(sample.Difficulty, StringComparer.Ordinal))
            .Where(sample => sample.Units.Count > 0 && EffectiveUnitCount(sample) > 0)
            .GroupBy(sample => sample.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (godPlus.Count == 0) return Empty;

        var sortedCounts = godPlus.Select(EffectiveUnitCount).OrderBy(x => x).ToList();
        var median = sortedCounts[sortedCounts.Count / 2];
        var newest = godPlus.Max(sample => sample.CreatedAt);
        var oldest = godPlus.Min(sample => sample.CreatedAt);

        var goals = BuildProfiles(godPlus, median, newest, TopScope.Any);
        var soloGoals = BuildProfiles(godPlus, median, newest, TopScope.SoloTop);
        var multiGoals = BuildProfiles(godPlus, median, newest, TopScope.MultiTop);
        return new ClearBuildStats(goals, soloGoals, multiGoals, godPlus, godPlus.Count, median,
            newest, oldest);
    }

    /// <summary>
    /// 목표 프로필을 해석하되, 보유 유닛(앵커)이 있으면 "그 유닛과 같이 쓰인 클리어"만
    /// 골라 채용률을 재집계한다(취향 조건부 학습 — 같은 상위라도 에넬+마젠 편안러와
    /// 단일·끝딜 도전러의 지원 구성이 갈린다는 고인물 검증 반영). 앵커는 목표 클리어에
    /// 실제로 등장하되 범용(50퍼센트 이상)은 아닌 유닛만 인정하고, 특이도(낮은 채용률)
    /// 순으로 교집합을 좁히되 표본이 12판 미만으로 쪼개지는 앵커는 건너뛴다.
    /// </summary>
    public GoalClearProfile? ResolveProfile(IEnumerable<string> goalRawcodes,
        TopScope scope = TopScope.Any, IEnumerable<string>? ownedRawcodes = null)
    {
        var baseProfile = GoalProfile(goalRawcodes, scope);
        if (baseProfile is null || baseProfile.SampleCount < MinimumGoalSamples) return null;
        var owned = ownedRawcodes?.Distinct(StringComparer.Ordinal).ToList();
        if (owned is null || owned.Count == 0) return baseProfile;

        var anchors = owned
            .Select(code => (Code: code, Share: baseProfile.SupportShare.GetValueOrDefault(code)))
            .Where(pair => pair.Share > 0 && pair.Share < AnchorShareCeiling)
            .OrderBy(pair => pair.Share)
            .Select(pair => pair.Code)
            .ToList();
        if (anchors.Count == 0) return baseProfile;

        var subset = _samples
            .Where(sample => ContainsCode(sample, baseProfile.GoalRawcode))
            .Where(sample => MatchesScope(sample, baseProfile.Scope))
            .ToList();
        var applied = new List<string>();
        foreach (var anchor in anchors)
        {
            var narrowed = subset.Where(sample => ContainsCode(sample, anchor)).ToList();
            if (narrowed.Count < MinimumGoalSamples) continue;
            subset = narrowed;
            applied.Add(anchor);
        }
        if (applied.Count == 0) return baseProfile;
        return BuildConditionalProfile(baseProfile, subset, applied);
    }

    /// <summary>강화 폼 rawcode(예: 상디초월 G90H)를 대표 코드로 통일해 집계한다.</summary>
    private static ClearSample CanonicalizeSample(ClearSample sample) =>
        sample.Units.All(unit => RawcodeAliases.Canonical(unit.Code) == unit.Code)
            ? sample
            : sample with
            {
                Units = sample.Units
                    .Select(unit => unit with { Code = RawcodeAliases.Canonical(unit.Code) })
                    .ToList()
            };

    private static bool ContainsCode(ClearSample sample, string code) =>
        sample.Units.Any(unit => unit.Code.Equals(code, StringComparison.Ordinal));

    private static bool MatchesScope(ClearSample sample, TopScope scope)
    {
        if (scope == TopScope.Any) return true;
        var tops = sample.Units.Count(unit => IsGoalGrade(unit.Grade));
        return scope == TopScope.SoloTop ? tops == 1 : tops >= 2;
    }

    private GoalClearProfile BuildConditionalProfile(GoalClearProfile baseProfile,
        IReadOnlyList<ClearSample> subset, IReadOnlyList<string> anchors)
    {
        var newest = NewestSampleAt ?? subset.Max(sample => sample.CreatedAt);
        var totalWeight = 0d;
        var shares = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var sample in subset)
        {
            var weight = SampleWeight(sample, MedianUnitCount, newest);
            totalWeight += weight;
            foreach (var unit in sample.Units
                         .Where(unit => !string.IsNullOrWhiteSpace(unit.Code))
                         .GroupBy(unit => unit.Code, StringComparer.Ordinal)
                         .Select(group => group.First()))
            {
                if (unit.Code.Equals(baseProfile.GoalRawcode, StringComparison.Ordinal)) continue;
                shares[unit.Code] = shares.GetValueOrDefault(unit.Code) + weight;
            }
        }
        var normalized = shares.ToDictionary(pair => pair.Key,
            pair => Math.Min(1.0, pair.Value / totalWeight), StringComparer.Ordinal);
        var core = normalized
            .Where(pair => pair.Value >= CoreShareThreshold)
            .OrderByDescending(pair => pair.Value)
            .Take(CoreCandidateLimit)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        return new GoalClearProfile(baseProfile.GoalRawcode, subset.Count, normalized, core,
            baseProfile.Scope, anchors.ToList());
    }

    private static Dictionary<string, GoalClearProfile> BuildProfiles(
        IReadOnlyList<ClearSample> godPlus, int median, DateTimeOffset newest, TopScope scope)
    {
        var goalTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        var goalSamples = new Dictionary<string, int>(StringComparer.Ordinal);
        var support = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        foreach (var sample in godPlus)
        {
            var weight = SampleWeight(sample, median, newest);
            var codes = sample.Units
                .Where(unit => !string.IsNullOrWhiteSpace(unit.Code))
                .GroupBy(unit => unit.Code, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var goals = codes.Where(unit => IsGoalGrade(unit.Grade)).ToList();
            // 1상위 프로필은 상위 등급 유닛이 정확히 하나, 다상위 프로필은 둘 이상인
            // 클리어만 집계한다. 항법 필드가 없는 클리어 기록의 구조적 대리 변수다.
            if (scope == TopScope.SoloTop && goals.Count != 1) continue;
            if (scope == TopScope.MultiTop && goals.Count < 2) continue;
            foreach (var goal in goals)
            {
                goalTotals[goal.Code] = goalTotals.GetValueOrDefault(goal.Code) + weight;
                goalSamples[goal.Code] = goalSamples.GetValueOrDefault(goal.Code) + 1;
                if (!support.TryGetValue(goal.Code, out var shares))
                    support[goal.Code] = shares = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var unit in codes)
                {
                    if (unit.Code.Equals(goal.Code, StringComparison.Ordinal)) continue;
                    shares[unit.Code] = shares.GetValueOrDefault(unit.Code) + weight;
                }
            }
        }

        var profiles = new Dictionary<string, GoalClearProfile>(StringComparer.Ordinal);
        foreach (var (goalCode, totalWeight) in goalTotals)
        {
            if (totalWeight <= 0) continue;
            var shares = support[goalCode]
                .ToDictionary(pair => pair.Key, pair => Math.Min(1.0, pair.Value / totalWeight),
                    StringComparer.Ordinal);
            var core = shares
                .Where(pair => pair.Value >= CoreShareThreshold)
                .OrderByDescending(pair => pair.Value)
                .Take(CoreCandidateLimit)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            profiles[goalCode] = new GoalClearProfile(goalCode, goalSamples[goalCode], shares, core,
                scope);
        }
        return profiles;
    }

    public static ClearBuildStats Load(IEnumerable<string> samplePaths)
    {
        var samples = new List<ClearSample>();
        foreach (var path in samplePaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                samples.AddRange(ClearSampleDocument.Parse(File.ReadAllText(path)));
            }
            catch
            {
                // 표본 파일 손상은 추천 자체를 멈출 이유가 아니다. 남은 파일과
                // 수작업 커뮤니티 테이블 폴백으로 계속 동작한다.
            }
        }
        return FromSamples(samples);
    }

    /// <summary>
    /// 일부 기록은 unitCount가 0으로 깨져 있다. 나열된 유닛 수량 합과 비교해
    /// 큰 쪽을 실제 빌드 크기로 본다(수치가 작을수록 잘 짠 빌드 가중치의 입력).
    /// </summary>
    public static int EffectiveUnitCount(ClearSample sample) =>
        Math.Max(sample.UnitCount, sample.Units.Sum(unit => unit.Count));

    internal static double SampleWeight(ClearSample sample, int medianUnitCount,
        DateTimeOffset newestSampleAt)
    {
        var effectiveCount = EffectiveUnitCount(sample);
        var quality = medianUnitCount <= 0 || effectiveCount <= 0
            ? 1.0
            : Math.Clamp((double)medianUnitCount / effectiveCount,
                QualityWeightFloor, QualityWeightCeiling);
        var ageDays = Math.Max(0, (newestSampleAt - sample.CreatedAt).TotalDays);
        var recency = Math.Pow(0.5, ageDays / RecencyHalfLifeDays);
        return quality * recency;
    }

    private static bool IsGoalGrade(string grade) =>
        GoalGradePrefixes.Any(prefix => grade.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>
/// 상위·항법 선택 UI에 "학습된 것만" 노출하기 위한 판정(사용자 요청).
/// 상위는 신+ 표본 12판 이상, 항법은 그 스코프(1상위/다상위) 전용 표본이
/// 실제로 있어야 학습된 것으로 본다(전체 프로필 폴백만 있으면 숨김).
/// </summary>
public static class LearnedSelection
{
    public static int GoalSampleCount(ClearBuildStats? stats, UnitDefinition unit) =>
        stats?.GoalProfile(unit.Rawcodes)?.SampleCount ?? 0;

    public static bool NavigationLearned(ClearBuildStats? stats, UnitDefinition? goal,
        NavigationOption option)
    {
        if (stats is null || !stats.HasData || goal is null) return true;
        var scope = option.AllowsMultipleTopUnits ? TopScope.MultiTop
            : option.CanCraftTopUnits ? TopScope.SoloTop
            : TopScope.Any;
        var profile = stats.GoalProfile(goal.Rawcodes, scope);
        if (profile is null || profile.SampleCount < ClearBuildStats.MinimumGoalSamples)
            return false;
        return scope == TopScope.Any || profile.Scope == scope;
    }
}

/// <summary>클리어 프로필의 상위 기수 조건. 항법 자체는 기록에 없어 구조로 대신한다.</summary>
public enum TopScope
{
    Any,
    SoloTop,
    MultiTop
}

public sealed record GoalClearProfile(
    string GoalRawcode,
    int SampleCount,
    IReadOnlyDictionary<string, double> SupportShare,
    IReadOnlySet<string> CoreRawcodes,
    TopScope Scope = TopScope.Any,
    IReadOnlyList<string>? AnchorRawcodes = null);

public sealed record ClearEvidence(int SampleCount, int SharePercent, TopScope Scope = TopScope.Any,
    string? AnchorLabel = null)
{
    public bool SoloTop => Scope == TopScope.SoloTop;
    public bool MultiTop => Scope == TopScope.MultiTop;
}

public sealed record ClearSample(
    string Id,
    DateTimeOffset CreatedAt,
    string Difficulty,
    int UnitCount,
    IReadOnlyList<ClearSampleUnit> Units);

public sealed record ClearSampleUnit(string Code, int Count, string Grade);

public static class ClearSampleDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>스냅샷/캐시 JSON을 표본 목록으로 해석한다. 필수 키가 빠진 레코드는 건너뛴다.</summary>
    public static IReadOnlyList<ClearSample> Parse(string json)
    {
        var document = JsonSerializer.Deserialize<CompactDocument>(json, JsonOptions);
        if (document is null || document.SchemaVersion != 1) return [];
        var results = new List<ClearSample>(document.Samples.Count);
        foreach (var sample in document.Samples)
        {
            if (string.IsNullOrWhiteSpace(sample.I) || string.IsNullOrWhiteSpace(sample.D)) continue;
            if (!DateTimeOffset.TryParse(sample.T, out var createdAt)) continue;
            var units = (sample.U ?? [])
                .Where(unit => !string.IsNullOrWhiteSpace(unit.C))
                .Select(unit => new ClearSampleUnit(unit.C, Math.Max(1, unit.K), unit.G ?? ""))
                .ToList();
            results.Add(new ClearSample(sample.I, createdAt, sample.D, sample.N, units));
        }
        return results;
    }

    public static string Serialize(IEnumerable<ClearSample> samples, string source, string capturedAt)
    {
        var document = new CompactDocument
        {
            SchemaVersion = 1,
            Source = source,
            CapturedAt = capturedAt,
            Samples = samples.Select(sample => new CompactSample
            {
                I = sample.Id,
                T = sample.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                D = sample.Difficulty,
                N = sample.UnitCount,
                U = sample.Units.Select(unit => new CompactUnit
                {
                    C = unit.Code,
                    K = unit.Count,
                    G = unit.Grade
                }).ToList()
            }).ToList()
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private sealed class CompactDocument
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = "";
        public string CapturedAt { get; set; } = "";
        public List<CompactSample> Samples { get; set; } = [];
    }

    private sealed class CompactSample
    {
        [JsonPropertyName("i")] public string I { get; set; } = "";
        [JsonPropertyName("t")] public string T { get; set; } = "";
        [JsonPropertyName("d")] public string D { get; set; } = "";
        [JsonPropertyName("n")] public int N { get; set; }
        [JsonPropertyName("u")] public List<CompactUnit>? U { get; set; }
    }

    private sealed class CompactUnit
    {
        [JsonPropertyName("c")] public string C { get; set; } = "";
        [JsonPropertyName("k")] public int K { get; set; }
        [JsonPropertyName("g")] public string? G { get; set; }
    }
}
