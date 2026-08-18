using System.Text.Json.Serialization;

namespace OrandOverlay;

public enum UnitRole
{
    Holding,
    Slow,
    ArmorReduction,
    AttackBoost,
    AreaControl,
    BossControl,
    Damage,
    Support
}

public sealed record RoleValue(UnitRole Role, double Value);

public sealed class UnitDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Tier { get; init; } = "일반";
    public List<RoleValue> Roles { get; init; } = [];
    public Dictionary<string, int> Recipe { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public List<string> Rawcodes { get; init; } = [];
    public string Image { get; init; } = "";
    public List<UnitAbilityDisplay> OfficialAbilities { get; init; } = [];
    public string Description { get; init; } = "";
}

public sealed class UnitAbilityDisplay
{
    public required string Name { get; init; }
    public required string DisplayValue { get; init; }
}

public sealed class RouteDefinition
{
    public required string Id { get; init; }
    public required string GoalUnitId { get; init; }
    public required string Name { get; init; }
    public List<string> RequiredUnits { get; init; } = [];
    public List<string> OptionalUnits { get; init; } = [];
    public Dictionary<UnitRole, double> DesiredRoles { get; init; } = [];
    public List<string> RequiredTags { get; init; } = [];
    public List<string> ForbiddenTogether { get; init; } = [];
    public string Note { get; init; } = "";
    public double BaseScore { get; init; } = 50;
}

public sealed class GameData
{
    public int SchemaVersion { get; init; } = 1;
    public string DataVersion { get; init; } = "demo";
    public string Disclaimer { get; init; } = "";
    public List<UnitDefinition> Units { get; init; } = [];
    public List<RouteDefinition> Routes { get; init; } = [];
}

public sealed class InventoryEntry
{
    public required string UnitId { get; init; }
    public int Count { get; set; } = 1;
    public double Confidence { get; set; } = 1;
    [JsonIgnore] public bool IsManual { get; set; }
}

public sealed record RoleStatus(UnitRole Role, double Current, double Desired)
{
    public double Ratio => Desired <= 0 ? 1 : Math.Min(1, Current / Desired);
}

public sealed class Recommendation
{
    public required RouteDefinition Route { get; init; }
    public double Score { get; init; }
    public List<string> ReadyUnits { get; init; } = [];
    public List<string> MissingUnits { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<RoleStatus> Roles { get; init; } = [];
    public string NextAction { get; init; } = "";
    public RecipeProgress RecipeProgress { get; init; } = new();
    public List<CompositionUnitDetail> CompositionUnits { get; init; } = [];
    public RecipeTreeNode? RecipeTree { get; init; }
    public List<RecipeCraftStep> RemainingCraftSteps { get; init; } = [];
    /// <summary>신+ 클리어 데이터 근거(표본·채용률). 표본이 부족하면 null이다.</summary>
    public ClearEvidence? ClearEvidence { get; set; }
}

public sealed class RecipeCraftStep
{
    public required string UnitId { get; init; }
    public required string Name { get; init; }
    public string Tier { get; init; } = "";
    public string Image { get; init; } = "";
    public int RequiredCount { get; init; }
    public int OwnedCount { get; init; }
    public List<RecipeCraftIngredient> Ingredients { get; init; } = [];
    // 맵에서 추출한 이 유닛의 조합 스킬 단축키(미상이면 null).
    public string? CombineKey { get; init; }
    // 현재 패 기준 이 단계(남은 수량)의 재료 완성률(0~1). 드릴다운 % 표시용.
    public double CompletionRatio { get; init; }

