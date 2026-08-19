# 라이브 플레이 데이터 파이프라인 (M1+M2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 오버레이가 판 종료 시 익명 플레이 레코드를 Cloudflare Worker로 보내고, 개발 PC 수집기가 이를 집계해 `Data/orand-live-stats.json`으로 배포하며, 추천 근거에 실사용 표본을 표기한다.

**Architecture:** Worker는 검증+저장만 하는 우체통(D1). 클라이언트는 세션 경계에서 레코드를 만들어 fail-silent 업로드(실패 시 로컬 큐 재시도). 집계·배포는 기존 수집기와 같은 방식(로컬 파이썬 → git 커밋 → 앱의 기존 데이터 갱신 경로).

**Tech Stack:** C# .NET 8 WPF(클라), Cloudflare Worker + D1(plain JS, wrangler), Python 3.12(수집기), node:test(Worker 검증 함수 테스트), 기존 SmokeTests(클라 테스트).

**Spec:** `docs/superpowers/specs/2026-08-19-live-telemetry-pipeline-design.md`

## Global Constraints

- 수집은 **내 슬롯 데이터만**. 닉네임·계정·IP·채팅·타인 패 금지. 익명 UUID만.
- 클라이언트는 **tmo.gg에 접속하지 않는다** (기존 합의 유지).
- 업로드는 **fail-silent**: 어떤 실패도 사용자에게 표시하지 않고 게임에 지장 없음. 업로드 타임아웃 5초.
- 레코드 크기 상한 **4KB**, 판당 1개.
- 옵트아웃 체크박스 기본 **켬**, 끄면 생성·전송 0건.
- 로컬 큐 상한: **50판 또는 30일**. 서버 400 응답 3회면 해당 레코드 폐기.
- Worker 응답: 성공 204 / 검증 실패 400 / 레이트리밋 429 / 저장 실패 507.
- `work/` 하위(Worker 프로젝트·수집기·원본 레코드)는 공개 저장소에 올리지 않는다.
- 커밋 메시지는 담백하게(발표 톤 금지), 끝에 `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- 앱 실행 중이면 bin이 잠기므로 빌드는 `-p:BaseOutputPath=bin-verify/`, 빌드 전 `Stop-Process -Name OrandOverlay`.
- 스모크 실행: `dotnet run --project SmokeTests/OrandOverlay.SmokeTests.csproj -c Debug` (전부 통과해야 함). 어설션을 추가/제거하면 마지막 `PASS: ... N/N` 라벨을 실제 `OK:` 줄 수로 갱신한다.

---

### Task 1: 텔레메트리 레코드 모델 + 빌더

**Files:**
- Create: `outputs/OrandOverlay/TelemetryRecord.cs`
- Modify: `outputs/OrandOverlay/SmokeTests/Program.cs` (마지막 `Console.WriteLine("PASS: ...")` 직전에 어설션 블록 추가)

**Interfaces:**
- Produces: `TelemetryRecord`(camelCase JSON DTO), `TelemetryHandEntry`, `MatchTelemetryRecorder.Build(...)` — Task 3이 호출, Task 4 Worker·Task 6 수집기가 같은 스키마를 파싱.

- [ ] **Step 1: 실패하는 테스트 작성** — SmokeTests에 추가:

```csharp
// 텔레메트리 레코드: 판 종료 시 서버로 보내는 익명 플레이 기록.
{
    var record = MatchTelemetryRecorder.Build(
        anonId: "11111111-1111-1111-1111-111111111111",
        appVersion: "0.6.0", mapVersion: "2.314", warcraftVersion: "2.0.4.23745",
        goalUnitId: "yamato_transcendent", navigationMode: "PathOfKings.BountyHunter",
        goroseiMode: "Nasjuro", buildVariant: "auto",
        finalHand: new List<InventoryEntry>
        {
            new() { UnitId = "luffy_common", Count = 2 },
            new() { UnitId = "rawcode:200h", Count = 1 },
        },
        completedTops: new List<string> { "yamato_transcendent" },
        topRecommendations: new List<string> { "yamato_transcendent", "rawcode:E90H" },
        sessionStartedAt: new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero),
        sessionEndedAt: new DateTimeOffset(2026, 8, 19, 10, 40, 0, TimeSpan.Zero),
        lastObservedUnitCount: 3);
    Assert(record.SchemaVersion == 1 && record.Outcome == "unknown" && record.OutcomeSource == "none",
        "텔레메트리: v1 레코드는 라벨 unknown/none으로 생성");
    Assert(record.RecordId != Guid.Empty.ToString() && record.AnonId.StartsWith("1111"),
        "텔레메트리: recordId 생성·anonId 보존");
    Assert(record.FinalHand.Count == 2 && record.FinalHand[0].Count == 2,
        "텔레메트리: 최종 패 rawcode+수량 보존");
    var telemetryJson = System.Text.Json.JsonSerializer.Serialize(record);
    Assert(telemetryJson.Contains("\"schemaVersion\":1") && telemetryJson.Contains("\"outcome\":\"unknown\""),
        "텔레메트리: camelCase JSON 직렬화");
    Assert(System.Text.Encoding.UTF8.GetByteCount(telemetryJson) < 4096,
        "텔레메트리: 일반 레코드가 4KB 상한 안");
    Assert(!telemetryJson.Contains("nickname") && !telemetryJson.Contains("battletag"),
        "텔레메트리: 개인정보 필드 자체가 없음");
}
```

- [ ] **Step 2: 실패 확인** — `dotnet run --project SmokeTests/OrandOverlay.SmokeTests.csproj -c Debug` → `MatchTelemetryRecorder` 미정의 컴파일 오류가 정상.

- [ ] **Step 3: 최소 구현** — `TelemetryRecord.cs`:

```csharp
using System.Text.Json.Serialization;

namespace OrandOverlay;

