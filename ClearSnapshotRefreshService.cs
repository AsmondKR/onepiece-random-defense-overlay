using System.Net.Http;
using System.Text.Json;

namespace OrandOverlay;

/// <summary>
/// 클리어 통계 갱신을 티모지지가 아니라 이 프로젝트 저장소(GitHub) 스냅샷에서 받는다.
/// 티모지지 서버에 사용자 수만큼 부하를 만들지 않기 위해 클라이언트는 티모지지에
/// 접속하지 않고, 집계는 개발 쪽에서 한 번만 수행해 저장소에 커밋한다. 앱은 몇백
/// 바이트짜리 매니페스트로 새 스냅샷 여부만 확인하고, 새로울 때만 전체 스냅샷을
/// 내려받아 캐시에 병합한다. 실패하면 기존 번들+캐시 데이터로 조용히 후퇴한다.
/// </summary>
public sealed class ClearSnapshotRefreshService(Func<string, Task<string>>? fetcher = null)
{
    private const string RawBase =
        "https://raw.githubusercontent.com/AsmondKR/onepiece-random-defense-overlay/main/Data/";
    public const string ManifestUrl = RawBase + "clear-snapshot-manifest.json";
    public const string SnapshotUrl = RawBase + "tmo-clear-samples.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<string, Task<string>> _fetch = fetcher ?? FetchViaHttp;

    public static string CacheFile => Path.Combine(AppPaths.UserDataDirectory, "tmo-clear-cache.json");

    /// <summary>
    /// 저장소 스냅샷이 로컬 최신 표본보다 새로울 때만 전체를 받아 새 표본을 돌려준다.
    /// 어떤 예외도 밖으로 던지지 않고 빈 목록으로 후퇴한다.
    /// </summary>
    public async Task<IReadOnlyList<ClearSample>> FetchNewSamplesAsync(DateTimeOffset newerThan)
    {
        try
        {
            var newest = ParseManifest(await _fetch(ManifestUrl).ConfigureAwait(false));
            if (newest is null || newest <= newerThan) return [];
            var snapshot = ClearSampleDocument.Parse(await _fetch(SnapshotUrl).ConfigureAwait(false));
            return snapshot.Where(sample => sample.CreatedAt > newerThan).ToList();
        }
        catch
        {
            // 네트워크/스키마 문제로 갱신이 실패해도 추천은 기존 데이터로 계속된다.
            return [];
        }
    }

    /// <summary>매니페스트에서 스냅샷의 최신 표본 시각을 읽는다.</summary>
    public static DateTimeOffset? ParseManifest(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
            if (manifest?.NewestSampleAt is not { Length: > 0 } text) return null;
            return DateTimeOffset.TryParse(text, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>새 표본을 사용자 캐시에 병합한다. id가 겹치면 기존 항목을 유지한다.</summary>
    public static IReadOnlyList<ClearSample> MergeIntoCache(string cacheFile,
        IReadOnlyList<ClearSample> newSamples, string capturedAt)
    {
        var existing = new List<ClearSample>();
        if (File.Exists(cacheFile))
        {
            try
            {
                existing.AddRange(ClearSampleDocument.Parse(File.ReadAllText(cacheFile)));
            }
            catch
            {
                // 손상된 캐시는 무시하고 이번 갱신으로 다시 만든다.
            }
        }

        var merged = existing
            .Concat(newSamples)
            .GroupBy(sample => sample.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(sample => sample.CreatedAt)
            .ToList();
        if (merged.Count == existing.Count && newSamples.Count == 0) return merged;

        try
        {
            var temp = cacheFile + ".tmp";
            File.WriteAllText(temp, ClearSampleDocument.Serialize(merged, SnapshotUrl, capturedAt));
            File.Move(temp, cacheFile, overwrite: true);
        }
        catch
        {
            // 캐시 저장 실패는 이번 세션의 메모리 집계에 영향을 주지 않는다.
        }
        return merged;
    }

    private static async Task<string> FetchViaHttp(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrandOverlay/1.0 (read-only)");
        return await client.GetStringAsync(url).ConfigureAwait(false);
    }

    private sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string? NewestSampleAt { get; set; }
        public int SampleCount { get; set; }
    }
}