    [JsonIgnore]
    public int MissingCount => Math.Max(0, RequiredCount - OwnedCount);
}

public sealed class RecipeCraftIngredient
{
    public required string UnitId { get; init; }
    public required string Name { get; init; }
    public string Tier { get; init; } = "";
    public int RequiredCount { get; init; }
    public int SelectionOrder { get; init; }
}

public sealed class RecipeTreeNode
{
    public required string UnitId { get; init; }
    public required string Name { get; init; }
    public string Tier { get; init; } = "";
    public string Image { get; init; } = "";
    public int RequiredCount { get; init; } = 1;
    public int OwnedCount { get; init; }
    public List<RecipeTreeNode> Children { get; init; } = [];
}

public sealed class RecipeProgress
{
    public long RequiredLeafCount { get; init; }
    public long OwnedLeafCount { get; init; }
    public List<RecipeLeafProgress> Leaves { get; init; } = [];

    [JsonIgnore]
    public double CompletionRatio => RequiredLeafCount <= 0
        ? 1
        : Math.Clamp((double)OwnedLeafCount / RequiredLeafCount, 0, 1);

    [JsonIgnore]
    public IReadOnlyList<RecipeLeafProgress> MissingLeaves => Leaves
        .Where(x => x.MissingCount > 0)
        .OrderByDescending(x => x.MissingCount)
        .ThenBy(x => x.Name, StringComparer.CurrentCulture)
        .ToList();
}

public sealed class RecipeLeafProgress
{
    public required string UnitId { get; init; }
    public required string Name { get; init; }
    public string Tier { get; init; } = "";
    public string Image { get; init; } = "";
    public long RequiredCount { get; init; }
    public long OwnedCount { get; init; }

    [JsonIgnore]
    public long MissingCount => Math.Max(0, RequiredCount - OwnedCount);
}

public sealed class CompositionUnitDetail
{
    public required string UnitId { get; init; }
    public required string Name { get; init; }
    public string Tier { get; init; } = "";
    public string Image { get; init; } = "";
    public int RequiredCount { get; init; }
    public int SuggestedCount { get; init; } = 1;
    public int OwnedCount { get; init; }
    public bool IsGoal { get; init; }
    public bool IsRequired { get; init; }
    public List<UnitAbilityDisplay> Abilities { get; init; } = [];
    public string Description { get; init; } = "";

    [JsonIgnore]
    public bool IsOptional => !IsGoal && !IsRequired;

    [JsonIgnore]
    public string KindLabel => IsGoal ? "목표" : IsRequired ? "필수" : "보강";
}

public sealed class RecognitionResult
{
    public List<InventoryEntry> Entries { get; init; } = [];
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public string Status { get; init; } = "대기";
    public RecognitionState State { get; init; } = RecognitionState.Ready;
    public RecognitionDiagnostics Diagnostics { get; init; } = new();
    // True only when the reader has positive evidence that the current match context ended
    // (Warcraft exited, GameUI disappeared, or WorldFrame disappeared). A TMO reconnect or
    // wrapper-discovery delay is merely disconnected and must not erase manual corrections.
    [JsonIgnore] public bool ConfirmsSessionBoundary { get; init; }