/// <summary>
/// 판 종료 시 서버로 보내는 익명 플레이 레코드(스키마 v1).
/// 내 슬롯 데이터만 담는다. 닉네임·계정·IP는 어떤 필드로도 존재하지 않는다.
/// 승패 라벨은 판정 프로브가 생기기 전까지 unknown으로 보낸다(설계 문서 참조).
/// </summary>
public sealed class TelemetryRecord
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("recordId")] public string RecordId { get; init; } = "";
    [JsonPropertyName("anonId")] public string AnonId { get; init; } = "";
    [JsonPropertyName("capturedAt")] public string CapturedAt { get; init; } = "";
    [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";
    [JsonPropertyName("mapVersion")] public string MapVersion { get; init; } = "";
    [JsonPropertyName("warcraftVersion")] public string WarcraftVersion { get; init; } = "";
    [JsonPropertyName("goalUnitId")] public string GoalUnitId { get; init; } = "";
    [JsonPropertyName("navigationMode")] public string NavigationMode { get; init; } = "";
    [JsonPropertyName("goroseiMode")] public string GoroseiMode { get; init; } = "";
    [JsonPropertyName("buildVariant")] public string BuildVariant { get; init; } = "";
    [JsonPropertyName("finalHand")] public List<TelemetryHandEntry> FinalHand { get; init; } = [];
    [JsonPropertyName("completedTops")] public List<string> CompletedTops { get; init; } = [];
    [JsonPropertyName("topRecommendations")] public List<string> TopRecommendations { get; init; } = [];
    [JsonPropertyName("sessionStartedAt")] public string SessionStartedAt { get; init; } = "";
    [JsonPropertyName("sessionEndedAt")] public string SessionEndedAt { get; init; } = "";
    [JsonPropertyName("lastObservedUnitCount")] public int LastObservedUnitCount { get; init; }
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "unknown";
    [JsonPropertyName("outcomeSource")] public string OutcomeSource { get; init; } = "none";
}

public sealed class TelemetryHandEntry
{
    [JsonPropertyName("unitId")] public string UnitId { get; init; } = "";
    [JsonPropertyName("count")] public int Count { get; init; }
}

