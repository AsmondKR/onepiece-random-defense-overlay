using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OrandOverlay;

public sealed record UpdateInfo(Version Latest, string Tag, string DownloadUrl);

/// <summary>
/// GitHub Releases 기반 자동 업데이트. 시작 시 최신 릴리스를 확인하고, 단일 exe로
/// 실행 중일 때만 새 exe를 내려받아 교체·재시작한다. 다중 파일 배포(publish 폴더)로
/// 실행 중이면 릴리스 페이지만 연다. 확인 실패는 조용히 무시한다(오프라인 등).
/// </summary>
public sealed class UpdateService(Func<string, Task<string>>? fetcher = null,
    Func<string, Task<string?>>? redirectLocator = null)
{
    public const string LatestApiUrl =
        "https://api.github.com/repos/AsmondKR/onepiece-random-defense-overlay/releases/latest";
    public const string ReleasesPageUrl =
        "https://github.com/AsmondKR/onepiece-random-defense-overlay/releases/latest";
    public const string DownloadUrlPrefix =
        "https://github.com/AsmondKR/onepiece-random-defense-overlay/releases/download/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<string, Task<string>> _fetch = fetcher ?? FetchViaHttp;
    private readonly Func<string, Task<string?>> _locateRedirect = redirectLocator ?? LocateRedirectViaHttp;

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public async Task<UpdateInfo?> CheckAsync() =>
        (await CheckDetailedAsync().ConfigureAwait(false)).Update;

    /// <summary>수동 확인용: "업데이트 없음"과 "확인 실패(네트워크 등)"를 구분해 준다.</summary>
    public async Task<(UpdateInfo? Update, bool Failed)> CheckDetailedAsync()
    {
        // 1차: 릴리스 페이지의 302 리다이렉트에서 최신 태그를 읽는다. API(비인증 시간당
        // 60회/IP)와 달리 제한 부담이 없어 확인 주기를 짧게 잡아도 안전하다 — PC방처럼
        // 여러 유저가 한 IP를 공유하는 환경 포함. exe 주소는 태그로 규칙적으로 만든다.
        try
        {
            var location = await _locateRedirect(ReleasesPageUrl).ConfigureAwait(false);
            if (ParseRedirectLocation(location, CurrentVersion) is { } fromRedirect)
                return (fromRedirect, false);
            if (location is { Length: > 0 } &&
                location.Contains("/releases/tag/", StringComparison.OrdinalIgnoreCase))
                return (null, false); // 태그를 정상적으로 읽었고, 새 버전이 아니다.
        }
        catch
        {
            // 아래 API 폴백으로 넘어간다.
        }
        try
        {
            var json = await _fetch(LatestApiUrl).ConfigureAwait(false);
            return (ParseLatest(json, CurrentVersion), false);
        }
        catch
        {
            return (null, true);
        }
    }

    /// <summary>릴리스 페이지 리다이렉트의 Location URL에서 태그를 읽어 업데이트 정보를 만든다.</summary>
    public static UpdateInfo? ParseRedirectLocation(string? location, Version current)
    {
        const string marker = "/releases/tag/";
        if (location is not { Length: > 0 }) return null;
        var index = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var tag = Uri.UnescapeDataString(location[(index + marker.Length)..].Trim('/'));
        if (tag.Length == 0 || tag.Contains('/')) return null;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        var normalizedLatest = Normalize(latest);
        if (normalizedLatest <= Normalize(current)) return null;
        return new UpdateInfo(normalizedLatest, tag,
            DownloadUrlPrefix + Uri.EscapeDataString(tag) + "/OrandOverlay.exe");
    }

    private static async Task<string?> LocateRedirectViaHttp(string url)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrandOverlay-Updater");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        return response.Headers.Location?.ToString();
    }

    /// <summary>최신 릴리스 JSON을 해석해 현재 버전보다 높을 때만 업데이트 정보를 준다.</summary>
    public static UpdateInfo? ParseLatest(string json, Version current)
    {
        var release = JsonSerializer.Deserialize<LatestRelease>(json, JsonOptions);
        if (release?.TagName is not { Length: > 0 } tag) return null;
        var text = tag.TrimStart('v', 'V');
        if (!Version.TryParse(text, out var latest)) return null;
        // Version 비교는 미지정 필드(-1)를 0으로 정규화해 2.0 == 2.0.0으로 본다.
        var normalizedLatest = Normalize(latest);
        if (normalizedLatest <= Normalize(current)) return null;
        var asset = release.Assets?.FirstOrDefault(item =>
            item.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
        if (asset?.BrowserDownloadUrl is not { Length: > 0 } url) return null;
        return new UpdateInfo(normalizedLatest, tag, url);
    }

    private static Version Normalize(Version value) => new(
        Math.Max(0, value.Major), Math.Max(0, value.Minor),
        Math.Max(0, value.Build), Math.Max(0, value.Revision));

    /// <summary>단일 exe 실행(자기 자신 교체 가능) 여부.</summary>
    // 단일 exe도 실행 시 압축 해제 폴더(AppContext.BaseDirectory)에는 OrandOverlay.dll이
    // 풀려 있어, 거기서 dll을 찾으면 항상 "폴더 배포"로 오판해 자동 교체가 영영 막힌다.
    // 폴더 배포 여부는 "실행 파일 바로 옆"에 dll이 있는지로만 구분한다.
    public static bool CanSelfInstall =>
        Environment.ProcessPath is { Length: > 0 } path &&
        !File.Exists(Path.Combine(Path.GetDirectoryName(path) ?? "", "OrandOverlay.dll"));

    /// <summary>
    /// 새 exe를 옆에 내려받고, 앱 종료를 기다렸다가 교체·재시작하는 파워셸을 띄운 뒤
    /// 앱을 닫는다. 호출 전에 CanSelfInstall을 확인해야 한다.
    /// </summary>
    public async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        var exePath = Environment.ProcessPath!;
        var newPath = exePath + ".new";
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OrandOverlay-Updater");
            using var response = await client.GetAsync(update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var target = File.Create(newPath);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                copied += read;
                if (totalBytes > 0) progress?.Report((double)copied / totalBytes);
            }
        }

        // 교체 스크립트는 앱이 죽은 뒤에 돌아 실패해도 흔적이 없다 — 단계별로
        // update.log에 남겨, 재시작 실패 환경을 원격으로 진단할 수 있게 한다.
        var logPath = Path.Combine(AppPaths.UserDataDirectory, "update.log");
        var script = $$"""
            function Log($m) {
              try { Add-Content -LiteralPath '{{logPath}}' -Value ((Get-Date).ToString('HH:mm:ss') + ' ' + $m) } catch {}
            }
            Log 'begin {{update.Tag}}'
            $removed = $false
            for ($i = 0; $i -lt 120; $i++) {
              try { Remove-Item -LiteralPath '{{exePath}}' -ErrorAction Stop; $removed = $true; break }
              catch { Start-Sleep -Milliseconds 500 }
            }
            Log ('removed=' + $removed)
            try {
              Move-Item -LiteralPath '{{newPath}}' -Destination '{{exePath}}' -Force -ErrorAction Stop
              Log 'moved'
            }
            catch { Log ('move failed: ' + $_.Exception.Message) }
            $started = $false
            for ($i = 0; $i -lt 10; $i++) {
              try { Start-Process -FilePath '{{exePath}}' -ErrorAction Stop; $started = $true; break }
              catch { Start-Sleep -Seconds 1 }
            }
            Log ('started=' + $started)
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static void OpenReleasesPage() =>
        Process.Start(new ProcessStartInfo { FileName = ReleasesPageUrl, UseShellExecute = true });

    private static async Task<string> FetchViaHttp(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrandOverlay-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return await client.GetStringAsync(url).ConfigureAwait(false);
    }

    private sealed class LatestRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public List<ReleaseAsset>? Assets { get; set; }
    }

    private sealed class ReleaseAsset
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