    // A failed or transient read must never erase the last known-good inventory.
    [JsonIgnore] public bool ShouldReplaceInventory => State == RecognitionState.Ready;
    [JsonIgnore] public bool ShouldClearAutomaticInventory => State == RecognitionState.Waiting;
}

public enum RecognitionState
{
    Ready,
    Waiting,
    Unsupported,
    UnverifiedProfile,
    ConfigurationError,
    TransientReadError
}

public sealed class RecognitionDiagnostics
{
    public string Source { get; init; } = "Unknown";
    public string ProcessVersion { get; init; } = "";
    public int? ProcessId { get; init; }
    public string ProfileId { get; init; } = "";
    public int? ProfileRevision { get; init; }
    public string ProfileSource { get; init; } = "";
    public string ResolvedListAddress { get; init; } = "";
    public int ObservedObjects { get; init; }
    public int MappedObjects { get; init; }
    public int UnknownObjects { get; init; }
    public List<string> UnknownRawcodes { get; init; } = [];
    public string Detail { get; init; } = "";

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ProfileId))
                parts.Add($"프로필 {ProfileId} r{ProfileRevision ?? 0}");
            if (!string.IsNullOrWhiteSpace(ProfileSource))
                parts.Add(ProfileSource);
            if (!string.IsNullOrWhiteSpace(ProcessVersion))
                parts.Add($"워크 {ProcessVersion}" + (ProcessId is int pid ? $" · PID {pid}" : ""));
            if (!string.IsNullOrWhiteSpace(ResolvedListAddress))
                parts.Add($"목록 {ResolvedListAddress}");
            if (ObservedObjects > 0 || MappedObjects > 0 || UnknownObjects > 0)
                parts.Add($"객체 {ObservedObjects} · 매핑 {MappedObjects} · 미등록 {UnknownObjects}");
            if (UnknownRawcodes.Count > 0)
                parts.Add("미등록 rawcode: " + string.Join(", ", UnknownRawcodes.Take(8)));
            if (!string.IsNullOrWhiteSpace(Detail)) parts.Add(Detail);
            return string.Join(" | ", parts);
        }
    }

    [JsonIgnore]
    public string UserDisplayText
    {
        get
        {
            // 인식이 성공한 상태에서는 수치 요약을, 실패·대기 상태(수치가 없을 때)에는
            // Detail의 원인·조치 안내를 그대로 보여준다. 예전에는 Detail을 아예 버려서
            // 연동이 안 될 때 "확인하는 중입니다"만 반복돼 원격 진단이 불가능했다.
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ProcessVersion)) parts.Add($"워크 버전 {ProcessVersion}");
            if (MappedObjects > 0 || ObservedObjects > 0)
                parts.Add($"인식 패 {MappedObjects}개");
            if (UnknownObjects > 0) parts.Add($"내부 유닛 {UnknownObjects}개 제외");
            if (parts.Count > 0) return string.Join(" · ", parts);
            return string.IsNullOrWhiteSpace(Detail) ? "연동 상태를 확인하는 중입니다." : Detail;
        }
    }
}

public sealed class AppSettings
{
    public string GoalUnitId { get; set; } = "yamato_transcendent";
    // 첫 희귀함이 잡히면 그 희귀함이 들어가는 학습 상위로 목표를 자동 전환(자동 시작).
    public bool AutoStartGoal { get; set; } = true;
    public string NavigationMode { get; set; } = "PathOfKings.BountyHunter";
    public double CaptureIntervalSeconds { get; set; } = 0.8;
    public double MatchThreshold { get; set; } = 0.86;
    public NormalizedRect InventoryRegion { get; set; } = new(0.64, 0.58, 0.34, 0.36);
    public int GridColumns { get; set; } = 6;
    public int GridRows { get; set; } = 4;
    public bool ClickThroughOverlay { get; set; }
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public string RecognitionSource { get; set; } = "Memory";
    public bool AutoScanEnabled { get; set; } = true;
    public bool ClearDataAutoRefresh { get; set; } = true;
    // 워크 기본 단축키(Alt·Ctrl+숫자·F9~F12 등)와 겹치지 않는 키. WPF Key 이름.
    // Scroll Lock은 일부 키보드에서 Fn 조합이거나 아예 없어 Caps Lock이 기본이다.
    public string OverlayToggleKey { get; set; } = "Capital";
    // 신+ 판별 전역 변수(오로성). 게임 시작 안내창을 보고 선택한다.
    public string GoroseiMode { get; set; } = "None";
    // 자동 업데이트 무한 루프 방지: 같은 태그는 한 번만 시도한다.
    public string LastAttemptedUpdateTag { get; set; } = "";
}

/// <summary>긴급소집 와일드카드(특별함 선택) 사용처 한 건.</summary>
public sealed record EmergencySummonAdvice(string UnitId, string Name, int Count, string Reason);