public static class MatchTelemetryRecorder
{
    /// <summary>세션 종료 시점 상태로 레코드를 만든다. 순수 함수 — I/O 없음.</summary>
    public static TelemetryRecord Build(string anonId, string appVersion, string mapVersion,
        string warcraftVersion, string goalUnitId, string navigationMode, string goroseiMode,
        string buildVariant, IReadOnlyList<InventoryEntry> finalHand,
        IReadOnlyList<string> completedTops, IReadOnlyList<string> topRecommendations,
        DateTimeOffset sessionStartedAt, DateTimeOffset sessionEndedAt, int lastObservedUnitCount) => new()
    {
        RecordId = Guid.NewGuid().ToString(),
        AnonId = anonId,
        CapturedAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("o"),
        AppVersion = appVersion,
        MapVersion = mapVersion,
        WarcraftVersion = warcraftVersion,
        GoalUnitId = goalUnitId,
        NavigationMode = navigationMode,
        GoroseiMode = goroseiMode,
        BuildVariant = buildVariant,
        FinalHand = finalHand.Where(x => x.Count > 0)
            .Select(x => new TelemetryHandEntry { UnitId = x.UnitId, Count = x.Count }).ToList(),
        CompletedTops = completedTops.ToList(),
        TopRecommendations = topRecommendations.Take(5).ToList(),
        SessionStartedAt = sessionStartedAt.UtcDateTime.ToString("o"),
        SessionEndedAt = sessionEndedAt.UtcDateTime.ToString("o"),
        LastObservedUnitCount = lastObservedUnitCount,
    };
}
```

- [ ] **Step 4: 통과 확인** — 스모크 전체 그린 + PASS 카운트 라벨 갱신.

- [ ] **Step 5: 커밋**

```bash
git add TelemetryRecord.cs SmokeTests/Program.cs
git commit -m "텔레메트리 레코드 모델과 빌더 추가"
```

---

### Task 2: 로컬 큐 + 업로더

**Files:**
- Create: `outputs/OrandOverlay/TelemetryUploader.cs`
- Modify: `outputs/OrandOverlay/SmokeTests/Program.cs`

**Interfaces:**
- Consumes: `TelemetryRecord`(Task 1).
- Produces: `TelemetryUploader(string? endpoint = null, string? queueDirectory = null)` — `Task EnqueueAndFlushAsync(TelemetryRecord record)`, `Task FlushPendingAsync()`, `void TrimQueue()`, `int PendingCount`, `const string DefaultEndpoint`. Task 3이 사용. 큐 파일: `{recordId}.json` + 거절 카운터 `{recordId}.json.retry`.

- [ ] **Step 1: 실패하는 테스트 작성** — SmokeTests에 추가:

```csharp
// 텔레메트리 업로더: fail-silent 큐. 서버 없이도 게임에 지장이 없어야 한다.
{
    var queueDir = Path.Combine(Path.GetTempPath(), "orand-telemetry-" + Guid.NewGuid().ToString("N"));
    // 닫힌 로컬 포트 → 즉시 연결 실패 → 큐에 남아야 한다.
    var uploader = new TelemetryUploader("http://127.0.0.1:9/v1/records", queueDir);
    var record = MatchTelemetryRecorder.Build(
        "22222222-2222-2222-2222-222222222222", "0.6.0", "2.314", "2.0.4.23745",
        "yamato_transcendent", "PathOfKings.BountyHunter", "None", "auto",
        new List<InventoryEntry> { new() { UnitId = "luffy_common", Count = 1 } },
        new List<string>(), new List<string> { "yamato_transcendent" },
        DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow, 1);
    uploader.EnqueueAndFlushAsync(record).GetAwaiter().GetResult();
    Assert(uploader.PendingCount == 1, "텔레메트리 업로더: 전송 실패 시 큐에 보관");
    uploader.FlushPendingAsync().GetAwaiter().GetResult();
    Assert(uploader.PendingCount == 1, "텔레메트리 업로더: 재시도 실패해도 레코드 유지(네트워크 오류)");

    for (var i = 0; i < 55; i++)
        File.WriteAllText(Path.Combine(queueDir, $"{Guid.NewGuid()}.json"), "{}");
    uploader.TrimQueue();
    Assert(Directory.GetFiles(queueDir, "*.json").Length <= 50, "텔레메트리 업로더: 큐 50판 상한");
    Directory.Delete(queueDir, true);
}
```

- [ ] **Step 2: 실패 확인** — 컴파일 실패(`TelemetryUploader` 미정의).

- [ ] **Step 3: 최소 구현** — `TelemetryUploader.cs`:

```csharp
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
        Directory.CreateDirectory(_queueDirectory);
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
                if (response.IsSuccessStatusCode) { Delete(path); continue; }
                if ((int)response.StatusCode == 400 && CountReject(path) >= MaxRejects) { Delete(path); continue; }
                if ((int)response.StatusCode is 429 or >= 500) return; // 서버 사정 — 나중에
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

    private int CountReject(string path)
    {
        var marker = path + ".retry";
        var count = 1;
        try
        {
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out var prior)) count = prior + 1;
            File.WriteAllText(marker, count.ToString());
        }
        catch { }
        return count;
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + ".retry"); } catch { }
    }
}
```

- [ ] **Step 4: 통과 확인** — 스모크 그린(카운트 라벨 갱신 포함).

- [ ] **Step 5: 커밋** — `git add TelemetryUploader.cs SmokeTests/Program.cs && git commit -m "텔레메트리 로컬 큐·업로더 추가"`

---

### Task 3: 설정 + 세션 경계 훅

**Files:**
- Modify: `outputs/OrandOverlay/Models.cs` (AppSettings 2필드)
- Modify: `outputs/OrandOverlay/SettingsStore.cs` (EnsureTelemetryAnonId)
- Modify: `outputs/OrandOverlay/MainWindow.xaml` (`ClearDataRefreshCheck` 아래 체크박스)
- Modify: `outputs/OrandOverlay/MainWindow.xaml.cs` (세션 추적 + 종료 시 전송)
- Modify: `outputs/OrandOverlay/CompletedTopUnitTracker.cs` (완료 목록 읽기 프로퍼티 — 없을 때만)
- Modify: `outputs/OrandOverlay/SmokeTests/Program.cs`

**Interfaces:**
- Consumes: `MatchTelemetryRecorder.Build`, `TelemetryUploader`(Task 1·2), 기존 세션 경계 분기(`MainWindow.xaml.cs` `IsConfirmedOutOfGame(result)` — 1290행 부근), `_automatic`(현재 자동 인식 패), `_completedTopUnits`, `RefreshAll`의 추천 목록.
- Produces: `AppSettings.TelemetryEnabled`(bool, 기본 true), `AppSettings.TelemetryAnonId`(string), `SettingsStore.EnsureTelemetryAnonId(AppSettings)`, `CompletedTopUnitTracker.CompletedUnitIds`(IReadOnlyCollection<string>).

- [ ] **Step 1: 실패하는 테스트** — SmokeTests에 추가:

```csharp
// 텔레메트리 설정: 기본 켬, 익명 ID는 최초 1회 생성.
{
    var fresh = new AppSettings();
    Assert(fresh.TelemetryEnabled, "텔레메트리 설정: 기본값 켬");
    Assert(string.IsNullOrEmpty(fresh.TelemetryAnonId), "텔레메트리 설정: ID는 보장 시점에 생성");
    var ensured = SettingsStore.EnsureTelemetryAnonId(fresh);
    Assert(Guid.TryParse(ensured.TelemetryAnonId, out _), "텔레메트리 설정: 익명 GUID 생성");
    var again = SettingsStore.EnsureTelemetryAnonId(ensured);
    Assert(again.TelemetryAnonId == ensured.TelemetryAnonId, "텔레메트리 설정: 이미 있으면 유지");
}
```

- [ ] **Step 2: 실패 확인** — 컴파일 실패.

- [ ] **Step 3: 구현**

`Models.cs` `AppSettings`(LastAttemptedUpdateTag 아래):

```csharp
    // 익명 플레이 통계(판 종료 요약) 전송. 개인정보 없음 — 설계 문서 참조.
    public bool TelemetryEnabled { get; set; } = true;
    public string TelemetryAnonId { get; set; } = "";
```

`SettingsStore.cs`:

```csharp
    /// <summary>익명 텔레메트리 ID가 없으면 만들어 저장한다(설치 후 1회).</summary>
    public static AppSettings EnsureTelemetryAnonId(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TelemetryAnonId)) return settings;
        settings.TelemetryAnonId = Guid.NewGuid().ToString();
        try { Save(settings); } catch { /* 다음 저장 때 함께 */ }
        return settings;
    }
```

`MainWindow.xaml` — `ClearDataRefreshCheck` 아래:

```xml
          <CheckBox x:Name="TelemetryCheck" Content="익명 플레이 통계 보내기" Margin="3,5"
                    IsChecked="True" Checked="Telemetry_OnChanged" Unchecked="Telemetry_OnChanged"
                    ToolTip="판이 끝나면 익명 요약(패·추천·결과)을 보내 추천 개선에 씁니다. 닉네임·계정 정보는 수집하지 않습니다."/>
```

`CompletedTopUnitTracker.cs` — 완료 집합을 읽을 공개 프로퍼티가 없으면 추가(내부 완료 저장 필드명은 파일에서 확인해 그대로 노출):

```csharp
    /// <summary>텔레메트리용: 이번 세션에서 완료 처리된 상위 유닛 ID 목록.</summary>
    public IReadOnlyCollection<string> CompletedUnitIds => _completed.Keys.ToList();
