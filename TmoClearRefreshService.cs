using System.Net.Http;
using System.Text.Json;

namespace OrandOverlay;

/// <summary>
/// 앱 시작 시 한 번, 번들 스냅샷 이후의 신규 클리어 기록만 증분 수신해 사용자 캐시에 병합한다.
/// 읽기 전용 HTTP GET만 사용하고, 실패하면 기존 번들+캐시 데이터로 조용히 후퇴한다(fail-closed).
/// </summary>
public sealed class TmoClearRefreshService(Func<string, Task<string>>? fetcher = null)
{
    public const string ApiUrl = "https://tmo.gg/_api/ranks/ordr2/clears";
    private const int PageLimit = 50;
    private const int MaximumPages = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<string, Task<string>> _fetch = fetcher ?? FetchViaHttp;

    public static string CacheFile => Path.Combine(AppPaths.UserDataDirectory, "tmo-clear-cache.json");

    /// <summary>
    /// newerThan 이후의 기록을 최대 30페이지 수신한다. 결과는 새 표본 목록이며,
    /// 어떤 예외도 밖으로 던지지 않고 빈 목록으로 후퇴한다.
    /// </summary>
    public async Task<IReadOnlyList<ClearSample>> FetchNewSamplesAsync(DateTimeOffset newerThan)
    {
        var results = new List<ClearSample>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            for (var page = 0; page < MaximumPages; page++)
            {
                var url = $"{ApiUrl}?limit={PageLimit}" +
                          (cursor is null ? "" : $"&next={Uri.EscapeDataString(cursor)}");
                var json = await _fetch(url).ConfigureAwait(false);
                var parsed = ParseLivePage(json);
                if (parsed is null || parsed.Value.Samples.Count == 0) break;

                var reachedBoundary = false;
                foreach (var sample in parsed.Value.Samples)
                {
                    if (sample.CreatedAt <= newerThan) { reachedBoundary = true; break; }
                    if (seen.Add(sample.Id)) results.Add(sample);
                }
                if (reachedBoundary) break;
                if (string.IsNullOrWhiteSpace(parsed.Value.NextCursor) ||
                    parsed.Value.NextCursor == cursor) break;
                cursor = parsed.Value.NextCursor;
            }
        }
        catch
        {
            // 네트워크/스키마 문제로 실시간 갱신이 실패해도 추천은 기존 데이터로 계속된다.
            return [];
        }
        return results;
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
            File.WriteAllText(temp, ClearSampleDocument.Serialize(merged, ApiUrl, capturedAt));
            File.Move(temp, cacheFile, overwrite: true);
        }
        catch
        {
            // 캐시 저장 실패는 이번 세션의 메모리 집계에 영향을 주지 않는다.
        }
        return merged;
    }

    /// <summary>라이브 API 페이지를 표본으로 해석한다. 필수 키가 빠진 레코드는 건너뛴다.</summary>
    public static (IReadOnlyList<ClearSample> Samples, string? NextCursor)? ParseLivePage(string json)
    {
        LivePage? page;
        try
        {
            page = JsonSerializer.Deserialize<LivePage>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
        if (page?.Clears is null) return null;

        var samples = new List<ClearSample>(page.Clears.Count);
        foreach (var clear in page.Clears)
        {
            if (string.IsNullOrWhiteSpace(clear.Id) ||
                string.IsNullOrWhiteSpace(clear.Difficulty)) continue;
            if (!DateTimeOffset.TryParse(clear.CreatedAt, out var createdAt)) continue;
            var units = (clear.Units ?? [])
                .Where(unit => !string.IsNullOrWhiteSpace(unit.Code))
                .Select(unit => new ClearSampleUnit(unit.Code!, Math.Max(1, unit.Count),
                    unit.Grade ?? ""))
                .ToList();
            samples.Add(new ClearSample(clear.Id, createdAt, clear.Difficulty, clear.UnitCount, units));
        }
        return (samples, page.NextCursor);
    }

    private static async Task<string> FetchViaHttp(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrandOverlay/1.0 (read-only)");
        return await client.GetStringAsync(url).ConfigureAwait(false);
    }

    private sealed class LivePage
    {
        public List<LiveClear>? Clears { get; set; }
        public string? NextCursor { get; set; }
    }

    private sealed class LiveClear
    {
        public string Id { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string Difficulty { get; set; } = "";
        public int UnitCount { get; set; }
        public List<LiveUnit>? Units { get; set; }
    }

    private sealed class LiveUnit
    {
        public string? Code { get; set; }
        public int Count { get; set; }
        public string? Grade { get; set; }
    }
}