/// <summary>
/// 강화·각성으로 rawcode가 바뀌는 동일 유닛의 별칭 통합. 신+ 3만 판에서 두 코드가
/// 같은 판에 동시 등장하지 않음을 확인해 등록했다(예: 상디초월은 발라티에 강화 시
/// H90H→G90H). 클리어 학습과 메모리 인식이 같은 유닛으로 취급하게 한다.
/// </summary>
public static class RawcodeAliases
{
    private static readonly IReadOnlyDictionary<string, string> AliasToCanonical =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["G90H"] = "H90H", // 상디 초월 (발라티에 강화 폼)
            ["1B0H"] = "790H", // 아오키지 초월
            ["390H"] = "190H", // 쵸파 초월
            ["TB0H"] = "F90H", // 조로 초월
            ["OB0H"] = "LB0H", // 료쿠규 초월
            ["DA0h"] = "M70h", // 카이도 불멸
            ["BA0H"] = "G10h", // 파이러츠 도킹 5
            ["MB0h"] = "E40h"  // 센고쿠 불멸 (강화 폼)
        };

    public static IReadOnlyDictionary<string, string> Map => AliasToCanonical;

    public static string Canonical(string rawcode) =>
        AliasToCanonical.GetValueOrDefault(rawcode, rawcode);

    // 니카 루초/뱀초처럼 카탈로그는 갈라놨지만(조합 트리가 다름) 인게임 rawcode가
    // 같아 클리어 기록이 한 코드로 잡히는 경우: 목표 학습 조회용 코드만 공유한다.
    private static readonly IReadOnlyDictionary<string, string[]> SharedStats =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["KB0H_"] = ["KB0H"] // 니카(뱀초) → 클리어 기록은 KB0H로 집계됨
        };

    public static IReadOnlyList<string> WithSharedStats(string rawcode) =>
        SharedStats.TryGetValue(rawcode, out var extra) ? [rawcode, .. extra] : [rawcode];
}

/// <summary>같은 상위라도 갈리는 빌드 방향(예: 니카 이감/노이감). 자동은 패 기준 판정.</summary>
public sealed record BuildVariant(string Id, string Name, string Summary);

public static class BuildVariants
{
    public const string AutoId = "auto";

    private static readonly IReadOnlyList<BuildVariant> NikaVariants =
    [
        new(AutoId, "자동 (패 기준)", "패에 쌓인 스턴이 1.8 이상이면 노이감으로 판정합니다."),
        new("slow", "이감 버전", "스턴 1.6 · 풀이감 102 — 신+ 실측 다수파(77퍼센트)."),
        new("noslow", "노이감 (2스턴)",
            "스턴 지원 0.8+0.9 또는 0.9+0.9 페어 · 이감 최소 · 방깎 마감 집중.")
    ];

    /// <summary>이 상위에 알려진 빌드 방향 목록. 없으면 빈 목록(선택 UI 숨김).</summary>
    public static IReadOnlyList<BuildVariant> For(UnitDefinition goal) =>
        goal.Rawcodes.Any(code => code is "KB0H" or "KB0H_") ? NikaVariants : [];
}

/// <summary>신+ 난이도의 판별 전역 변수. 게임 시작 시 안내창으로 확인해 선택한다.</summary>
public enum GoroseiMode
{
    None,
    Nasjuro,
    Warcury,
    Saturn
}

public sealed record GoroseiOption(GoroseiMode Mode, string Name, string Summary);

