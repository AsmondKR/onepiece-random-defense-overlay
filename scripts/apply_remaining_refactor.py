from __future__ import annotations

from pathlib import Path
import re
from textwrap import dedent


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit(f"{label}: expected one match, found {count}")
    return updated


def remove_priority_section(text: str, name: str) -> str:
    declaration = f"    private static readonly IReadOnlyDictionary<string, int> {name}"
    declaration_at = text.find(declaration)
    if declaration_at < 0:
        raise SystemExit(f"priority declaration not found: {name}")

    # Each policy table is preceded by its own comment block and a blank line.
    start = text.rfind("\n\n", 0, declaration_at)
    start = 0 if start < 0 else start + 2
    end_match = re.search(r"\n        \};\n", text[declaration_at:])
    if end_match is None:
        raise SystemExit(f"priority end not found: {name}")
    end = declaration_at + end_match.end()
    return text[:start] + text[end:]


def patch_recommendation_engine() -> None:
    path = Path("RecommendationEngine.cs")
    text = path.read_text(encoding="utf-8")

    for name in (
        "YamatoCommunityPriority",
        "UsoppCommunityPriority",
        "ZoroCommunityPriority",
        "JinbeCommunityPriority",
    ):
        text = remove_priority_section(text, name)

    text = regex_once(
        text,
        r"\n        IReadOnlyDictionary<string, int>\? priorities = null;\n"
        r".*?\n        if \(priorities is null\) return 0;",
        "\n        var priorities = RecommendationCommunityPriorities.ForGoal(goal);\n"
        "        if (priorities is null) return 0;",
        "community priority lookup",
    )

    cascade_anchor = "        IReadOnlyDictionary<string, int> cascadeInventory = counts;"
    text = replace_once(
        text,
        cascade_anchor,
        "        // 세라핌은 초기 제한 뒤에 삽입될 수 있으므로 최종 목록에서도 take 계약을 지킨다.\n"
        "        results = RecommendationResultPolicy.Limit(results, take);\n\n"
        + cascade_anchor,
        "final recommendation limit",
    )
    path.write_text(text, encoding="utf-8")

    Path("RecommendationResultPolicy.cs").write_text(
        dedent(
            """
            namespace OrandOverlay;

            /// <summary>후보를 뒤늦게 삽입해도 추천 API의 최대 개수 계약을 지킨다.</summary>
            public static class RecommendationResultPolicy
            {
                public static List<T> Limit<T>(IEnumerable<T> items, int take) =>
                    items.Take(Math.Max(1, take)).ToList();
            }
            """
        ).lstrip(),
        encoding="utf-8",
    )


def patch_main_window() -> None:
    path = Path("MainWindow.xaml.cs")
    text = path.read_text(encoding="utf-8")

    text = regex_once(
        text,
        r"            if \(RecognitionPolicy\.ShouldResetMatch\(result\)\)\n"
        r"            \{\n.*?\n            \}\n"
        r"            RecognitionStatus\.Text",
        "            if (RecognitionPolicy.ShouldResetMatch(result))\n"
        "                ResetMatchSession();\n"
        "            RecognitionStatus.Text",
        "central match reset",
    )

    handler_anchor = (
        "    private void GoalCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)\n"
    )
    reset_method = dedent(
        """
        /// <summary>
        /// 확정된 세션 경계에서 이전 판의 패·추천 선택·추적 상태를 한 번에 비운다.
        /// 일부 필드만 초기화하면 다음 판에 이전 패나 선택 카드가 다시 나타날 수 있다.
        /// </summary>
        private void ResetMatchSession()
        {
            SendMatchTelemetry();
            _automatic.Clear();
            _automaticStale = false;
            _automaticDisconnected = true;
            _telemetryBuffer.Reset();
            _outcome.Reset();
            _telemetrySessionStart = default;
            _telemetryLastTop = [];
            _matchDifficulty = "unknown";
            _liveSessionActive = false;
            _autoStartApplied = false;
            _completedTopUnits.Reset();
            _greenBloodUsage.Reset();
            _selectedRouteId = null;
            _clusterHeadRouteId = null;
            _boardRecs = [];
            _boardPlan = [];
            _boardBanner = null;
            _lastScanSignature = null;
        }

        """
    )
    reset_method = "".join(
        "    " + line if line.strip() else line
        for line in reset_method.splitlines(keepends=True)
    )
    text = replace_once(
        text,
        handler_anchor,
        reset_method + handler_anchor,
        "match reset method insertion",
    )
    path.write_text(text, encoding="utf-8")


