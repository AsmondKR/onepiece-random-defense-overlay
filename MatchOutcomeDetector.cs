namespace OrandOverlay;

/// <summary>
/// 판 결과 자동 판정. 근거는 맵(ORDR_S2_2.314) 자신의 판정 코드다.
///
/// 패배: 어떤 이유로 지든(유닛 카운트 초과, 65라운드 포함 보스 라운드에서 시간 내 미격파,
/// 스토리 파괴) 전부 탈락 함수 m0E → 트리거 s → myN으로 모이고, myN이 그 플레이어의
/// 유닛을 전부 제거한다(GroupEnumUnitsOfPlayer + RemoveUnit 필터). 즉 맵의 패배 판정
/// 결과가 곧 "내 유닛 전멸 · 남의 유닛은 잔존"이다. (실측: 패배 직후 로컬 0 · 타 소유 265)
///
/// 클리어: 65라운드 정산 블록(BQN==65)이 그 시점 생존자에게만 "마지막 라운드 유닛 점수"
/// 문자열을 조합한다 — 이것이 맵의 클리어 판정이다. 단 JASS는 모든 클라이언트에서 같은
/// 코드가 돌아 남의 정산 문자열도 내 메모리에 생기므로, 정산은 "판이 끝까지 갔다"는 전역
/// 신호로만 쓰고 "내가" 살아남았는지는 정산 후에도 내 유닛이 남아 있는지로 확인한다.
/// 정산과 같은 순간 패배 처리가 돌 수 있어 확인 시간을 두고 기다린다.
///
/// 옛 판의 잔여 문자열 대비: 라운드는 세션 첫 관측을 기준선으로 진행량을 요구하고,
/// 정산 사본 수는 세션 기준선보다 "늘어난" 경우만 새 정산으로 본다.
/// 애매하면 unknown으로 남긴다 — 잘못된 라벨보다 없는 라벨이 낫다.
/// </summary>
public sealed class MatchOutcomeDetector(int requiredZeroScans = 2)
{
    /// <summary>패배로 볼 만큼 패를 가졌던 적이 있어야 한다(뽑기 전 0과 구분).</summary>
    private const int MinimumPeakUnits = 3;

    /// <summary>맵의 마지막 라운드. 정산 문자열은 여기서만 만들어진다.</summary>
    private const int FinalRound = 65;

    /// <summary>옛 라운드 사본에 속지 않도록, 이만큼의 진행을 직접 지켜봐야 한다.</summary>
    private const int MinimumObservedProgress = 30;

    /// <summary>정산을 본 뒤 이만큼 지나도 패배가 없어야 클리어다(정산 직후 탈락 처리 대비).</summary>
    private static readonly TimeSpan ClearConfirmWindow = TimeSpan.FromSeconds(30);

    /// <summary>정산 후 내 유닛이 살아 있는 스캔을 이만큼 봐야 한다.</summary>
    private const int RequiredAliveScansAfterSettlement = 2;

    private int _peakLocalUnits;
    private int _consecutiveZeroScans;
    private int _baselineRound;
    private int _maxRound;
    private int _settlementBaseline = -1;
    private DateTimeOffset? _settlementSeenAt;
    private int _aliveScansAfterSettlement;
    private DateTimeOffset _lastObservedAt;
    private bool _defeated;

    public string Outcome
    {
        get
        {
            if (_defeated) return "fail";
            if (_settlementSeenAt is not { } settledAt) return "unknown";
            if (_maxRound < FinalRound || _maxRound - _baselineRound < MinimumObservedProgress)
                return "unknown";
            if (_aliveScansAfterSettlement < RequiredAliveScansAfterSettlement) return "unknown";
            return _lastObservedAt - settledAt >= ClearConfirmWindow ? "clear" : "unknown";
        }
    }

    public string OutcomeSource => Outcome switch
    {
        "fail" => "unitWipe",
        "clear" => "mapSettlement",
        _ => "none"
    };

    /// <summary>스캔 1회 관측. localUnits는 내 소유 유닛 수, foreignUnits는 풀의 나머지 유닛 수.</summary>
    public void Observe(int localUnits, int foreignUnits) =>
        Observe(localUnits, foreignUnits, DateTimeOffset.UtcNow);

    public void Observe(int localUnits, int foreignUnits, DateTimeOffset at)
    {
        if (_defeated) return;
        _lastObservedAt = at;
        _peakLocalUnits = Math.Max(_peakLocalUnits, localUnits);
        if (_settlementSeenAt is not null && localUnits > 0) _aliveScansAfterSettlement++;

        // 풀에 남의 유닛도 없으면 판이 끝났거나 읽기가 흔들린 것 — 패배로 단정하지 않는다.
        if (localUnits > 0 || foreignUnits <= 0 || _peakLocalUnits < MinimumPeakUnits)
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
        _aliveScansAfterSettlement = 0;
        _lastObservedAt = default;
    }
}
