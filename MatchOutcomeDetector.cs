namespace OrandOverlay;

/// <summary>
/// 판 결과 자동 판정.
///
/// 패배: 맵(ORDR_S2_2.314) JASS의 패배 처리 myN이 GroupEnumUnitsOfPlayer로 그 플레이어의
/// 유닛을 열거하며 필터 mTN(=RemoveUnit)을 돌린다. 즉 패배하면 내 유닛이 전부 사라지고
/// 판은 계속 돌아가므로 풀에는 다른 소유자의 유닛이 남는다 — 이 조합이 패배 신호다.
/// (실측 확인: 패배 직후 로컬 0 · 타 소유 265)
///
/// 클리어: 유저 규칙 그대로 "65라운드까지 가서 패배하지 않았으면 클리어".
/// 라운드는 메모리에 옛 사본이 남아 최댓값만으로는 못 믿으므로, 세션을 충분히 일찍부터
/// 지켜본 경우에만 확정한다. 애매하면 unknown으로 남긴다 — 잘못된 라벨보다 없는 라벨이 낫다.
/// </summary>
public sealed class MatchOutcomeDetector(int requiredZeroScans = 2)
{
    /// <summary>패배로 볼 만큼 패를 가졌던 적이 있어야 한다(뽑기 전 0과 구분).</summary>
    private const int MinimumPeakUnits = 3;

    /// <summary>맵의 마지막 라운드. 여기 도달하고 패배하지 않았으면 클리어다.</summary>
    private const int FinalRound = 65;

    /// <summary>클리어로 인정하려면 이만큼의 라운드 진행을 직접 지켜봤어야 한다.</summary>
    private const int MinimumObservedProgress = 30;

    private int _peakLocalUnits;
    private int _consecutiveZeroScans;
    private int _baselineRound;
    private int _maxRound;
    private bool _defeated;

    public string Outcome
    {
        get
        {
            if (_defeated) return "fail";
            return _maxRound >= FinalRound && _maxRound - _baselineRound >= MinimumObservedProgress
                ? "clear"
                : "unknown";
        }
    }

    public string OutcomeSource => Outcome switch
    {
        "fail" => "unitWipe",
        "clear" => "roundProgress",
        _ => "none"
    };

    /// <summary>스캔 1회 관측. localUnits는 내 소유 유닛 수, foreignUnits는 풀의 나머지 유닛 수.</summary>
    public void Observe(int localUnits, int foreignUnits)
    {
        if (_defeated) return;
        _peakLocalUnits = Math.Max(_peakLocalUnits, localUnits);

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
        if (round <= 0) return;
        if (_baselineRound == 0) _baselineRound = round;
        _maxRound = Math.Max(_maxRound, round);
    }

    /// <summary>세션 경계에서 호출. 다음 판을 위해 상태를 비운다.</summary>
    public void Reset()
    {
        _defeated = false;
        _peakLocalUnits = 0;
        _consecutiveZeroScans = 0;
        _baselineRound = 0;
        _maxRound = 0;
    }
}