def patch_memory_reader() -> None:
    path = Path("WarcraftMemoryRecognitionService.cs")
    text = path.read_text(encoding="utf-8")

    map_state_anchor = "    private MapStateSample? _lastMapState;\n"
    text = replace_once(
        text,
        map_state_anchor,
        map_state_anchor
        + "    private static readonly TimeSpan WaitingLocatorRescanInterval = TimeSpan.FromSeconds(5);\n"
        + "    private bool _sessionBoundaryCachesCleared;\n"
        + "    private DateTimeOffset _nextWaitingLocatorRescanAt = DateTimeOffset.MinValue;\n",
        "waiting cache fields",
    )

    text = replace_once(
        text,
        "                ResetSessionCaches();\n"
        "                return Failure(RecognitionState.Waiting, \"워크 미실행",
        "                ResetSessionCaches(force: true);\n"
        "                return Failure(RecognitionState.Waiting, \"워크 미실행",
        "process-exit cache reset",
    )
    text = replace_once(
        text,
        "                ResetSessionCaches();\n"
        "                return Failure(RecognitionState.Waiting, \"대전 대기 중",
        "                ResetSessionCaches(force: true);\n"
        "                return Failure(RecognitionState.Waiting, \"대전 대기 중",
        "lobby cache reset",
    )

    waiting_inventory = (
        "                ResetSessionCaches();\n                return new RecognitionResult"
    )
    if text.count(waiting_inventory) != 2:
        raise SystemExit(
            "waiting inventory resets: expected two, "
            f"found {text.count(waiting_inventory)}"
        )
    text = text.replace(
        waiting_inventory,
        "                ResetSessionCaches(allowPeriodicRescan: true);\n"
        "                return new RecognitionResult",
    )

    text = replace_once(
        text,
        "            ResetSessionCaches();\n"
        "            return Failure(RecognitionState.Waiting, \"대전 준비 중",
        "            ResetSessionCaches(allowPeriodicRescan: true);\n"
        "            return Failure(RecognitionState.Waiting, \"대전 준비 중",
        "pool-not-ready cache reset",
    )

    ready_anchor = (
        "            var suffix = mapped.UnknownCount > 0 ? "
        "$\" · 미등록 {mapped.UnknownCount}\" : \"\";\n"
    )
    text = replace_once(
        text,
        ready_anchor,
        ready_anchor + "            MarkSessionReady();\n",
        "ready cache marker",
    )

    replacement = dedent(
        """
        private void ResetSessionCaches(bool allowPeriodicRescan = false, bool force = false)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force && _sessionBoundaryCachesCleared &&
                (!allowPeriodicRescan || now < _nextWaitingLocatorRescanAt))
                return;

            lock (_cacheGate) _locatorCache = null;
            _lastMapState = null;
            _lastMapStateAt = DateTimeOffset.MinValue;
            _sessionBoundaryCachesCleared = true;
            _nextWaitingLocatorRescanAt = allowPeriodicRescan
                ? now + WaitingLocatorRescanInterval
                : DateTimeOffset.MinValue;
        }

        private void MarkSessionReady()
        {
            _sessionBoundaryCachesCleared = false;
            _nextWaitingLocatorRescanAt = DateTimeOffset.MinValue;
        }

        private ulong GetLocatorAddress
        """
    )
    replacement = "".join(
        "    " + line if line.strip() else line
        for line in replacement.splitlines(keepends=True)
    ).rstrip()

    text = regex_once(
        text,
        r"    private void ResetSessionCaches\(\)\n"
        r"    \{\n.*?\n    \}\n\n"
        r"    private ulong GetLocatorAddress",
        replacement,
        "cache reset method",
    )
    path.write_text(text, encoding="utf-8")


def patch_app_startup() -> None:
    path = Path("App.xaml.cs")
    text = path.read_text(encoding="utf-8")
    text = replace_once(
        text,
        "        MemoryProfileRefreshService.TryRefreshAsync().GetAwaiter().GetResult();",
        "        _ = MemoryProfileRefreshService.TryRefreshAsync();",
        "non-blocking profile refresh",
    )
    text = text.replace(
        "        // 최대 3초만 기다리며, 실패하면 기존 사용자 캐시/번들 프로필을 그대로 사용한다.",
        "        // 시작 화면은 네트워크를 기다리지 않고, 실패하면 기존 캐시/번들 프로필을 사용한다.",
    )
    path.write_text(text, encoding="utf-8")


