using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OrandOverlay;

/// <summary>
/// 텔레메트리 전송기. 원칙은 fail-silent: 어떤 실패도 예외로 새어 나가지 않고
/// 로컬 큐(최대 50판/30일)에 남겨 다음 기회에 재시도한다. 서버 400 응답이
/// 3회 쌓이면 그 레코드는 폐기한다(스키마 불일치 무한 재시도 방지).
/// </summary>
public sealed class TelemetryUploader
{
    public const string DefaultEndpoint = "TASK5_REPLACES_WITH_DEPLOYED_URL"; // Task 5에서 실제 workers.dev URL로 교체
    private const int MaxQueued = 50;
    private const int MaxAgeDays = 30;
    private const int MaxRejects = 3;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _endpoint;
    private readonly string _queueDirectory;

    public TelemetryUploader(string? endpoint = null, string? queueDirectory = null)
    {
        _endpoint = endpoint ?? DefaultEndpoint;
        _queueDirectory = queueDirectory
                          ?? Path.Combine(AppPaths.UserDataDirectory, "telemetry", "pending");
        try { Directory.CreateDirectory(_queueDirectory); } catch { /* fail-silent */ }
    }

    public int PendingCount
    {
        get { try { return Directory.GetFiles(_queueDirectory, "*.json").Length; } catch { return 0; } }
    }

    public async Task EnqueueAndFlushAsync(TelemetryRecord record)
    {
        try
        {
            var path = Path.Combine(_queueDirectory, record.RecordId + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record));
            TrimQueue();
            await FlushPendingAsync();
        }
        catch { /* fail-silent */ }
    }

    public async Task FlushPendingAsync()
    {
        try
        {
            foreach (var path in Directory.GetFiles(_queueDirectory, "*.json")
                         .OrderBy(File.GetCreationTimeUtc))
            {
                var payload = await File.ReadAllTextAsync(path);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try { response = await Http.PostAsync(_endpoint, content); }
                catch { return; } // 네트워크 불가 — 다음 기회에
                using (response)
                {
                    if (response.IsSuccessStatusCode) { Delete(path); continue; }
                    if ((int)response.StatusCode == 400 && CountReject(path) >= MaxRejects) { Delete(path); continue; }
                    if ((int)response.StatusCode is 429 or >= 500) return; // 서버 사정 — 나중에
                }
            }
        }
        catch { /* fail-silent */ }
    }

    /// <summary>큐 상한 유지: 30일 지난 것과 50판 초과분(오래된 것부터)을 버린다.</summary>
    public void TrimQueue()
    {
        try
        {
            var files = Directory.GetFiles(_queueDirectory, "*.json")
                .OrderBy(File.GetCreationTimeUtc).ToList();
            foreach (var path in files.Where(p =>
                         DateTime.UtcNow - File.GetCreationTimeUtc(p) > TimeSpan.FromDays(MaxAgeDays)))
                Delete(path);
            files = Directory.GetFiles(_queueDirectory, "*.json").OrderBy(File.GetCreationTimeUtc).ToList();
            foreach (var path in files.Take(Math.Max(0, files.Count - MaxQueued)))
                Delete(path);
        }
        catch { /* fail-silent */ }
    }

    private static int CountReject(string path)
    {
        var marker = path + ".retry";
        var count = 1;
        try
        {
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out var prior)) count = prior + 1;
            File.WriteAllText(marker, count.ToString());
        }
        catch { /* fail-silent */ }
        return count;
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch { /* fail-silent */ }
        try { File.Delete(path + ".retry"); } catch { /* fail-silent */ }
    }
}