```

`MainWindow.xaml.cs`:
- 필드 추가(기존 필드 블록):

```csharp
    private readonly TelemetryUploader _telemetry = new();
    private DateTimeOffset _telemetrySessionStart;
    private List<string> _telemetryLastTop = [];
    private string _lastWarcraftVersion = "";
```

- 초기화(설정 로드 직후): `SettingsStore.EnsureTelemetryAnonId(_settings);`, UI 준비 후 `TelemetryCheck.IsChecked = _settings.TelemetryEnabled;`, 앱 시작 시 `_ = _telemetry.FlushPendingAsync();`
- 핸들러:

```csharp
    private void Telemetry_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.TelemetryEnabled = TelemetryCheck.IsChecked == true;
        SettingsStore.Save(_settings);
    }
```

- `RefreshAll`에서 추천 목록 확정 직후: `_telemetryLastTop = recommendations.Take(5).Select(x => x.Unit.Id).ToList();` (변수명은 해당 지점의 실제 추천 리스트 변수로).
- `ScanAsync`: `result.Diagnostics.ProcessVersion`이 비어 있지 않으면 `_lastWarcraftVersion`에 보관. `ShouldReplaceInventory` 분기에서 `_liveSessionActive`가 false→true로 바뀔 때 `_telemetrySessionStart = DateTimeOffset.UtcNow;`
- `IsConfirmedOutOfGame(result)` 분기 — 기존 리셋 코드보다 **먼저** `SendMatchTelemetry();` 호출:

```csharp
    private void SendMatchTelemetry()
    {
        try
        {
            if (!_settings.TelemetryEnabled || !_liveSessionActive) return;
            var hand = _automatic.Values.Where(x => x.Count > 0).ToList();
            var completed = _completedTopUnits.CompletedUnitIds.ToList();
            if (hand.Count == 0 && completed.Count == 0) return; // 빈 판은 보내지 않음
            var record = MatchTelemetryRecorder.Build(
                _settings.TelemetryAnonId, UpdateService.CurrentVersion.ToString(3),
                "2.314", string.IsNullOrEmpty(_lastWarcraftVersion) ? "unknown" : _lastWarcraftVersion,
                _settings.GoalUnitId, _settings.NavigationMode, _settings.GoroseiMode,
                "auto", hand, completed, _telemetryLastTop,
                _telemetrySessionStart, DateTimeOffset.UtcNow, hand.Sum(x => x.Count));
            _ = _telemetry.EnqueueAndFlushAsync(record);
        }
        catch { /* fail-silent */ }
    }
```

  (`buildVariant`는 AppSettings에 저장 필드가 없으면 `"auto"` 고정 — BuildVariants 선택 저장 필드가 있으면 그 값을 사용.)
- [ ] **Step 4: 통과 확인** — 스모크 그린 + `Stop-Process OrandOverlay` 후 `dotnet build -c Release -p:BaseOutputPath=bin-verify/` 경고 0.
- [ ] **Step 5: 커밋** — `git commit -m "판 종료 시 익명 플레이 통계 전송(기본 켬, 옵트아웃 가능)"`

---

### Task 4: Cloudflare Worker (수집 우체통)

**Files (모두 `D:\OrandOverlay\work\telemetry-worker\` — 저장소 밖·비공개):**
- Create: `package.json`, `wrangler.toml`, `schema.sql`, `src/validate.js`, `src/index.js`, `test/validate.test.mjs`, `test/good-record.json`

**Interfaces:**
- Produces: `POST /v1/records`(익명 업로드, 204/400/429/507), `GET /v1/records?since=<rowid>&limit=<n>`(`Authorization: Bearer <COLLECTOR_KEY>`, 403 미인증) — Task 5 클라 상수·Task 6 수집기가 사용. D1 `records(id, anon_id, received_at, app_version, map_version, outcome, payload)` + `rate(key, day, count)`.

- [ ] **Step 1: 프로젝트 뼈대** — `package.json`:

```json
{
  "name": "orand-telemetry-worker",
  "private": true,
  "type": "module",
  "scripts": { "test": "node --test test/", "dev": "npx wrangler dev", "deploy": "npx wrangler deploy" }
}
```

`wrangler.toml`(database_id는 Step 6에서 기입):

```toml
name = "orand-telemetry"
main = "src/index.js"
compatibility_date = "2026-08-01"