/// <summary>
/// 오로성별 빌드 목표 보정(인게임 안내 기준).
/// 나스쥬로: 적 이속 +10%·아군 공속 -10% → 이감 목표 10% 상향.
/// 워큐리: 적 방어력 +10·마법방어력 +10% → 방깎 +10, 마방깎 목표 10.
/// 새턴: 아군 공격력 -20%·폭뎀 -7%·적 체젠 증가 → 단일·끝딜 모두 확보(마딜).
/// </summary>
public static class GoroseiEffects
{
    public static IReadOnlyList<GoroseiOption> Options { get; } =
    [
        new(GoroseiMode.None, "선택 안 함", "오로성 보정 없이 기본 목표를 사용합니다."),
        new(GoroseiMode.Nasjuro, "나스쥬로", "적 이속 +10% · 아군 공속 -10% — 이감 목표를 112로 올립니다."),
        new(GoroseiMode.Warcury, "워큐리", "적 방어력 +10 · 마방 +10% — 방깎 221, 마방깎 10을 목표로 합니다."),
        new(GoroseiMode.Saturn, "새턴", "아군 공격력 -20% · 폭뎀 -7% — 단일·끝딜을 모두 확보합니다.")
    ];

    public static GoroseiMode Parse(string? value) =>
        Enum.TryParse<GoroseiMode>(value, ignoreCase: true, out var mode) ? mode : GoroseiMode.None;

    public static double AdjustSlowTarget(double target, GoroseiMode mode) =>
        mode == GoroseiMode.Nasjuro ? Math.Round(target * 1.1) : target;

    public static double AdjustArmorTarget(double target, GoroseiMode mode) =>
        mode == GoroseiMode.Warcury && target > 0 ? target + 10 : target;

    public static double AdjustMagicArmorTarget(double target, GoroseiMode mode) =>
        mode == GoroseiMode.Warcury && target > 0 ? Math.Max(target, 10) : target;

    public static string? StatsNote(GoroseiMode mode) => mode switch
    {
        GoroseiMode.Nasjuro => "오로성 나스쥬로: 이감·공속 보강 필요",
        GoroseiMode.Warcury => "오로성 워큐리: 깎기(방깎·마방깎) 보강 필요",
        GoroseiMode.Saturn => "오로성 새턴: 폭뎀 의존 유닛 성능 저하 · 딜 보강 필요",
        _ => null
    };
}

/// <summary>티모지지 공식 티어 표기(예: "초월 [마딜]") 기반 딜 유형 판정.</summary>
public static class DamageTiers
{
    public static bool IsMagic(string tier) =>
        tier.Contains("[마딜]", StringComparison.Ordinal);
}

public sealed record NavigationCategory(string Id, string Name);

public sealed record NavigationOption(
    string Id,
    string CategoryId,
    string CategoryName,
    string Name,
    int TopUnitLimit,
    string Summary)
{
    public bool CanCraftTopUnits => TopUnitLimit != 0;
    public bool AllowsMultipleTopUnits => TopUnitLimit > 1;
}

public static class NavigationProfiles
{
    public static IReadOnlyList<NavigationCategory> Categories { get; } =
    [
        new("AlliedForces", "연합세력"),
        new("PathOfKings", "패왕의길"),
        new("Gambler", "도박광"),
        new("BestHelp", "최고의도움"),
        new("Random", "랜덤선택")
    ];

