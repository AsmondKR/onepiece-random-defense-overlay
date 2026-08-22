namespace OrandOverlay;

/// <summary>
/// 표본이 부족할 때 쓰는 상위별 수작업 커뮤니티 우선순위.
/// RecommendationEngine에서 데이터/정책을 분리해 새 상위 메타를 추가할 때 엔진 본체를 덜 건드리게 한다.
/// </summary>
public static class RecommendationCommunityPriorities
{
    private static readonly IReadOnlyDictionary<string, int> Yamato =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Q30h"] = 12,
            ["V50h"] = 11,
            ["F30h"] = 10,
            ["HA0h"] = 10,
            ["830h"] = 9,
            ["M30h"] = 9,
            ["W50h"] = 8,
            ["630h"] = 8,
            ["B30h"] = 7,
            ["N30h"] = 7,
            ["O30h"] = 6,
            ["W20h"] = 6,
            ["Z20h"] = 5,
            ["V20h"] = 1
        };

    private static readonly IReadOnlyDictionary<string, int> Usopp =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["M30h"] = 21,
            ["W20h"] = 20,
            ["IC0h"] = 20,
            ["630h"] = 18,
            ["W50h"] = 16,
            ["Q30h"] = 15,
            ["N30h"] = 15,
            ["HA0h"] = 14,
            ["830h"] = 14,
            ["H30h"] = 13,
            ["Z20h"] = 12,
            ["O30h"] = 11,
            ["V20h"] = 10,
            ["T30h"] = 9,
            ["V50h"] = 9,
            ["540h"] = 5
        };

    private static readonly IReadOnlyDictionary<string, int> Zoro =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["F50h"] = 30,
            ["O30h"] = 29,
            ["830h"] = 18,
            ["H30h"] = 17,
            ["M30h"] = 16,
            ["W50h"] = 15,
            ["540h"] = 10
        };

    private static readonly IReadOnlyDictionary<string, int> Jinbe =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Q80h"] = 40,
            ["IA0h"] = 38,
            ["B50h"] = 36,
            ["V20h"] = 34,
            ["W30h"] = 32,
            ["IC0h"] = 30,
            ["930h"] = 28
        };

    // 신+ 거프 1상위 337판 채용률. 스턴은 거프 본인이 커버하고 이감·깎·버프·짤필러를 채운다.
    private static readonly IReadOnlyDictionary<string, int> Garp =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["3A0h"] = 52, // S-호크
            ["Q30h"] = 48, // 모비딕
            ["H30h"] = 47, // 크래커
            ["W50h"] = 45, // 비비 변이
            ["M30h"] = 41, // 사보 히든
            ["630h"] = 40, // 센고쿠
            ["540h"] = 38, // 킬러
            ["V50h"] = 32, // 에이스 왜곡
            ["A30h"] = 30, // 레일리 전설
            ["N30h"] = 28, // 료쿠규
            ["E10h"] = 26, // 쵸파 두뇌강화 짤깍
            ["F30h"] = 26, // 카르가라
            ["830h"] = 26, // 시저
            ["U30h"] = 25, // 레드포스
            ["IC0h"] = 24, // 퀸
            ["T30h"] = 24, // 레베카
            ["S30h"] = 24, // 울티
            ["HA0h"] = 21, // 킹
            ["MC0h"] = 20, // 히바리
            ["O30h"] = 16, // 봉쿠레
            ["K30h"] = 16, // 히루루크
            ["K50h"] = 15, // 페로나 짤이감
            ["U10h"] = 14, // 바질 희귀 짤깍
            ["W20h"] = 12, // 드래곤 전설
            ["X90h"] = 12, // 드레이크 짤이감·짤깍
            ["D20h"] = 10, // 키드 짤이감
            ["B20h"] = 8,  // 아오키지 희귀 짤이감
            ["H20h"] = 8,  // 크로커다일 희귀 짤이감
            ["F10h"] = 6   // 스모커 특별 짤이감
        };

    public static IReadOnlyDictionary<string, int>? ForGoal(UnitDefinition goal)
    {
        if (goal.Id.Equals("yamato_transcendent", StringComparison.OrdinalIgnoreCase) ||
            goal.Rawcodes.Contains("DB0H", StringComparer.Ordinal))
            return Yamato;
        if (goal.Rawcodes.Contains("B90H", StringComparer.Ordinal)) return Usopp;
        if (goal.Rawcodes.Contains("F90H", StringComparer.Ordinal)) return Zoro;
        if (goal.Rawcodes.Contains("A90H", StringComparer.Ordinal)) return Jinbe;
        if (goal.Rawcodes.Contains("C40h", StringComparer.Ordinal)) return Garp;
        return null;
    }
}
