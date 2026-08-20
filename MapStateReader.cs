using System.Text;

namespace OrandOverlay;

/// <summary>한 번의 힙 훑기에서 읽은 맵 상태.</summary>
/// <param name="MaxRound">발견한 가장 큰 "현재 라운드" 값(없으면 0). 한 판 안에서 라운드는 줄지 않는다.</param>
/// <param name="SettlementCopies">65라운드 정산 문자열("마지막 라운드 유닛 점수 : N점") 사본 수.</param>
/// <param name="Difficulty">런타임에 조합된 난이도 이름. 못 읽으면 unknown.</param>
public readonly record struct MapStateSample(int MaxRound, int SettlementCopies, string Difficulty);

/// <summary>
/// 맵이 스스로 내리는 판정을 메모리에서 읽는다.
///
/// 원칙: 스크립트의 고정 문구는 맵 로드 때부터 메모리에 있어서 존재 여부가 신호가 못 된다
/// (실측: "엔딩 : 게임 종료"·"클리어하셨습니다"가 게임 도중에도 발견됨). 그래서 숫자가
/// 붙어 런타임에 조합된 문자열만 센다 — war3map.j 원문에는 마커 뒤에 따옴표가 오므로
/// "마커 + 숫자" 요구가 원문·고정 문구를 자연히 걸러낸다.
///
/// 읽는 것:
/// 1) 현재 라운드 — 타이머 제목 "현재 라운드|r : N" / "현재 라운드 : |rN" (맵에 두 표기가 공존).
/// 2) 65라운드 정산 — "마지막 라운드 유닛 점수 : |cffffd700N점". 스크립트에서 유일하게
///    BQN==65 블록이 생존자에게만 조합한다. 이 문자열의 등장이 곧 맵의 최종 정산이다.
/// 3) 난이도 — 멀티보드 제목 `2.314[R]|r ` + 색코드 이름, 또는 정산 줄 `난이도 : ` + 색코드 이름.
///    색코드 이름 단독은 스크립트 원문에도 있어서 마커 뒤에 붙었을 때만 인정한다.
///
/// 옛 판의 사본이 힙에 남는 문제는 판정기(MatchOutcomeDetector)가 기준선 비교로 맡는다.
/// 전체 힙 훑기라 비싸다 — 호출 쪽에서 간격을 두고 부른다.
/// </summary>
public static class MapStateReader
{
    private static readonly byte[] RoundMarkerA = Encoding.UTF8.GetBytes("현재 라운드|r : ");
    private static readonly byte[] RoundMarkerB = Encoding.UTF8.GetBytes("현재 라운드 : |r");
    private static readonly byte[] SettlementMarker = Encoding.UTF8.GetBytes("마지막 라운드 유닛 점수 : |cffffd700");
    private static readonly byte[] SettlementSuffix = Encoding.UTF8.GetBytes("점");
    private static readonly byte[] DifficultyBoardMarker = Encoding.UTF8.GetBytes("[R]|r ");
    private static readonly byte[] DifficultyLineMarker = Encoding.UTF8.GetBytes("난이도 : ");
    private static readonly (byte[] Token, string Name)[] DifficultyTokens =
    [
        (Encoding.UTF8.GetBytes("|cff00bfff쉬움|r"), "쉬움"),
        (Encoding.UTF8.GetBytes("|cffee82ee보통|r"), "보통"),
        (Encoding.UTF8.GetBytes("|cffff0000어려움|r"), "어려움"),
        (Encoding.UTF8.GetBytes("|cff9400d3지옥|r"), "지옥"),
        (Encoding.UTF8.GetBytes("|cffffd700신|r"), "신"),
        (Encoding.UTF8.GetBytes("|c00cc3337악몽|r"), "악몽"),
    ];
    private static readonly string[] DifficultyRank = ["악몽", "신", "지옥", "어려움", "보통", "쉬움"];
    private const int MaximumRound = 200;