    public static IReadOnlyList<NavigationOption> Options { get; } =
    [
        new("AlliedForces.DoubleBenefit", "AlliedForces", "연합세력", "일석이조", int.MaxValue,
            "항법 기본효과가 발동할 때 랜덤위습을 추가로 받습니다."),
        new("AlliedForces.EmergencyCall", "AlliedForces", "연합세력", "긴급소집", int.MaxValue,
            "특별함 선택 와일드카드 3개를 받으며 항법 기본효과는 비활성화됩니다."),
        new("AlliedForces.TraitEngineering", "AlliedForces", "연합세력", "특성공학", int.MaxValue,
            "특성공학 스킬과 특성포인트를 얻고 변화 가능 횟수가 늘어납니다."),

        new("PathOfKings.MartialLaw", "PathOfKings", "패왕의길", "계엄령", 0,
            "최상위 유닛을 조합할 수 없습니다."),
        new("PathOfKings.BountyHunter", "PathOfKings", "패왕의길", "바운티헌터", 1,
            "최상위는 1기만 가능하며 라인 처치 시 보물상자를 발견할 수 있습니다."),
        new("PathOfKings.RoyalLoader", "PathOfKings", "패왕의길", "로얄로더", 1,
            "최상위는 1기만 가능하며 조합한 최상위 유닛에 추가 능력치를 줍니다."),

        new("Gambler.Casino", "Gambler", "도박광", "카지노", int.MaxValue,
            "고급 유닛도박 성공 시 희귀함을 확정 획득하며 하급·중급 도박은 비활성화됩니다."),
        new("Gambler.RiskHedge", "Gambler", "도박광", "리스크헷지", int.MaxValue,
            "고급 도박 실패 보상이 강화되고 희귀함 리롤 비용이 사라집니다."),
        new("Gambler.ContinuousBetting", "Gambler", "도박광", "연속베팅", int.MaxValue,
            "중급 도박 누적 횟수에 따라 위습·목재·해적선을 받습니다."),

        new("BestHelp.MaximumOutput", "BestHelp", "최고의도움", "최대출력", int.MaxValue,
            "도움소 공격 스킬이 강화되고 마나포션 스킬이 추가됩니다."),
        new("BestHelp.Alchemy", "BestHelp", "최고의도움", "연금술", int.MaxValue,
            "도움소 마나로 유닛을 분해하는 연금술 스킬이 추가됩니다."),
        new("BestHelp.ReverseThinking", "BestHelp", "최고의도움", "역발상", int.MaxValue,
            "레일리 희귀함과 유니크 발굴 스택을 얻는 대신 도움소 사용에 제한이 생깁니다."),

        new("Random.BlueFlavor", "Random", "랜덤선택", "파란색맛", int.MaxValue,
            "무작위 항법을 선택하고 랜덤위습 1기를 추가로 받습니다."),
        new("Random.GreenFlavor", "Random", "랜덤선택", "초록색맛", int.MaxValue,
            "무작위 항법을 선택하고 목재 1을 추가로 받습니다."),
        new("Random.YellowFlavor", "Random", "랜덤선택", "노란색맛", int.MaxValue,
            "무작위 항법을 선택하고 압살롬 1기를 추가로 받습니다.")
    ];

    private static readonly IReadOnlyDictionary<string, string> LegacyDefaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PathOfKings"] = "PathOfKings.BountyHunter",
            ["AlliedForces"] = "AlliedForces.DoubleBenefit",
            ["Gambler"] = "Gambler.ContinuousBetting",
            ["BestHelp"] = "BestHelp.MaximumOutput",
            ["Random"] = "Random.BlueFlavor"
        };

    public static IReadOnlyList<NavigationOption> ForCategory(string? categoryId) => Options
        .Where(option => option.CategoryId.Equals(categoryId, StringComparison.OrdinalIgnoreCase))
        .ToList();

    public static NavigationCategory FindCategory(string? id) => Categories.FirstOrDefault(category =>
        category.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Categories[1];

    public static NavigationOption Find(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && LegacyDefaults.TryGetValue(id, out var migratedId))
            id = migratedId;

        return Options.FirstOrDefault(option => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? Options.First(option => option.Id == "PathOfKings.BountyHunter");
    }
}

public static class RecognitionPolicy
{
    public static string NormalizeSource(string? source) =>
        source?.Equals("Screen", StringComparison.OrdinalIgnoreCase) == true ? "Screen" : "Memory";

    // A short in-match snapshot race may keep the last recommendation stable. A disconnected,
    // loading, unsupported, or misconfigured source must never drive "current" recommendations.
    public static bool MayUseLastGoodForRecommendations(RecognitionState state) =>
        state == RecognitionState.TransientReadError;

    public static bool IsConfirmedOutOfGame(RecognitionResult result) => result.ConfirmsSessionBoundary;
}