[[d1_databases]]
binding = "DB"
database_name = "orand-telemetry"
database_id = "FILL_AFTER_D1_CREATE"
```

`schema.sql`:

```sql
CREATE TABLE IF NOT EXISTS records (
  id TEXT PRIMARY KEY,
  anon_id TEXT NOT NULL,
  received_at INTEGER NOT NULL,
  app_version TEXT NOT NULL,
  map_version TEXT NOT NULL,
  outcome TEXT NOT NULL,
  payload TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_records_received ON records(received_at);
CREATE TABLE IF NOT EXISTS rate (
  key TEXT PRIMARY KEY,
  day TEXT NOT NULL,
  count INTEGER NOT NULL
);
```

- [ ] **Step 2: 검증 함수 실패 테스트** — `test/validate.test.mjs`:

```js
import test from "node:test";
import assert from "node:assert/strict";
import { validateRecord } from "../src/validate.js";

const good = {
  schemaVersion: 1, recordId: "3f2c8a1e-0000-4000-8000-000000000001",
  anonId: "3f2c8a1e-0000-4000-8000-000000000002", capturedAt: "2026-08-19T10:00:00.000Z",
  appVersion: "0.6.0", mapVersion: "2.314", warcraftVersion: "2.0.4.23745",
  goalUnitId: "yamato_transcendent", navigationMode: "PathOfKings.BountyHunter",
  goroseiMode: "None", buildVariant: "auto",
  finalHand: [{ unitId: "luffy_common", count: 2 }],
  completedTops: [], topRecommendations: ["yamato_transcendent"],
  sessionStartedAt: "2026-08-19T09:30:00.000Z", sessionEndedAt: "2026-08-19T10:00:00.000Z",
  lastObservedUnitCount: 2, outcome: "unknown", outcomeSource: "none",
};

test("정상 레코드 통과", () => assert.equal(validateRecord(good), null));
test("스키마 버전 불일치 거절", () =>
  assert.match(validateRecord({ ...good, schemaVersion: 2 }), /schemaVersion/));
test("outcome 화이트리스트", () =>
  assert.match(validateRecord({ ...good, outcome: "win" }), /outcome/));
test("finalHand 200종 상한", () => {
  const hand = Array.from({ length: 201 }, (_, i) => ({ unitId: `u${i}`, count: 1 }));
  assert.match(validateRecord({ ...good, finalHand: hand }), /finalHand/);
});
test("UUID 형식 강제", () =>
  assert.match(validateRecord({ ...good, anonId: "me@example.com" }), /anonId/));
```

`test/good-record.json`에 위 `good` 객체를 JSON으로 저장(Step 7 curl용).

- [ ] **Step 3: 실패 확인** — `npm test` → `validate.js` 없음으로 실패.

- [ ] **Step 4: 구현** — `src/validate.js`:

```js
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const OUTCOMES = new Set(["unknown", "clear", "fail"]);
const SOURCES = new Set(["none", "memoryProbe"]);
const str = (v, max) => typeof v === "string" && v.length > 0 && v.length <= max;

/** 유효하면 null, 아니면 사유 문자열. 우체통은 이 이상 똑똑해지지 않는다. */
export function validateRecord(r) {
  if (!r || typeof r !== "object") return "not an object";
  if (r.schemaVersion !== 1) return "schemaVersion must be 1";
  if (!UUID.test(r.recordId ?? "")) return "recordId must be UUID";
  if (!UUID.test(r.anonId ?? "")) return "anonId must be UUID";
  for (const key of ["capturedAt", "sessionStartedAt", "sessionEndedAt"])
    if (!str(r[key], 40) || Number.isNaN(Date.parse(r[key]))) return `${key} must be ISO time`;
  for (const key of ["appVersion", "mapVersion", "warcraftVersion"])
    if (!str(r[key], 32)) return `${key} required`;
  for (const key of ["goalUnitId", "navigationMode", "goroseiMode", "buildVariant"])
    if (!str(r[key], 64)) return `${key} required`;
  if (!Array.isArray(r.finalHand) || r.finalHand.length > 200) return "finalHand invalid";
  for (const entry of r.finalHand)
    if (!str(entry?.unitId, 64) || !Number.isInteger(entry?.count) ||
        entry.count < 1 || entry.count > 999) return "finalHand entry invalid";
  if (!Array.isArray(r.completedTops) || r.completedTops.length > 50 ||
      r.completedTops.some(x => !str(x, 64))) return "completedTops invalid";
  if (!Array.isArray(r.topRecommendations) || r.topRecommendations.length > 5 ||
      r.topRecommendations.some(x => !str(x, 64))) return "topRecommendations invalid";
  if (!Number.isInteger(r.lastObservedUnitCount) ||
      r.lastObservedUnitCount < 0 || r.lastObservedUnitCount > 999) return "lastObservedUnitCount invalid";
  if (!OUTCOMES.has(r.outcome)) return "outcome invalid";
  if (!SOURCES.has(r.outcomeSource)) return "outcomeSource invalid";
  return null;
}
```

`src/index.js`:

```js
import { validateRecord } from "./validate.js";

const MAX_BODY = 4096;      // 4KB — 설계 상한
const DAILY_LIMIT = 200;    // 익명ID당 하루 업로드 상한(판당 1개면 과분)

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "POST" && url.pathname === "/v1/records")
      return handleUpload(request, env);
    if (request.method === "GET" && url.pathname === "/v1/records")
      return handlePull(request, env, url);
    return new Response(null, { status: 404 });
  },
};

async function handleUpload(request, env) {
  const body = await request.text();
  if (body.length > MAX_BODY) return new Response(null, { status: 400 });
  let record;
  try { record = JSON.parse(body); } catch { return new Response(null, { status: 400 }); }
  if (validateRecord(record) !== null) return new Response(null, { status: 400 });

  const day = new Date().toISOString().slice(0, 10);
  const key = `${record.anonId}:${day}`;
  try {
    const row = await env.DB.prepare("SELECT count, day FROM rate WHERE key = ?").bind(key).first();
    const used = row && row.day === day ? row.count : 0;
    if (used >= DAILY_LIMIT) return new Response(null, { status: 429 });
    await env.DB.prepare(
      "INSERT INTO rate(key, day, count) VALUES(?, ?, ?) ON CONFLICT(key) DO UPDATE SET count = ?, day = ?")
      .bind(key, day, used + 1, used + 1, day).run();
    await env.DB.prepare(
      "INSERT OR IGNORE INTO records(id, anon_id, received_at, app_version, map_version, outcome, payload) VALUES(?, ?, ?, ?, ?, ?, ?)")
      .bind(record.recordId, record.anonId, Date.now(), record.appVersion,
            record.mapVersion, record.outcome, body).run();
    return new Response(null, { status: 204 });
  } catch { return new Response(null, { status: 507 }); }
}

