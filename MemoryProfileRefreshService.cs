using System.Net.Http;
using System.Text.Json;

namespace OrandOverlay;

/// <summary>
/// Warcraft 패치 대응용 메모리 프로필 갱신기.
/// 앱 업데이트와 분리해 GitHub의 최신 memory-profiles.json을 사용자 데이터 폴더에 캐시한다.
/// 네트워크/스키마/검증 실패 시 기존 사용자 캐시 또는 번들 프로필을 그대로 사용한다.
/// </summary>
public static class MemoryProfileRefreshService
{
    public const string ProfilesUrl =
        "https://raw.githubusercontent.com/AsmondKR/onepiece-random-defense-overlay/main/Data/memory-profiles.json";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private static string UserProfilePath =>
        Path.Combine(AppPaths.UserDataDirectory, "memory-profiles.json");

    private static string StampPath =>
        Path.Combine(AppPaths.UserDataDirectory, "memory-profiles.last-check");

    public static async Task TryRefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ShouldRefresh()) return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(RequestTimeout);
            using var client = new HttpClient { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OrandOverlay/1.0 (memory-profile-refresh)");
            var json = await client.GetStringAsync(ProfilesUrl, linked.Token).ConfigureAwait(false);
            if (!TryValidate(json)) return;

            Directory.CreateDirectory(AppPaths.UserDataDirectory);
            var temp = UserProfilePath + ".tmp";
            await File.WriteAllTextAsync(temp, json, linked.Token).ConfigureAwait(false);
            File.Move(temp, UserProfilePath, overwrite: true);
            File.WriteAllText(StampPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // 프로필 갱신은 보조 기능이다. 실패해도 번들/기존 캐시로 계속 실행한다.
        }
    }

    private static bool ShouldRefresh()
    {
        try
        {
            if (!File.Exists(StampPath)) return true;
            if (!DateTimeOffset.TryParse(File.ReadAllText(StampPath), out var checkedAt)) return true;
            return DateTimeOffset.UtcNow - checkedAt >= RefreshInterval;
        }
        catch
        {
            return true;
        }
    }

    internal static bool TryValidate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            var profiles = JsonSerializer.Deserialize<List<MemoryProfile>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
            if (profiles is null || profiles.Count == 0) return false;
            if (profiles.GroupBy(x => $"{x.FileVersion}|{x.ModuleName}", StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1)) return false;
            return profiles.All(profile => MemoryProfileValidator.Validate(profile).Count == 0 &&
                                           profile.Enabled && profile.Verified);
        }
        catch
        {
            return false;
        }
    }
}