public static class InventoryMerge
{
    public static bool CanDecrement(IEnumerable<InventoryEntry> currentInventory, string unitId) =>
        currentInventory.Any(x => x.Count > 0 && x.UnitId.Equals(unitId, StringComparison.OrdinalIgnoreCase));

    public static List<InventoryEntry> ApplyCorrections(IEnumerable<InventoryEntry> automatic,
        IEnumerable<InventoryEntry> manualDeltas, Func<string, bool>? isSingleton = null)
    {
        var result = automatic.Where(x => x.Count > 0).Select(x => new InventoryEntry
        {
            UnitId = x.UnitId,
            Count = x.Count,
            Confidence = x.Confidence
        }).ToDictionary(x => x.UnitId, StringComparer.OrdinalIgnoreCase);

        foreach (var delta in manualDeltas.Where(x => x.Count != 0))
        {
            if (result.TryGetValue(delta.UnitId, out var current)) current.Count += delta.Count;
            else if (delta.Count > 0)
                result[delta.UnitId] = new InventoryEntry
                {
                    UnitId = delta.UnitId,
                    Count = delta.Count,
                    Confidence = 1,
                    IsManual = true
                };

            if (!result.TryGetValue(delta.UnitId, out current)) continue;
            if (isSingleton?.Invoke(delta.UnitId) == true) current.Count = Math.Min(current.Count, 1);
            if (current.Count <= 0) result.Remove(delta.UnitId);
        }
        return result.Values.ToList();
    }
}

public sealed record NormalizedRect(double X, double Y, double Width, double Height);

public readonly record struct OverlayPosition(double Left, double Top);
public readonly record struct OverlayBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static class OverlayPositionPolicy
{
    public static OverlayPosition ClampToNearestWorkArea(double left, double top, double width, double height,
        IReadOnlyList<OverlayBounds> workAreas)
    {
        if (workAreas.Count == 0) return new OverlayPosition(left, top);
        var windowLeft = left;
        var windowTop = top;
        var windowRight = left + width;
        var windowBottom = top + height;
        var centerX = left + width / 2;
        var centerY = top + height / 2;

        var target = workAreas
            .Select(area =>
            {
                var intersectionWidth = Math.Max(0, Math.Min(windowRight, area.Right) - Math.Max(windowLeft, area.Left));
                var intersectionHeight = Math.Max(0, Math.Min(windowBottom, area.Bottom) - Math.Max(windowTop, area.Top));
                var intersection = intersectionWidth * intersectionHeight;
                var nearestX = Math.Clamp(centerX, area.Left, area.Right);
                var nearestY = Math.Clamp(centerY, area.Top, area.Bottom);
                var distance = Math.Pow(centerX - nearestX, 2) + Math.Pow(centerY - nearestY, 2);
                return (Area: area, Intersection: intersection, Distance: distance);
            })
            .OrderByDescending(x => x.Intersection)
            .ThenBy(x => x.Distance)
            .First().Area;

        return Clamp(left, top, width, height, target.Left, target.Top, target.Width, target.Height);
    }

    public static OverlayPosition Clamp(double left, double top, double width, double height,
        double virtualLeft, double virtualTop, double virtualWidth, double virtualHeight)
    {
        var safeWidth = double.IsFinite(width) && width > 0 ? width : 430;
        var safeHeight = double.IsFinite(height) && height > 0 ? height : 650;
        var maximumLeft = virtualLeft + Math.Max(0, virtualWidth - safeWidth);
        var maximumTop = virtualTop + Math.Max(0, virtualHeight - safeHeight);
        var safeLeft = double.IsFinite(left) ? Math.Clamp(left, virtualLeft, maximumLeft) : virtualLeft;
        var safeTop = double.IsFinite(top) ? Math.Clamp(top, virtualTop, maximumTop) : virtualTop;
        return new OverlayPosition(safeLeft, safeTop);
    }
}