async function handlePull(request, env, url) {
  const auth = request.headers.get("Authorization") ?? "";
  if (auth !== `Bearer ${env.COLLECTOR_KEY}`) return new Response(null, { status: 403 });
  const since = Number(url.searchParams.get("since") ?? 0);
  const limit = Math.min(Number(url.searchParams.get("limit") ?? 500), 1000);
  try {
    const rows = await env.DB.prepare(
      "SELECT rowid, payload FROM records WHERE rowid > ? ORDER BY rowid LIMIT ?")
      .bind(since, limit).all();
    return Response.json({
      records: rows.results.map(r => ({ cursor: r.rowid, payload: JSON.parse(r.payload) })),
      nextCursor: rows.results.length ? rows.results.at(-1).rowid : since,
    });
  } catch { return new Response(null, { status: 507 }); }
}
```

- [ ] **Step 5: 유닛 테스트 통과** — `npm test` 전부 PASS.
- [ ] **Step 6: D1 생성·스키마·시크릿·배포** —

```bash
npx wrangler d1 create orand-telemetry     # 출력 database_id를 wrangler.toml에 기입
npx wrangler d1 execute orand-telemetry --remote --file schema.sql
python -c "import secrets; print(secrets.token_urlsafe(32))"   # 출력 키를 다음 명령 프롬프트에 입력
npx wrangler secret put COLLECTOR_KEY
npx wrangler deploy                        # 출력 URL 기록: https://orand-telemetry.<account>.workers.dev
```

- [ ] **Step 7: 실배포 왕복 검증** — 배포 URL 기준:

```bash
curl -s -o /dev/null -w "%{http_code}" -X POST <URL>/v1/records -d '{"bad":1}'                  # 기대 400
curl -s -o /dev/null -w "%{http_code}" -X POST <URL>/v1/records --data-binary @test/good-record.json  # 기대 204
curl -s -o /dev/null -w "%{http_code}" "<URL>/v1/records?since=0"                               # 기대 403
curl -s -H "Authorization: Bearer <KEY>" "<URL>/v1/records?since=0" | head -c 200               # records 1건 확인
```

- [ ] **Step 8: 키 보관** — 키를 `work/telemetry-worker/.collector-key`에 저장(수집기가 읽음). work는 저장소 밖이라 커밋 없음.

---

### Task 5: 클라 엔드포인트 연결

**Files:**
- Modify: `outputs/OrandOverlay/TelemetryUploader.cs` (`DefaultEndpoint`)
- Modify: `outputs/OrandOverlay/SmokeTests/Program.cs`

- [ ] **Step 1:** `DefaultEndpoint`를 Task 4 배포 URL + `/v1/records`로 교체.
- [ ] **Step 2: 스모크에 상수 검증 추가**:

```csharp
Assert(TelemetryUploader.DefaultEndpoint.StartsWith("https://") &&
       TelemetryUploader.DefaultEndpoint.EndsWith("/v1/records") &&
       !TelemetryUploader.DefaultEndpoint.Contains("tmo.gg"),
    "텔레메트리: 기본 엔드포인트는 자체 Worker(HTTPS)이며 티모지지가 아님");
```

- [ ] **Step 3:** 스모크 그린 확인.
- [ ] **Step 4:** D1 적재 수 확인으로 왕복 갈음 — `npx wrangler d1 execute orand-telemetry --remote --command "SELECT COUNT(*) AS n FROM records"` (Task 4 Step 7의 204 업로드 1건 이상).
- [ ] **Step 5: 커밋** — `git commit -m "텔레메트리 업로드 엔드포인트 연결"`

---

### Task 6: 수집기 확장 (pull → 집계 → 스냅샷)

**Files:**
- Create: `work/collector/collect_telemetry.py` (비공개)
- Create: `outputs/OrandOverlay/Data/orand-live-stats.json` (스크립트 산출물)
- 스케줄 작업 `OrandOverlay Telemetry Collector` 등록

**Interfaces:**
- Consumes: Worker `GET /v1/records`(Task 4), `work/telemetry-worker/.collector-key`.
- Produces: `Data/orand-live-stats.json` — Task 7이 파싱하는 스키마:

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-08-19T13:00:00Z",
  "totalRecords": 3,
  "labeledRecords": 2,
  "goals": {
    "yamato_transcendent": {
      "plays": 2, "labeled": 1, "clears": 1,
      "adherenceMean": 1.0, "failHeavyUnits": []
    }
  },
  "weights": {}
}
```

- [ ] **Step 1: 집계 셀프테스트 작성** — `collect_telemetry.py`에 `--selftest` 모드 내장(수집기 관례상 별도 러너 없음):

```python
def selftest():
    records = [
        {"goalUnitId": "g1", "outcome": "unknown",
         "topRecommendations": ["g1", "u2"], "finalHand": [{"unitId": "u2", "count": 1}],
         "completedTops": ["g1"]},
        {"goalUnitId": "g1", "outcome": "clear",
         "topRecommendations": ["g1"], "finalHand": [], "completedTops": ["g1"]},
        {"goalUnitId": "g2", "outcome": "fail",
         "topRecommendations": ["u9"], "finalHand": [{"unitId": "u9", "count": 2}],
         "completedTops": []},
    ]
    stats = aggregate(records)
    assert stats["totalRecords"] == 3 and stats["labeledRecords"] == 2, stats
    g1 = stats["goals"]["g1"]
    assert g1["plays"] == 2 and g1["clears"] == 1 and g1["labeled"] == 1
    assert 0.99 < g1["adherenceMean"] <= 1.0   # 두 판 모두 추천 유닛을 확보
    assert stats["goals"]["g2"]["labeled"] == 1 and stats["goals"]["g2"]["clears"] == 0
    assert stats["weights"] == {}              # 게이트(표본 30) 미달이면 자동 반영 없음
    print("selftest OK")
```

- [ ] **Step 2: 실패 확인** — `python collect_telemetry.py --selftest` → `aggregate` 미정의로 실패.
- [ ] **Step 3: 구현** — 파일 상단·핵심 함수:

