using System.Text;

namespace OrandOverlay;

/// <summary>
/// 현재 라운드를 메모리에서 읽는다.
///
/// 맵이 타이머 제목을 "현재 라운드|r : N"으로 매 라운드 새로 만들기 때문에(JASS
/// TimerDialogSetTitle + I2S 연결) 그 조합 문자열을 찾아 숫자를 읽는다. 옛 라운드의
/// 사본이 힙에 남아 있으므로 최댓값을 현재 라운드로 본다 — 한 판 안에서 라운드는
/// 줄지 않는다. 이전 판의 잔여 값에 속지 않는 책임은 판정기(MatchOutcomeDetector)가
/// 기준선 비교로 맡는다.
///
/// 전체 힙 훑기라 비싸다. 호출 쪽에서 간격을 두고 부르는 것을 전제로 한다.
/// </summary>
public static class RoundReader
{
    private static readonly byte[] Marker = Encoding.UTF8.GetBytes("현재 라운드|r : ");
    private const int MaximumRound = 200;

    internal static int? TryReadCurrentRound(ReadOnlyProcessMemory memory, CancellationToken token)
    {
        var best = 0;
        var gate = new object();
        try
        {
            Parallel.ForEach(memory.ReadableRegions(),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount),
                    CancellationToken = token
                },
                () => 0,
                (region, _, local) =>
                {
                    foreach (var (_, buffer) in memory.ReadChunks([region]))
                        local = Math.Max(local, ScanBuffer(buffer));
                    return local;
                },
                local => { lock (gate) best = Math.Max(best, local); });
        }
        catch (OperationCanceledException) { return null; }
        return best > 0 ? best : null;
    }

    /// <summary>버퍼 하나에서 찾은 가장 큰 라운드 값(없으면 0). 테스트에서 직접 부른다.</summary>
    public static int ScanBuffer(byte[] buffer)
    {
        var best = 0;
        var limit = buffer.Length - Marker.Length - 1;
        for (var index = 0; index <= limit; index++)
        {
            if (buffer[index] != Marker[0]) continue;
            var matched = true;
            for (var offset = 1; offset < Marker.Length; offset++)
                if (buffer[index + offset] != Marker[offset]) { matched = false; break; }
            if (!matched) continue;

            var digits = index + Marker.Length;
            var value = 0;
            var read = 0;
            while (digits + read < buffer.Length && buffer[digits + read] is >= (byte)'0' and <= (byte)'9' && read < 3)
            {
                value = value * 10 + (buffer[digits + read] - '0');
                read++;
            }
            if (read > 0 && value <= MaximumRound) best = Math.Max(best, value);
            index = digits;
        }
        return best;
    }
}