    internal static MapStateSample? TryRead(ReadOnlyProcessMemory memory, CancellationToken token)
    {
        var bestRound = 0;
        var settlements = 0;
        var difficulty = "unknown";
        var gate = new object();
        try
        {
            Parallel.ForEach(memory.ReadableRegions(),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount),
                    CancellationToken = token
                },
                () => new MapStateSample(0, 0, "unknown"),
                (region, _, local) =>
                {
                    foreach (var (_, buffer) in memory.ReadChunks([region]))
                    {
                        var sample = ScanBuffer(buffer);
                        local = new MapStateSample(Math.Max(local.MaxRound, sample.MaxRound),
                            local.SettlementCopies + sample.SettlementCopies,
                            CombineDifficulty(local.Difficulty, sample.Difficulty));
                    }
                    return local;
                },
                local =>
                {
                    lock (gate)
                    {
                        bestRound = Math.Max(bestRound, local.MaxRound);
                        settlements += local.SettlementCopies;
                        difficulty = CombineDifficulty(difficulty, local.Difficulty);
                    }
                });
        }
        catch (OperationCanceledException) { return null; }
        return new MapStateSample(bestRound, settlements, difficulty);
    }

    /// <summary>버퍼 하나를 훑는다. 테스트에서 직접 부른다.</summary>
    public static MapStateSample ScanBuffer(byte[] buffer)
    {
        var round = Math.Max(ScanRound(buffer, RoundMarkerA), ScanRound(buffer, RoundMarkerB));
        return new MapStateSample(round, CountSettlements(buffer), ScanDifficulty(buffer));
    }

    private static string ScanDifficulty(byte[] buffer)
    {
        var fromBoard = MatchDifficultyAfter(buffer, DifficultyBoardMarker);
        var fromLine = MatchDifficultyAfter(buffer, DifficultyLineMarker);
        return CombineDifficulty(fromBoard, fromLine);
    }

    private static string MatchDifficultyAfter(byte[] buffer, byte[] marker)
    {
        var found = "unknown";
        foreach (var end in MarkerEnds(buffer, marker))
        {
            foreach (var (token, name) in DifficultyTokens)
            {
                if (end + token.Length > buffer.Length) continue;
                var matched = true;
                for (var offset = 0; offset < token.Length; offset++)
                    if (buffer[end + offset] != token[offset]) { matched = false; break; }
                if (!matched) continue;
                found = CombineDifficulty(found, name);
            }
        }
        return found;
    }

    private static string CombineDifficulty(string left, string right)
    {
        if (left is "" or "unknown") return string.IsNullOrEmpty(right) ? "unknown" : right;
        if (right is "" or "unknown" || left == right) return left;
        var leftRank = Array.IndexOf(DifficultyRank, left);
        var rightRank = Array.IndexOf(DifficultyRank, right);
        if (leftRank < 0) return right;
        if (rightRank < 0) return left;
        return leftRank <= rightRank ? left : right;
    }

    private static int ScanRound(byte[] buffer, byte[] marker)
    {
        var best = 0;
        foreach (var digits in MarkerEnds(buffer, marker))
        {
            var (value, read) = ReadDigits(buffer, digits, 3);
            if (read > 0 && value <= MaximumRound) best = Math.Max(best, value);
        }
        return best;
    }

    private static int CountSettlements(byte[] buffer)
    {
        var count = 0;
        foreach (var digits in MarkerEnds(buffer, SettlementMarker))
        {
            var (_, read) = ReadDigits(buffer, digits, 6);
            if (read == 0) continue;
            var suffix = digits + read;
            if (suffix + SettlementSuffix.Length > buffer.Length) continue;
            var matched = true;
            for (var offset = 0; offset < SettlementSuffix.Length; offset++)
                if (buffer[suffix + offset] != SettlementSuffix[offset]) { matched = false; break; }
            if (matched) count++;
        }
        return count;
    }

    /// <summary>버퍼에서 마커가 끝나는 위치들을 차례로 돌려준다.</summary>
    private static IEnumerable<int> MarkerEnds(byte[] buffer, byte[] marker)
    {
        var limit = buffer.Length - marker.Length - 1;
        for (var index = 0; index <= limit; index++)
        {
            if (buffer[index] != marker[0]) continue;
            var matched = true;
            for (var offset = 1; offset < marker.Length; offset++)
                if (buffer[index + offset] != marker[offset]) { matched = false; break; }
            if (!matched) continue;
            yield return index + marker.Length;
            index += marker.Length - 1;
        }
    }

    private static (int Value, int Read) ReadDigits(byte[] buffer, int start, int maxDigits)
    {
        var value = 0;
        var read = 0;
        while (start + read < buffer.Length && buffer[start + read] is >= (byte)'0' and <= (byte)'9'
               && read < maxDigits)
        {
            value = value * 10 + (buffer[start + read] - '0');
            read++;
        }
        return (value, read);
    }
}