def create_unit_tests() -> None:
    tests = Path("OrandOverlay.Tests")
    tests.mkdir(exist_ok=True)

    (tests / "OrandOverlay.Tests.csproj").write_text(
        dedent(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\\OrandOverlay.csproj" />
              </ItemGroup>
            </Project>
            """
        ).lstrip(),
        encoding="utf-8",
    )

    (tests / "RecommendationResultPolicyTests.cs").write_text(
        dedent(
            """
            using OrandOverlay;
            using Xunit;

            namespace OrandOverlay.Tests;

            public sealed class RecommendationResultPolicyTests
            {
                [Fact]
                public void Limit_CapsItemsAddedAfterInitialRanking()
                {
                    var result = RecommendationResultPolicy.Limit(Enumerable.Range(1, 9), 8);
                    Assert.Equal(Enumerable.Range(1, 8), result);
                }

                [Fact]
                public void Limit_AlwaysReturnsAtLeastOneSlot()
                {
                    Assert.Single(RecommendationResultPolicy.Limit(new[] { "first", "second" }, 0));
                }

                [Fact]
                public void Limit_PreservesRankingOrder()
                {
                    Assert.Equal(new[] { "goal", "support" },
                        RecommendationResultPolicy.Limit(new[] { "goal", "support", "extra" }, 2));
                }
            }
            """
        ).lstrip(),
        encoding="utf-8",
    )

    (tests / "RecommendationCommunityPrioritiesTests.cs").write_text(
        dedent(
            """
            using OrandOverlay;
            using Xunit;

            namespace OrandOverlay.Tests;

            public sealed class RecommendationCommunityPrioritiesTests
            {
                [Theory]
                [InlineData("DB0H", "Q30h", 12)]
                [InlineData("B90H", "M30h", 21)]
                [InlineData("F90H", "F50h", 30)]
                [InlineData("A90H", "Q80h", 40)]
                public void ForGoal_ReturnsExtractedPolicy(
                    string goalRawcode, string candidateRawcode, int expected)
                {
                    var goal = new UnitDefinition
                    {
                        Id = "goal",
                        Name = "목표",
                        Rawcodes = [goalRawcode]
                    };
                    var priorities = RecommendationCommunityPriorities.ForGoal(goal);
                    Assert.NotNull(priorities);
                    Assert.Equal(expected, priorities![candidateRawcode]);
                }

                [Fact]
                public void ForGoal_ReturnsNullForUnmappedGoal()
                {
                    var goal = new UnitDefinition
                    {
                        Id = "other",
                        Name = "기타",
                        Rawcodes = ["0000"]
                    };
                    Assert.Null(RecommendationCommunityPriorities.ForGoal(goal));
                }
            }
            """
        ).lstrip(),
        encoding="utf-8",
    )

    (tests / "RecognitionPolicyTests.cs").write_text(
        dedent(
            """
            using OrandOverlay;
            using Xunit;

            namespace OrandOverlay.Tests;

            public sealed class RecognitionPolicyTests
            {
                [Fact]
                public void ShouldResetMatch_RequiresConfirmedWaitingBoundary()
                {
                    var confirmed = new RecognitionResult
                    {
                        State = RecognitionState.Waiting,
                        ConfirmsSessionBoundary = true
                    };
                    var unconfirmed = new RecognitionResult
                    {
                        State = RecognitionState.Waiting,
                        ConfirmsSessionBoundary = false
                    };
                    Assert.True(RecognitionPolicy.ShouldResetMatch(confirmed));
                    Assert.False(RecognitionPolicy.ShouldResetMatch(unconfirmed));
                }

                [Theory]
                [InlineData(RecognitionState.TransientReadError, true)]
                [InlineData(RecognitionState.Waiting, false)]
                [InlineData(RecognitionState.Unsupported, false)]
                public void MayUseLastGoodForRecommendations_IsLimitedToReadRaces(
                    RecognitionState state, bool expected)
                {
                    Assert.Equal(expected,
                        RecognitionPolicy.MayUseLastGoodForRecommendations(state));
                }
            }
            """
        ).lstrip(),
        encoding="utf-8",
    )


patch_recommendation_engine()
patch_main_window()
patch_memory_reader()
patch_app_startup()
create_unit_tests()
