namespace OrandOverlay;

/// <summary>
/// 판 결과 자동 판정. 근거는 맵(ORDR_S2_2.314) 자신의 판정 코드다.
///
/// 패배: 어떤 이유로 지든(유닛 카운트 초과, 65라운드 포함 보스 라운드에서 시간 내 미격파,
/// 스토리 파괴) 전부 탈락 함수 m0E → 트리거 s → myN으로 모이고, myN이 그 플레이어의
/// 유닛을 전부 제거한다(GroupEnumUnitsOfPlayer + RemoveUnit 필터). 멀티 실측은
/// 패배 직후 로컬 0 · 타 소유 265. 솔로는 타 소유가 0일 수 있어, 패를 가진 뒤
/// 내 유닛 연속 전멸이면 패배로 본다.
///
/// 클리어는 난이도마다 다르다(war3map.j AN/BQN):
/// 쉬움(AN=1) 40라운드 진입, 보통(AN=2) 50라운드, 어려움 이상 65라운드 정산
/// ("마지막 라운드 유닛 점수"). 정산은 전역 신호라 남의 화면도 메모리에 생기므로
/// 내가 살아 있는지로 확인한다. 65는 정산 직후 탈락이 있어 확인 시간을 둔다.
///
/// 옛 판의 잔여 문자열 대비: 라운드는 세션 첫 관측을 기준선으로 진행량을 요구하고,
/// 정산 사본 수는 세션 기준선보다 "늘어난" 경우만 새 정산으로 본다.
/// 애매하면 unknown으로 남긴다 — 잘못된 라벨보다 없는 라벨이 낫다.
/// </summary>
public sealed class MatchOutcomeDetector(int requiredZeroScans = 2)
{
    /// <summary>패배로 볼 만큼 패를 가졌던 적이 있어야 한다(뽑기 전 0과 구분).</summary>
    private const int MinimumPeakUnits = 3;

    /// <summary>정산을 본 뒤 이만큼 지나도 패배가 없어야 클리어다(정산 직후 탈락 처리 대비).</summary>
    private static readonly TimeSpan ClearConfirmWindow = TimeSpan.FromSeconds(30);

    /// <summary>클리어 후보 구간에서 내 유닛이 살아 있는 스캔을 이만큼 봐야 한다.</summary>
    private const int RequiredAliveScans = 2;

    private int _peakLocalUnits;
    private int _consecutiveZeroScans;
    private int _baselineRound;
    private int _maxRound;
    private int _settlementBaseline = -1;
    private DateTimeOffset? _settlementSeenAt;
    private int _aliveScansAfterClear;
    private DateTimeOffset _lastObservedAt;
    private bool _defeated;
    private string _difficulty = "unknown";

    /// <summary>맵 스크립트 기준 클리어 라운드. 쉬움 40, 보통 50, 그 외 65.</summary>
    public static int ClearRound(string difficulty) => difficulty switch
    {
        "쉬움" => 40,
        "보통" => 50,
        _ => 65
    };

    /// <summary>어려움 이상은 65라운드 정산 문자열이 있어야 한다.</summary>
    public static bool NeedsSettlement(string difficulty) =>
        difficulty is not ("쉬움" or "보통");

    public string Outcome
    {
        get
        {
            if (_defeated) return "fail";
            var finalRound = ClearRound(_difficulty);
            if (_maxRound < finalRound || _baselineRound >= finalRound) return "unknown";
            if (_aliveScansAfterClear < RequiredAliveScans) return "unknown";
            if (!NeedsSettlement(_difficulty)) return "clear";
            if (_settlementSeenAt is not { } settledAt) return "unknown";
            return _lastObservedAt - settledAt >= ClearConfirmWindow ? "clear" : "unknown";
        }
    }

    public string OutcomeSource => Outcome switch
    {
        "fail" => "unitWipe",
        "clear" => NeedsSettlement(_difficulty) ? "mapSettlement" : "clearRound",
        _ => "none"
    };

    /// <summary>스캔 1회 관측. localUnits는 내 소유 유닛 수, foreignUnits는 풀의 나머지 유닛 수.</summary>
    public void Observe(int localUnits, int foreignUnits) =>
        Observe(localUnits, foreignUnits, DateTimeOffset.UtcNow);

    public void Observe(int localUnits, int foreignUnits, DateTimeOffset at)
    {
        _ = foreignUnits;
        if (_defeated) return;
        _lastObservedAt = at;
        _peakLocalUnits = Math.Max(_peakLocalUnits, localUnits);
        if (localUnits > 0 && _maxRound >= ClearRound(_difficulty))
            _aliveScansAfterClear++;

        // 멀티는 내 유닛만 사라지고 남은 유닛이 있다. 솔로는 풀에 남이 원래 없을 수
        // 있다. 패를 가진 뒤 내 유닛이 연속 전멸이면 패배. 로비·종료는 세션 경계에서
        // Reset 하므로 여기서 남 0을 막아 두면 솔로 패배가 unknown으로 새어 나간다.
        if (localUnits > 0 || _peakLocalUnits < MinimumPeakUnits)
        {
            _consecutiveZeroScans = 0;
            return;
        }

        if (++_consecutiveZeroScans >= requiredZeroScans) _defeated = true;
    }

    /// <summary>현재 라운드 관측. 처음 값은 기준선으로 잡아 옛 사본에 속지 않게 한다.</summary>
    public void ObserveRound(int round)
    {
        if (round <= 0 || _defeated) return;
        if (_baselineRound == 0) _baselineRound = round;
        _maxRound = Math.Max(_maxRound, round);
    }

    /// <summary>맵이 띄운 난이도. unknown은 덮지 않는다.</summary>
    public void ObserveDifficulty(string difficulty)
    {
        if (_defeated) return;
        if (string.IsNullOrWhiteSpace(difficulty) || difficulty == "unknown") return;
        _difficulty = difficulty;
    }

    /// <summary>정산 문자열 사본 수 관측. 세션 기준선보다 늘어나야 새 정산이다.</summary>
    public void ObserveSettlement(int copies) => ObserveSettlement(copies, DateTimeOffset.UtcNow);

    public void ObserveSettlement(int copies, DateTimeOffset at)
    {
        if (copies < 0 || _defeated) return;
        if (_settlementBaseline < 0) { _settlementBaseline = copies; return; }
        // 옛 사본이 해제되어 수가 줄면 기준선도 따라 내린다 — 이후의 진짜 정산을 놓치지 않게.
        if (copies < _settlementBaseline) _settlementBaseline = copies;
        if (copies > _settlementBaseline) _settlementSeenAt ??= at;
        if (at > _lastObservedAt) _lastObservedAt = at;
    }

    /// <summary>세션 경계에서 호출. 다음 판을 위해 상태를 비운다.</summary>
    public void Reset()
    {
        _defeated = false;
        _peakLocalUnits = 0;
        _consecutiveZeroScans = 0;
        _baselineRound = 0;
        _maxRound = 0;
        _settlementBaseline = -1;
        _settlementSeenAt = null;
        _aliveScansAfterClear = 0;
        _lastObservedAt = default;
        _difficulty = "unknown";
    }
}
