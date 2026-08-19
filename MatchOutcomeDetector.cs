namespace OrandOverlay;

/// <summary>
/// 판 결과(패배) 자동 판정.
///
/// 근거: 맵(ORDR_S2_2.314) JASS의 패배 처리 myN이 GroupEnumUnitsOfPlayer로 그 플레이어의
/// 유닛을 열거하며 필터 mTN(=RemoveUnit)을 돌린다. 즉 패배하면 내 유닛이 전부 사라진다.
/// 판 자체는 계속 돌아가므로 풀에는 다른 소유자의 유닛이 남는다 — 이 조합이 패배 신호다.
///
/// 클리어는 유닛을 건드리지 않아 이 방식으로 잡히지 않는다(별도 확인 필요).
/// 그래서 이 판정기는 fail만 확정하고 나머지는 unknown으로 남긴다 — 잘못된 라벨보다 없는
/// 라벨이 낫다는 원칙.
/// </summary>
public sealed class MatchOutcomeDetector(int requiredZeroScans = 2)
{
    /// <summary>패배로 볼 만큼 패를 가졌던 적이 있어야 한다(뽑기 전 0과 구분).</summary>
    private const int MinimumPeakUnits = 3;

    private int _peakLocalUnits;
    private int _consecutiveZeroScans;

    public string Outcome { get; private set; } = "unknown";
    public string OutcomeSource => Outcome == "unknown" ? "none" : "unitWipe";

    /// <summary>스캔 1회 관측. localUnits는 내 소유 유닛 수, foreignUnits는 풀의 나머지 유닛 수.</summary>
    public void Observe(int localUnits, int foreignUnits)
    {
        if (Outcome != "unknown") return;
        _peakLocalUnits = Math.Max(_peakLocalUnits, localUnits);

        // 풀에 남의 유닛도 없으면 판이 끝났거나 읽기가 흔들린 것 — 패배로 단정하지 않는다.
        if (localUnits > 0 || foreignUnits <= 0 || _peakLocalUnits < MinimumPeakUnits)
        {
            _consecutiveZeroScans = 0;
            return;
        }

        if (++_consecutiveZeroScans >= requiredZeroScans) Outcome = "fail";
    }

    /// <summary>세션 경계에서 호출. 다음 판을 위해 상태를 비운다.</summary>
    public void Reset()
    {
        Outcome = "unknown";
        _peakLocalUnits = 0;
        _consecutiveZeroScans = 0;
    }
}
