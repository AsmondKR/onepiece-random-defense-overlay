namespace OrandOverlay;

/// <summary>
/// 한 판의 마지막 비어 있지 않은 패를 붙잡아 둔다. 패배 시 인벤토리가 먼저 비고,
/// 클리어는 정산 확인 뒤에야 라벨이 붙으므로, 전송 시점의 현재 패를 읽으면 0건이 된다.
/// 클리어·패배 확정 또는 세션 종료 때 판당 1회만 레코드를 만든다.
/// </summary>
public sealed class MatchTelemetryBuffer
{
    private List<InventoryEntry> _hand = [];
    private List<string> _completed = [];
    private List<string> _recommendations = [];
    private DateTimeOffset _sessionStartedAt;
    private int _lastObservedUnitCount;
    private bool _hasSnapshot;
    private bool _sent;

    public bool HasSnapshot => _hasSnapshot;
    public bool Sent => _sent;

    /// <summary>패가 하나라도 있으면 이번 판의 마지막 스냅샷으로 갱신한다. 빈 관측은 무시.</summary>
    public void Capture(IEnumerable<InventoryEntry> hand, IEnumerable<string> completedTops,
        IEnumerable<string> topRecommendations, DateTimeOffset sessionStartedAt, int observedUnitCount)
    {
        if (_sent) return;
        var snapshot = hand.Where(entry => entry.Count > 0)
            .Select(entry => new InventoryEntry { UnitId = entry.UnitId, Count = entry.Count })
            .ToList();
        if (snapshot.Count == 0) return;
        _hand = snapshot;
        _completed = completedTops.ToList();
        _recommendations = topRecommendations.Take(5).ToList();
        _sessionStartedAt = sessionStartedAt;
        _lastObservedUnitCount = observedUnitCount > 0 ? observedUnitCount : snapshot.Sum(x => x.Count);
        _hasSnapshot = true;
    }

    /// <summary>이미 보냈거나 스냅샷이 없으면 null. 호출 성공 시 이 판은 다시 만들지 않는다.</summary>
    public TelemetryRecord? TryEmit(string anonId, string appVersion, string mapVersion,
        string warcraftVersion, string goalUnitId, string navigationMode, string goroseiMode,
        string buildVariant, DateTimeOffset endedAt, string outcome, string outcomeSource)
    {
        if (_sent || !_hasSnapshot) return null;
        _sent = true;
        return MatchTelemetryRecorder.Build(
            anonId, appVersion, mapVersion, warcraftVersion,
            goalUnitId, navigationMode, goroseiMode, buildVariant,
            _hand, _completed, _recommendations,
            _sessionStartedAt, endedAt, _lastObservedUnitCount,
            outcome, outcomeSource);
    }

    /// <summary>세션 경계에서 다음 판을 위해 비운다.</summary>
    public void Reset()
    {
        _hand = [];
        _completed = [];
        _recommendations = [];
        _sessionStartedAt = default;
        _lastObservedUnitCount = 0;
        _hasSnapshot = false;
        _sent = false;
    }
}