```python
# -*- coding: utf-8 -*-
"""오버레이 자체 텔레메트리 수집·집계기 — 하루 2회 스케줄 실행.

Worker(D1)에서 새 레코드를 커서 기준으로 당겨 와 월별 jsonl로 보관하고,
Data/orand-live-stats.json 스냅샷을 갱신해 커밋한다. 원본 레코드는 공개하지 않는다.
자동 튜닝 게이트: 라벨 확정 표본>=30 + 95% 신뢰구간이 0을 벗어날 때만 weights 반영.
M2 시점에는 라벨이 없어 weights는 항상 비어 있고 표기용 통계만 나간다.
"""
import json, sys, subprocess, urllib.request
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).parent
REPO = Path(r"D:\OrandOverlay\outputs\OrandOverlay")
STATE = HERE / "telemetry_state.json"
RAW_DIR = HERE.parent / "telemetry"
STATS = REPO / "Data" / "orand-live-stats.json"
KEY_FILE = HERE.parent / "telemetry-worker" / ".collector-key"
LOG = HERE / "telemetry-collector.log"
ENDPOINT = "https://orand-telemetry.<account>.workers.dev/v1/records"  # Task 4 배포 URL로 교체
GATE_MIN_LABELED = 30


def pull(cursor):
    key = KEY_FILE.read_text(encoding="utf-8").strip()
    out = []
    while True:
        req = urllib.request.Request(f"{ENDPOINT}?since={cursor}&limit=500",
                                     headers={"Authorization": f"Bearer {key}"})
        with urllib.request.urlopen(req, timeout=30) as resp:
            page = json.load(resp)
        out.extend(page["records"])
        if page["nextCursor"] == cursor or len(page["records"]) < 500:
            return out, page["nextCursor"]
        cursor = page["nextCursor"]


def adherence(record):
    top = record.get("topRecommendations") or []
    if not top:
        return None
    owned = {e["unitId"] for e in record.get("finalHand", [])} | set(record.get("completedTops", []))
    return sum(1 for u in top if u in owned) / len(top)


def aggregate(records):
    goals = {}
    labeled_total = 0
    for r in records:
        g = goals.setdefault(r.get("goalUnitId", "unknown"),
                             {"plays": 0, "labeled": 0, "clears": 0, "_adh": [], "failHeavyUnits": []})
        g["plays"] += 1
        a = adherence(r)
        if a is not None:
            g["_adh"].append(a)
        if r.get("outcome") in ("clear", "fail"):
            g["labeled"] += 1
            labeled_total += 1
            if r["outcome"] == "clear":
                g["clears"] += 1
    for g in goals.values():
        g["adherenceMean"] = round(sum(g["_adh"]) / len(g["_adh"]), 3) if g["_adh"] else None
        del g["_adh"]
    return {"schemaVersion": 1,
            "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "totalRecords": len(records), "labeledRecords": labeled_total,
            "goals": goals, "weights": {}}  # weights 게이트 로직은 M4에서 채운다
```

`main()` 순서(collect_clears.py의 `log`/`run_git` 함수를 이 파일에 복사해 동일 관례 사용 — import 아님):
1. `STATE` 로드(`{"cursor": 0}` 기본) → `pull(cursor)`
2. 신규 레코드를 `RAW_DIR/records-YYYY-MM.jsonl`에 한 줄씩 append (`payload`만)
3. `RAW_DIR`의 모든 jsonl을 읽어 `aggregate` → `STATS`에 `json.dumps(..., ensure_ascii=True)` 기록
4. 신규 0건이면 로그만 남기고 종료(커밋 없음). 신규 있으면 `run_git("pull","--rebase","--autostash")` → `add Data/orand-live-stats.json` → `commit -m "라이브 플레이 통계 갱신 +N판"` → `push`
5. `STATE`에 새 cursor 저장. 모든 예외는 로그로만(`collect_clears.py`와 동일한 무인 실행 원칙).
- [ ] **Step 4: selftest 통과** — `python collect_telemetry.py --selftest` → `selftest OK`.
- [ ] **Step 5: 실왕복 1회** — `python collect_telemetry.py` 실행 → Task 4에서 넣은 테스트 레코드가 `Data/orand-live-stats.json`의 `totalRecords >= 1`로 잡히고 커밋·푸시됐는지 확인. (테스트 레코드가 통계를 오염시키므로 확인 후 D1에서 삭제: `npx wrangler d1 execute orand-telemetry --remote --command "DELETE FROM records WHERE anon_id = '3f2c8a1e-0000-4000-8000-000000000002'"` 후 수집기 재실행으로 스냅샷 재생성 — jsonl에서도 해당 anonId 줄 제거.)
- [ ] **Step 6: 스케줄 등록** —

```powershell
$a = New-ScheduledTaskAction -Execute "C:\Users\123\AppData\Local\Programs\Python\Python312\pythonw.exe" -Argument '"D:\OrandOverlay\work\collector\collect_telemetry.py"'
$t = @((New-ScheduledTaskTrigger -Daily -At 10:05), (New-ScheduledTaskTrigger -Daily -At 22:05))
$s = New-ScheduledTaskSettingsSet -StartWhenAvailable
Register-ScheduledTask -TaskName "OrandOverlay Telemetry Collector" -Action $a -Trigger $t -Settings $s
```

---

### Task 7: 앱 표기 — "실사용 n판"

**Files:**
- Create: `outputs/OrandOverlay/LiveStats.cs`
- Modify: `outputs/OrandOverlay/MainWindow.xaml.cs` (로드부 + 310행 데이터 라벨 + 목표 요약)
- Modify: `outputs/OrandOverlay/SmokeTests/Program.cs`
- 확인: `OrandOverlay.csproj`의 Data 포함 규칙이 `Data\**` 패턴이면 수정 불요(신규 json 자동 포함)

**Interfaces:**
- Consumes: `Data/orand-live-stats.json`(Task 6 스키마).
- Produces: `LiveStats.Load(string path)` → `LiveStats`(`TotalRecords`, `LabeledRecords`, `TryGetGoal(string, out LiveGoalStats)`), `LiveGoalStats(Plays, Labeled, Clears)` + `ClearRateText`.

- [ ] **Step 1: 실패 테스트** — SmokeTests:

```csharp
// 라이브 통계 스냅샷: 있으면 표기, 없거나 깨졌으면 조용히 무시(fail-silent).
{
    var statsPath = Path.Combine(Path.GetTempPath(), "orand-live-stats-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(statsPath,
        "{\"schemaVersion\":1,\"generatedAt\":\"2026-08-19T13:00:00Z\",\"totalRecords\":41,\"labeledRecords\":12," +
        "\"goals\":{\"yamato_transcendent\":{\"plays\":30,\"labeled\":10,\"clears\":7,\"adherenceMean\":0.8,\"failHeavyUnits\":[]}}," +
        "\"weights\":{}}");
    var live = LiveStats.Load(statsPath);
    Assert(live.TotalRecords == 41, "라이브 통계: 총 판수 파싱");
    Assert(live.TryGetGoal("yamato_transcendent", out var goalStats) && goalStats.Plays == 30,
        "라이브 통계: 목표별 판수");
    Assert(goalStats.ClearRateText == "클리어율 70%", "라이브 통계: 클리어율 표기(확정 라벨 기준)");
    Assert(!live.TryGetGoal("없는목표", out _), "라이브 통계: 미수집 목표는 표기 없음");
    Assert(LiveStats.Load(statsPath + ".missing").TotalRecords == 0, "라이브 통계: 파일 없으면 빈 통계");
    File.Delete(statsPath);
}
```

- [ ] **Step 2: 실패 확인** — 컴파일 실패.
- [ ] **Step 3: 구현** — `LiveStats.cs`:

```csharp
using System.IO;
using System.Text.Json;

namespace OrandOverlay;

/// <summary>자체 수집 플레이 통계 스냅샷. 없거나 깨져도 앱 동작에 영향이 없어야 한다.</summary>
public sealed class LiveStats
{
    public int TotalRecords { get; init; }
    public int LabeledRecords { get; init; }
    private readonly Dictionary<string, LiveGoalStats> _goals = new(StringComparer.OrdinalIgnoreCase);

    public static LiveStats Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new LiveStats();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) return new LiveStats();
            var stats = new LiveStats
            {
                TotalRecords = root.GetProperty("totalRecords").GetInt32(),
                LabeledRecords = root.GetProperty("labeledRecords").GetInt32(),
            };
            foreach (var goal in root.GetProperty("goals").EnumerateObject())
                stats._goals[goal.Name] = new LiveGoalStats(
                    goal.Value.GetProperty("plays").GetInt32(),
                    goal.Value.GetProperty("labeled").GetInt32(),
                    goal.Value.GetProperty("clears").GetInt32());
            return stats;
        }
        catch { return new LiveStats(); }
    }

    public bool TryGetGoal(string goalId, out LiveGoalStats stats) => _goals.TryGetValue(goalId, out stats!);
}

public sealed record LiveGoalStats(int Plays, int Labeled, int Clears)
{
    /// <summary>확정 라벨이 있을 때만 클리어율을 말한다. 없으면 빈 문자열.</summary>
    public string ClearRateText =>
        Labeled > 0 ? $"클리어율 {(int)Math.Round(100.0 * Clears / Labeled)}%" : "";
}
```

`MainWindow.xaml.cs`:
- 필드 `private LiveStats _liveStats = new();`, 카탈로그 로드 직후 `_liveStats = LiveStats.Load(Path.Combine(AppContext.BaseDirectory, "Data", "orand-live-stats.json"));`
- 310행 데이터 라벨 문자열에 `(_liveStats.TotalRecords > 0 ? $" · 실사용 {_liveStats.TotalRecords:#,0}판" : "")` 덧붙임.
- 목표 요약 텍스트(목표 콤보 선택 요약을 만드는 지점)에서 `_liveStats.TryGetGoal(_settings.GoalUnitId, out var g)`가 참이면 `$" · 실사용 {g.Plays}판{(g.ClearRateText.Length == 0 ? "" : " · " + g.ClearRateText)}"` 덧붙임.
- [ ] **Step 4: 통과 확인** — 스모크 그린 + 빌드 경고 0 + PASS 카운트 갱신.
- [ ] **Step 5: 커밋** — `git commit -m "추천 근거에 실사용 판수·클리어율 표기"`

---

### Task 8: 버전·패치노트·배포

- [ ] **Step 1:** `OrandOverlay.csproj` `<Version>` → `0.6.0`.
- [ ] **Step 2:** 전체 검증 — `Stop-Process OrandOverlay` 후 Release 빌드 경고 0 + 스모크 전체 그린.
- [ ] **Step 3:** publish + 릴리스(상시 지시): `dotnet publish OrandOverlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish-single -p:BaseOutputPath=bin-publish/` → `gh release create v0.6.0 publish-single/OrandOverlay.exe --title "v0.6.0 - 익명 플레이 통계" --notes-file <패치노트파일>` (자산명 OrandOverlay.exe 필수).
- [ ] **Step 4:** 디스코드 패치노트를 사용자 문체(굵은 소제목 + 이유 설명)로 작성해 대화에 제시 — **수집 시작 사실·수집 항목·옵트아웃 방법을 반드시 포함**.
- [ ] **Step 5:** 커밋·푸시 확인 후 완료 보고.

## Self-Review 결과

- 스펙 커버리지: M1(레코드·큐·옵트아웃·Worker·엔드포인트)=Task 1–5, M2(수집·집계·스냅샷·표기)=Task 6–7, 배포=Task 8. M3(판정 프로브)·M4(게이트 활성)는 스펙 마일스톤대로 이 계획 밖.
- 플레이스홀더: `DefaultEndpoint`(Task 2)와 `ENDPOINT`(Task 6)는 Task 4 배포 후 실제 URL로 교체하는 **명시된 절차**(배포 전에는 존재할 수 없는 값). `database_id`도 동일.
- 타입 일관성: `TelemetryRecord`(C#) ↔ `validateRecord`(JS) ↔ `aggregate`(Python) ↔ `LiveStats`(C#) 필드명 camelCase 일치 확인. `ClearRateText`는 정수 반올림 퍼센트로 통일.
