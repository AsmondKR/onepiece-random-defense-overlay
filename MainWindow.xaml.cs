using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrandOverlay;

public partial class MainWindow : Window
{
    private readonly DataCatalog _catalog = new();
    private readonly AppSettings _settings;
    private readonly Dictionary<string, InventoryEntry> _automatic = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new();
    // 릴리스 확인은 API가 아니라 리다이렉트 태그 조사라 호출 제한 부담이 없다 — 2분이면
    // 새 릴리스가 몇 분 안에 전 유저에게 퍼진다.
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private bool _updateBusy;
    private string? _updateNoticeTag;
    private RecommendationEngine _engine = null!;
    private InventoryStatsCalculator _statsCalculator = null!;
    private RareRerollAdvisor _rareRerollAdvisor = null!;
    private GreenBloodAdvisor _greenBloodAdvisor = null!;
    private GreenBloodAdvisor.UsageTracker _greenBloodUsage = null!;
    private SpecialDismantleAdvisor _specialAdvisor = null!;
    private CombineHotkeyCatalog _combineHotkeys = null!;
    private AutoCombinePlanner _combinePlanner = null!;
    private ClearBuildStats _clearStats = ClearBuildStats.Empty;
    private LiveStats _liveStats = new();
    private CompletedTopUnitTracker _completedTopUnits = null!;
    private readonly LiveVerificationRecorder _liveVerification = new();
    private readonly TelemetryUploader _telemetry = new();
    private readonly MatchOutcomeDetector _outcome = new();
    private DateTimeOffset _telemetrySessionStart;
    private List<string> _telemetryLastTop = [];
    private string _lastWarcraftVersion = "";
    private IInventoryRecognizer _recognizer = null!;
    private OverlayWindow _overlay = null!;
    private bool _initialized;
    private bool _scanInProgress;
    private bool _automaticStale;
    private bool _automaticDisconnected;
    private bool _liveSessionActive;
    private bool _autoStartApplied;
    private readonly HashSet<string> _expandedBuildNodes = new(StringComparer.OrdinalIgnoreCase);
    private bool _relockAfterMove;
    private bool _updatingSelections;
    private readonly HashSet<string> _expandedRouteIds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCancellation;
    private int _scanGeneration;
    private string? _lastScanSignature;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResolutionScale();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(ApplyResolutionScale));
        _settings = SettingsStore.Load();
        SettingsStore.EnsureTelemetryAnonId(_settings);
        try
        {
            _catalog.Load();
            _clearStats = ClearBuildStats.Load(ClearSamplePaths());
            _liveStats = LiveStats.Load(Path.Combine(AppContext.BaseDirectory, "Data", "orand-live-stats.json"));
            _combineHotkeys = CombineHotkeyCatalog.Load(
                Path.Combine(AppContext.BaseDirectory, "Data", "tmo-combine-hotkeys.json"));
            _engine = new RecommendationEngine(_catalog, _clearStats.HasData ? _clearStats : null,
                _combineHotkeys);
            _engine.SetLiveStats(_liveStats);
            _statsCalculator = new InventoryStatsCalculator(_catalog);
            _rareRerollAdvisor = new RareRerollAdvisor(_catalog);
            _greenBloodAdvisor = new GreenBloodAdvisor(_catalog);
            _greenBloodUsage = new GreenBloodAdvisor.UsageTracker(_catalog);
            _specialAdvisor = new SpecialDismantleAdvisor(_catalog);
            _combinePlanner = new AutoCombinePlanner(_catalog, _combineHotkeys);
            _completedTopUnits = new CompletedTopUnitTracker(_catalog);
            _recognizer = new WarcraftMemoryRecognitionService(_catalog);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "데이터 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        RepopulateGoalChoices(_settings.GoalUnitId);
        RepopulateNavigationChoices();
        RepopulateBuildVariants();
        GoroseiCombo.ItemsSource = GoroseiEffects.Options;
        var selectedGorosei = GoroseiEffects.Parse(_settings.GoroseiMode);
        GoroseiCombo.SelectedItem = GoroseiEffects.Options.First(option => option.Mode == selectedGorosei);
        GoroseiSummaryText.Text = GoroseiEffects.Options.First(option => option.Mode == selectedGorosei).Summary;
        ClickThroughCheck.IsChecked = _settings.ClickThroughOverlay;
        AutoScanCheck.IsChecked = _settings.AutoScanEnabled;
        ClearDataRefreshCheck.IsChecked = _settings.ClearDataAutoRefresh;
        TelemetryCheck.IsChecked = _settings.TelemetryEnabled;
        _ = _telemetry.FlushPendingAsync();
        AutoStartCheck.IsChecked = _settings.AutoStartGoal;
        DataVersionText.Text = $"데이터 {_catalog.Data.DataVersion} · {_catalog.Data.Disclaimer}" +
                               ClearStatsSummary();
        var appVersion = UpdateService.CurrentVersion;
        VersionText.Text = $"v{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}";

        _overlay = new OverlayWindow();
        _overlay.RestorePosition(_settings.OverlayLeft, _settings.OverlayTop);
        _overlay.Stats.RestorePosition(_settings.StatsOverlayLeft, _settings.StatsOverlayTop);
        _overlay.PositionCommitted += Overlay_OnPositionCommitted;
        _overlay.Stats.PositionCommitted += StatsOverlay_OnPositionCommitted;
        _overlay.HiddenByUser += () => OverlayButton.Content = "오버레이 보이기";
        _overlay.ReRecommendRequested += ShowReRecommendMenu;
        _overlay.SettlementRequested += ShowSettlement;
        _overlay.Show();
        // 배율이 적용된 뒤라야 창 크기가 확정되므로 기본 배치는 로드 후에 잡는다.
        _overlay.Dispatcher.BeginInvoke(new Action(ApplyDefaultOverlayLayout),
            System.Windows.Threading.DispatcherPriority.Loaded);
        _overlay.SetClickThrough(_settings.ClickThroughOverlay);
        _timer.Interval = TimeSpan.FromSeconds(0.8);
        _timer.Tick += async (_, _) => await ScanAsync();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _updateTimer.Stop();
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            SaveOverlayPosition();
            _overlay.Stats.CloseForApplication();
            _overlay.CloseForApplication();
            SettingsStore.Save(_settings);
        };
        _initialized = true;
        RefreshAll("워크 메모리 인식을 준비하는 중입니다.");
        if (_settings.AutoScanEnabled)
        {
            _timer.Start();
            _ = ScanAsync();
        }
        _ = RefreshClearDataAsync();
        _ = AutoUpdateAsync();
        // 초기 버전이라 실행 중에도 주기적으로 새 릴리스를 확인해 바로 반영한다.
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
    }

    private async Task AutoUpdateAsync()
    {
        // 이전 업데이트가 중간에 실패해 남은 임시 파일을 정리한다.
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } processPath &&
                File.Exists(processPath + ".new"))
                File.Delete(processPath + ".new");
        }
        catch
        {
            // 잔여 파일 정리 실패는 업데이트 확인을 막지 않는다.
        }
        await CheckForUpdateAsync();
    }

    // 새 릴리스가 있으면 확인 없이 내려받아 교체하고 자동 재시작한다(유저 지시).
    // 다만 교체는 재시작을 동반하므로 판 도중에는 미룬다(유저 지시) — 다음 확인
    // 주기에 다시 시도한다. 개발 PC(ORAND_DEV)는 검증을 위해 즉시 교체한다.
    // 같은 태그를 이미 시도했다면(버전 미상승 등) 반복하지 않는다.
    private async Task CheckForUpdateAsync()
    {
        if (_updateBusy) return;
        if (!UpdatePolicy.ShouldInstallNow(_liveSessionActive, UpdatePolicy.IsDeveloperMachine)) return;
        var service = new UpdateService();
        var update = await service.CheckAsync();
        if (update is null) return;
        if (update.Tag.Equals(_settings.LastAttemptedUpdateTag, StringComparison.OrdinalIgnoreCase))
        {
            await NotifyUpdateOnceAsync(update.Tag,
                $"{update.Tag} 자동 업데이트가 이전에 완료되지 않았습니다 — 릴리스 페이지에서 수동으로 받아주세요.");
            return;
        }
        if (!UpdateService.CanSelfInstall)
        {
            await NotifyUpdateOnceAsync(update.Tag,
                $"새 버전 {update.Tag} 공개 — 단일 exe 배포가 아니어서 자동 교체를 건너뜁니다.");
            return;
        }
        await InstallUpdateAsync(service, update);
    }

    // 수동 확인(footer 버튼): 자동 확인과 달리 결과를 항상 footer에 알려주고,
    // 이전에 실패로 기록된 태그도 다시 시도한다.
    private async void CheckUpdateNow_OnClick(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        FooterStatus.Text = "업데이트 확인 중…";
        var service = new UpdateService();
        var (update, failed) = await service.CheckDetailedAsync();
        if (failed)
        {
            FooterStatus.Text = "업데이트 확인에 실패했습니다 — 네트워크 상태를 확인해 주세요.";
            return;
        }
        if (update is null)
        {
            var version = UpdateService.CurrentVersion;
            FooterStatus.Text = $"최신 버전입니다 · v{version.Major}.{version.Minor}.{version.Build}";
            return;
        }
        if (!UpdateService.CanSelfInstall)
        {
            FooterStatus.Text = $"새 버전 {update.Tag} 공개 — 단일 exe 배포가 아니어서 자동 교체를 건너뜁니다.";
            return;
        }
        await InstallUpdateAsync(service, update);
    }

    // 주기 확인이 같은 안내를 footer에 반복해서 쓰지 않게 태그당 1회만 알린다.
    private async Task NotifyUpdateOnceAsync(string tag, string message)
    {
        if (tag.Equals(_updateNoticeTag, StringComparison.OrdinalIgnoreCase)) return;
        _updateNoticeTag = tag;
        await Dispatcher.InvokeAsync(() => FooterStatus.Text = message);
    }

    private async Task InstallUpdateAsync(UpdateService service, UpdateInfo update)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        // 진행바 창 — 업데이트가 돌고 있음을 눈에 보이게(유저 요청).
        Window? progressWindow = null;
        ProgressBar? progressBar = null;
        TextBlock? progressLabel = null;
        await Dispatcher.InvokeAsync(() =>
        {
            FooterStatus.Text = $"{update.Tag} 업데이트를 내려받는 중입니다. 완료되면 자동으로 재시작합니다.";
            progressLabel = new TextBlock
            {
                Text = $"{update.Tag} 업데이트 내려받는 중…",
                Foreground = Brushes.White,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 10)
            };
            progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 14, Width = 320 };
            var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            body.Children.Add(progressLabel);
            body.Children.Add(progressBar);
            progressWindow = new Window
            {
                Title = "자동 업데이트",
                Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(15, 17, 24)),
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Content = body
            };
            progressWindow.Show();
        });
        var progress = new Progress<double>(value =>
        {
            if (progressBar is null || progressLabel is null) return;
            progressBar.Value = value * 100;
            progressLabel.Text = $"{update.Tag} 업데이트 내려받는 중… {value:P0}";
        });
        var previousAttemptTag = _settings.LastAttemptedUpdateTag;
        try
        {
            _settings.LastAttemptedUpdateTag = update.Tag;
            SettingsStore.Save(_settings);
            await service.DownloadAndInstallAsync(update, progress);
            await Dispatcher.InvokeAsync(() =>
            {
                if (progressLabel is not null) progressLabel.Text = $"{update.Tag} 설치 후 재시작합니다…";
                Application.Current.Shutdown();
            });
        }
        catch
        {
            // 내려받기·교체 시작 자체가 실패한 경우라 재시도해도 안전하다.
            _settings.LastAttemptedUpdateTag = previousAttemptTag;
            SettingsStore.Save(_settings);
            _updateBusy = false;
            await Dispatcher.InvokeAsync(() =>
            {
                progressWindow?.Close();
                FooterStatus.Text = $"{update.Tag} 자동 업데이트에 실패했습니다 — 잠시 후 다시 시도합니다.";
            });
        }
    }

    // 필드의 강화 폼처럼 카탈로그에 없는 rawcode를 파일로 남겨, 별칭 등록으로
    // 인식을 확장할 수 있게 한다(예: 강화 상디 G90H 발견 경로의 자동화).
    private readonly HashSet<string> _reportedUnknownRawcodes = new(StringComparer.Ordinal);

    private void LogUnknownRawcodes(RecognitionResult result)
    {
        var codes = result.Diagnostics?.UnknownRawcodes;
        if (codes is null || codes.Count == 0) return;
        var fresh = codes.Where(code => _reportedUnknownRawcodes.Add(code)).ToList();
        if (fresh.Count == 0) return;
        try
        {
            File.AppendAllLines(
                Path.Combine(AppPaths.UserDataDirectory, "unknown-rawcodes.log"),
                fresh.Select(code => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {code}"));
        }
        catch
        {
            // 진단 로그 실패는 인식 흐름에 영향을 주지 않는다.
        }
    }

    private static string[] ClearSamplePaths() =>
    [
        Path.Combine(AppContext.BaseDirectory, "Data", "tmo-clear-samples.json"),
        ClearSnapshotRefreshService.CacheFile
    ];

    private string ClearStatsSummary() => _clearStats.HasData
        ? $" · 신+ 클리어 {_clearStats.TotalGodPlusSamples:#,0}판 학습" +
          $"({_clearStats.OldestSampleAt:MM.dd}~{_clearStats.NewestSampleAt:MM.dd})" +
          (_liveStats.TotalRecords > 0 ? $" · 실사용 {_liveStats.TotalRecords:#,0}판" : "")
        : " · 클리어 데이터 없음(수작업 우선순위 사용)";

    /// <summary>
    /// 앱 시작 후 한 번, 저장소 스냅샷이 번들보다 새로우면 증분 수신한다.
    /// 티모지지 서버에는 접속하지 않는다. 실패해도 기존 번들+캐시로 계속 동작한다.
    /// </summary>
    private async Task RefreshClearDataAsync()
    {
        if (!_settings.ClearDataAutoRefresh) return;
        var newerThan = _clearStats.NewestSampleAt ?? DateTimeOffset.UtcNow.AddDays(-14);
        var fresh = await new ClearSnapshotRefreshService().FetchNewSamplesAsync(newerThan);
        if (fresh.Count == 0) return;
        ClearSnapshotRefreshService.MergeIntoCache(ClearSnapshotRefreshService.CacheFile, fresh,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        var reloaded = await Task.Run(() => ClearBuildStats.Load(ClearSamplePaths()));
        if (!reloaded.HasData) return;
        await Dispatcher.InvokeAsync(() =>
        {
            _clearStats = reloaded;
            _engine = new RecommendationEngine(_catalog, _clearStats, _combineHotkeys);
            DataVersionText.Text = $"데이터 {_catalog.Data.DataVersion} · {_catalog.Data.Disclaimer}" +
                                   ClearStatsSummary();
            // 새 데이터로 학습된 상위·항법 목록을 갱신한다(선택은 최대한 유지).
            RepopulateGoalChoices(SelectedGoal?.Id ?? _settings.GoalUnitId);
            RepopulateNavigationChoices();
            RefreshAll($"신+ 클리어 데이터 {fresh.Count}판을 새로 반영했습니다.");
        });
    }

    private List<UnitDefinition> GoalUnits()
    {
        var tops = _catalog.AllUnits
            .Where(unit => IsTopUnitTier(unit.Tier))
            .DistinctBy(x => x.Id)
            .ToList();
        // 학습된 상위(신+ 표본 12판 이상)만 노출하고 표본 많은 순으로 보여준다.
        // 클리어 데이터가 아예 없으면 전체 목록으로 후퇴한다.
        if (!_clearStats.HasData)
            return tops
                .OrderBy(x => x.Tier, StringComparer.CurrentCulture)
                .ThenBy(x => x.Name, StringComparer.CurrentCulture)
                .ToList();
        return tops
            .Select(unit => (Unit: unit, Samples: LearnedSelection.GoalSampleCount(_clearStats, unit)))
            .Where(pair => pair.Samples >= ClearBuildStats.MinimumGoalSamples)
            .OrderByDescending(pair => pair.Samples)
            .ThenBy(pair => pair.Unit.Name, StringComparer.CurrentCulture)
            .Select(pair => pair.Unit)
            .ToList();
    }

    private UnitDefinition? SelectedGoal => GoalCombo.SelectedItem as UnitDefinition;

    private sealed record GoalCategory(string Id, string Name, bool IsMagic, string BaseTier);

    // 유저가 부르는 순서(초월→불멸→영원→제한) 우선, 그 외 티어는 뒤에 이름순.
    private static readonly string[] GoalTierOrder = ["초월", "불멸", "영원", "제한됨", "신비함"];

    private static GoalCategory CategoryFor(UnitDefinition unit)
    {
        var isMagic = DamageTiers.IsMagic(unit.Tier);
        var baseTier = unit.Tier.Split('[', 2)[0].Trim();
        var kind = isMagic ? "마딜" : "물딜";
        return new GoalCategory($"{kind}:{baseTier}", $"{kind} - {baseTier}", isMagic, baseTier);
    }

    // 상위 선택을 "딜 유형 - 티어" 카테고리와 유닛의 2단 콤보로 나눈다(유저 요청).
    // 카테고리 안 유닛 순서는 기존과 같이 학습 표본 많은 순을 유지한다.
    private void RepopulateGoalChoices(string? preferredGoalId = null)
    {
        _updatingSelections = true;
        try
        {
            var goalUnits = GoalUnits();
            if (goalUnits.Count == 0) return;
            var currentId = preferredGoalId ?? SelectedGoal?.Id ?? _settings.GoalUnitId;
            var currentUnit = goalUnits.FirstOrDefault(x => x.Id == currentId) ?? goalUnits[0];
            var categories = goalUnits
                .Select(CategoryFor)
                .DistinctBy(category => category.Id)
                .OrderBy(category => category.IsMagic ? 1 : 0)
                .ThenBy(category => Array.IndexOf(GoalTierOrder, category.BaseTier) is >= 0 and var rank
                    ? rank
                    : int.MaxValue)
                .ThenBy(category => category.BaseTier, StringComparer.CurrentCulture)
                .ToList();
            GoalCategoryCombo.ItemsSource = categories;
            var category = categories.First(item => item.Id == CategoryFor(currentUnit).Id);
            GoalCategoryCombo.SelectedItem = category;
            var units = goalUnits.Where(unit => CategoryFor(unit).Id == category.Id).ToList();
            GoalCombo.ItemsSource = units;
            GoalCombo.SelectedItem = units.FirstOrDefault(x => x.Id == currentUnit.Id) ?? units[0];
        }
        finally
        {
            _updatingSelections = false;
        }
    }

    private void Settlement_OnClick(object sender, RoutedEventArgs e) => ShowSettlement();

    // 클리어 정산: 인식된 유닛(클리어 화면에선 필드 배치 유닛도 로컬 소유로 잡힘)을
    // 티어별로 세어 작은 창으로 보여준다.
    private void ShowSettlement()
    {
        var report = SettlementReport.Build(_catalog, CombinedInventory());
        var window = new Window
        {
            Title = "클리어 정산",
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(15, 17, 24)),
            Topmost = true,
            MaxWidth = 560,
            ResizeMode = ResizeMode.NoResize,
            Content = new TextBlock
            {
                Text = report,
                Foreground = Brushes.White,
                FontSize = 14,
                LineHeight = 24,
                Margin = new Thickness(20, 16, 20, 16),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Malgun Gothic")
            }
        };
        window.Show();
    }

    private void AutoStart_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.AutoStartGoal = AutoStartCheck.IsChecked == true;
        RefreshAll(_settings.AutoStartGoal
            ? "유닛 자동 추천을 켰습니다. 첫 희귀함이 잡히면 상위를 자동으로 정합니다."
            : "유닛 자동 추천을 껐습니다. 목표 상위를 직접 선택하세요.");
    }

    // 자동 시작: 상위를 정하지 않고 출발한 판에서 첫 희귀함이 잡히면, 그 희귀함이
    // 들어가는 학습된 상위 중 표본 최다를 목표로 전환한다(판당 1회, 종료 시 재대기).
    private string? TryAutoStartGoal(IEnumerable<InventoryEntry> inventory)
    {
        if (!_settings.AutoStartGoal || _autoStartApplied || !_clearStats.HasData) return null;
        var advice = AutoStartAdvisor.RecommendGoal(_catalog, _clearStats,
            inventory.Where(entry => entry.Count > 0).Select(entry => entry.UnitId));
        if (advice is null) return null;
        _autoStartApplied = true;
        if (advice.Goal.Id.Equals(SelectedGoal?.Id, StringComparison.OrdinalIgnoreCase))
            return null;
        return ApplyGoalAdvice(advice.Goal,
            $"첫 희귀함 {advice.Rare.Name} 감지 — 신+ {advice.Samples:#,0}판 학습된 " +
            $"{advice.Goal.Name}(으)로 목표를 자동 전환했습니다.");
    }

    private string ApplyGoalAdvice(UnitDefinition goal, string prefix)
    {
        _autoStartApplied = true;
        RepopulateGoalChoices(goal.Id);
        RepopulateNavigationChoices();
        RepopulateBuildVariants();
        var navigationNote = "";
        if (RecommendNavigationForGoal(goal) is { } navigationPick &&
            (NavigationCombo.SelectedItem as NavigationOption)?.Id != navigationPick.Option.Id)
        {
            SelectNavigation(navigationPick.Option);
            navigationNote = $" 항법 추천: {navigationPick.Option.Name}({navigationPick.Reason}).";
        }
        return prefix + navigationNote;
    }

    // 다시 추천: 지금 패 기준으로 조합이 가장 가까운(완성률 순) 학습 상위 순위를
    // 목록으로 보여주고, 골라서 전환한다(유저 요청).
    private void ReRecommend_OnClick(object sender, RoutedEventArgs e) =>
        ShowReRecommendMenu(sender as FrameworkElement ?? this);

    private void ShowReRecommendMenu(FrameworkElement anchor)
    {
        if (!_initialized) return;
        var counts = CombinedInventory()
            .Where(entry => entry.Count > 0)
            .GroupBy(entry => entry.UnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count),
                StringComparer.OrdinalIgnoreCase);
        var calculator = new RecipeCompletionCalculator(_catalog.Unit);
        var ranked = GoalUnits()
            .Select(unit => (Unit: unit,
                Ratio: calculator.Calculate([unit.Id], counts).CompletionRatio,
                Samples: LearnedSelection.GoalSampleCount(_clearStats, unit)))
            .OrderByDescending(pair => pair.Ratio)
            .ThenByDescending(pair => pair.Samples)
            .Take(6)
            .ToList();
        if (ranked.Count == 0)
        {
            RefreshAll("학습된 상위가 없어 추천할 수 없습니다.");
            return;
        }
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        var rank = 1;
        foreach (var entry in ranked)
        {
            var unit = entry.Unit;
            var percent = Math.Round(entry.Ratio * 100, MidpointRounding.AwayFromZero);
            var samples = entry.Samples;
            var isCurrent = unit.Id.Equals(SelectedGoal?.Id, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = $"{rank++}. {unit.Name} · 완성률 {percent:0}% · 신+ {samples:#,0}판" +
                         (isCurrent ? " (현재 목표)" : ""),
                IsEnabled = !isCurrent
            };
            item.Click += (_, _) =>
            {
                _autoStartApplied = true;
                RefreshAll(ApplyGoalAdvice(unit,
                    $"다시 추천: {unit.Name} 선택 (완성률 {percent:0}퍼센트 · 신+ {samples:#,0}판)."));
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // 항법 추천: 클리어 API에 항법 필드가 없어 직접 학습은 불가 — 상위 기수 스코프
    // 표본(1상위 vs 다상위)이 많은 쪽 계열에서 학습된 첫 항법을 고른다. 현재 선택이
    // 이미 그 계열의 학습된 항법이면 유저 취향을 존중해 바꾸지 않는다(null 반환).
    private (NavigationOption Option, string Reason)? RecommendNavigationForGoal(UnitDefinition goal)
    {
        if (!_clearStats.HasData) return null;
        var solo = _clearStats.GoalProfile(goal.Rawcodes, TopScope.SoloTop)?.SampleCount ?? 0;
        var multi = _clearStats.GoalProfile(goal.Rawcodes, TopScope.MultiTop)?.SampleCount ?? 0;
        var preferMulti = multi > solo;
        bool FitsScope(NavigationOption option) => preferMulti
            ? option.AllowsMultipleTopUnits
            : option.CanCraftTopUnits && !option.AllowsMultipleTopUnits;
        var learned = NavigationProfiles.Categories
            .SelectMany(category => VisibleNavigations(category.Id))
            .ToList();
        if (NavigationCombo.SelectedItem is NavigationOption current &&
            learned.Any(option => option.Id == current.Id) && FitsScope(current))
            return null;
        var pick = learned.FirstOrDefault(FitsScope) ?? learned.FirstOrDefault();
        if (pick is null) return null;
        return (pick, preferMulti
            ? $"다상위 {multi:#,0}판 > 1상위 {solo:#,0}판"
            : $"1상위 {solo:#,0}판 우세");
    }

    // 항법 콤보를 특정 항법으로 강제 선택한다(자동 시작의 항법 추천용).
    private void SelectNavigation(NavigationOption option)
    {
        _updatingSelections = true;
        try
        {
            var categories = NavigationProfiles.Categories
                .Where(category => VisibleNavigations(category.Id).Count > 0)
                .ToList();
            if (categories.Count == 0) categories = NavigationProfiles.Categories.ToList();
            NavigationCategoryCombo.ItemsSource = categories;
            NavigationCategoryCombo.SelectedItem = categories.FirstOrDefault(category =>
                category.Id.Equals(option.CategoryId, StringComparison.OrdinalIgnoreCase)) ?? categories[0];
            var options = VisibleNavigations(option.CategoryId);
            if (options.Count == 0) options = NavigationProfiles.ForCategory(option.CategoryId).ToList();
            NavigationCombo.ItemsSource = options;
            NavigationCombo.SelectedItem = options.FirstOrDefault(item => item.Id == option.Id)
                                           ?? options[0];
            if (NavigationCombo.SelectedItem is NavigationOption selected)
                NavigationSummaryText.Text = selected.Summary;
            _settings.NavigationMode = option.Id;
        }
        finally
        {
            _updatingSelections = false;
        }
    }

    private void GoalCategoryCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections) return;
        if (GoalCategoryCombo.SelectedItem is not GoalCategory category) return;
        var units = GoalUnits().Where(unit => CategoryFor(unit).Id == category.Id).ToList();
        if (units.Count == 0) return;
        var current = SelectedGoal;
        GoalCombo.ItemsSource = units;
        // 선택 변경이 GoalCombo_OnSelectionChanged를 태워 항법·빌드 방향 재구성과
        // RefreshAll까지 이어진다.
        GoalCombo.SelectedItem = current is not null && units.Any(x => x.Id == current.Id)
            ? units.First(x => x.Id == current.Id)
            : units[0];
    }

    private List<NavigationOption> VisibleNavigations(string categoryId) => NavigationProfiles
        .ForCategory(categoryId)
        .Where(option => LearnedSelection.NavigationLearned(_clearStats, SelectedGoal, option))
        .ToList();

    // 상위·클리어 데이터가 바뀔 때 항법 목록을 학습된 것만으로 다시 채운다.
    // 채우는 동안의 SelectionChanged 연쇄는 _updatingSelections로 차단한다.
    private void RepopulateNavigationChoices()
    {
        _updatingSelections = true;
        try
        {
            var currentOption = NavigationCombo.SelectedItem as NavigationOption ??
                                NavigationProfiles.Find(_settings.NavigationMode);
            var categories = NavigationProfiles.Categories
                .Where(category => VisibleNavigations(category.Id).Count > 0)
                .ToList();
            if (categories.Count == 0) categories = NavigationProfiles.Categories.ToList();
            NavigationCategoryCombo.ItemsSource = categories;
            var category = categories.FirstOrDefault(item =>
                               item.Id.Equals(currentOption.CategoryId, StringComparison.OrdinalIgnoreCase))
                           ?? categories[0];
            NavigationCategoryCombo.SelectedItem = category;
            var options = VisibleNavigations(category.Id);
            if (options.Count == 0) options = NavigationProfiles.ForCategory(category.Id).ToList();
            NavigationCombo.ItemsSource = options;
            NavigationCombo.SelectedItem =
                options.FirstOrDefault(option => option.Id == currentOption.Id) ?? options[0];
            if (NavigationCombo.SelectedItem is NavigationOption selected)
                NavigationSummaryText.Text = selected.Summary;
        }
        finally
        {
            _updatingSelections = false;
        }
    }

    private static bool IsTopUnitTier(string tier)
    {
        var baseTier = tier.Split('[', 2)[0].Trim();
        return baseTier is "신비함" or "초월" or "불멸" or "영원" or "제한됨";
    }

    private IReadOnlyList<InventoryEntry> CombinedInventory(bool includeAutomatic = true)
    {
        var automatic = includeAutomatic ? _automatic.Values : Enumerable.Empty<InventoryEntry>();
        return automatic.Where(x => x.Count > 0)
            .OrderBy(x => _catalog.Unit(x.UnitId).Name)
            .ToList();
    }

    private void RefreshAll(string? message = null)
    {
        if (!_initialized) return;
        var inventory = CombinedInventory();
        var recommendationInventoryBase = _automaticDisconnected
            ? CombinedInventory(includeAutomatic: false)
            : inventory;
        var recommendationInventory = _automaticDisconnected
            ? recommendationInventoryBase
            : _completedTopUnits.Apply(recommendationInventoryBase);
        // 그린블러드를 유닛에 부여했으면 진력해방(스턴 1.2·공속 30)을 가상 항목으로
        // 합산해 패 수치와 역할 목표 계산에 함께 반영한다(세라핌 제작 시 제외).
        if (_greenBloodUsage.UsedOnUnit)
            recommendationInventory = recommendationInventory
                .Concat(new[]
                {
                    new InventoryEntry { UnitId = "greenblood_buff", Count = 1, Confidence = 1 }
                })
                .ToList();
        // 자동 시작: 첫 희귀함이 잡히는 순간 목표를 전환하고, 아래에서 새 목표로 추천한다.
        var autoStartMessage = TryAutoStartGoal(recommendationInventory);
        var goal = GoalCombo.SelectedItem as UnitDefinition;
        if (goal is null) return;
        var navigation = NavigationCombo.SelectedItem as NavigationOption ??
                         NavigationProfiles.Find(_settings.NavigationMode);
        _settings.GoalUnitId = goal.Id;
        _settings.NavigationMode = navigation.Id;
        var gorosei = (GoroseiCombo.SelectedItem as GoroseiOption)?.Mode
                      ?? GoroseiEffects.Parse(_settings.GoroseiMode);
        _settings.GoroseiMode = gorosei.ToString();
        var buildVariant = (BuildVariantCombo.SelectedItem as BuildVariant)?.Id
                           ?? BuildVariants.AutoId;
        var recommendations = _engine.RecommendNearestCrafts(goal.Id, recommendationInventory,
            navigationMode: navigation.Id, gorosei: gorosei, buildVariant: buildVariant,
            suppressSeraphim: _greenBloodUsage.Used);
        _telemetryLastTop = recommendations.Take(5).Select(x => x.Route.GoalUnitId).ToList();
        GoalSelectLabel.Text = "목표 상위 유닛 · 학습된 유닛만" +
            (_liveStats.TryGetGoal(goal.Id, out var liveGoal)
                ? $" · 실사용 {liveGoal.Plays}판{(liveGoal.ClearRateText.Length == 0 ? "" : " · " + liveGoal.ClearRateText)}"
                : "");
        var inventoryStats = _statsCalculator.Calculate(recommendationInventory);
        var rareRerolls = _rareRerollAdvisor.Evaluate(recommendationInventory, recommendations,
            goal, _clearStats.HasData ? _clearStats : null);
        IReadOnlyList<GreenBloodAdvice> greenBloodAdvice = _greenBloodUsage.Used
            ? Array.Empty<GreenBloodAdvice>()
            : _greenBloodAdvisor.Evaluate(goal, recommendationInventory,
                recommendations, _clearStats.HasData ? _clearStats : null);

        InventoryList.Items.Clear();
        foreach (var item in inventory)
            InventoryList.Items.Add($"{_catalog.Unit(item.UnitId).Name}  ×{item.Count}" +
                                    (_automaticStale ? "   이전 스냅샷" : ""));
        if (inventory.Count == 0) InventoryList.Items.Add("보유 패 없음");

        // 자동 시작 단계(첫 희귀함 전): 상위 카드 대신, 현재 패로 가장 빨리 완성되는
        // 희귀함 순위를 보여준다(패스트 유니크). 첫 희귀함이 잡히면 상위 추천으로 전환.
        // 그 외에는, 패가 하나도 없으면 지원(2순위 이하) 근거가 없어 목표 카드만 남긴다.
        var rarePhase = _settings.AutoStartGoal && !_autoStartApplied;
        // 자동 추천 단계에서는 목표가 아직 없으므로 상위 선택 칸 자체를 숨긴다(유저 요청).
        // 첫 희귀함으로 목표가 정해지면 다시 나타나 선택된 상위를 보여준다.
        GoalSelectLabel.Visibility = GoalSelectRow.Visibility =
            rarePhase ? Visibility.Collapsed : Visibility.Visible;
        IReadOnlyList<Recommendation> visibleRecommendations = rarePhase
            ? _engine.RecommendFastRares(recommendationInventory)
            : recommendationInventory.Count == 0 && recommendations.Count > 1
                ? [recommendations[0]]
                : recommendations;
        var phaseHint = rarePhase
            ? "7라운드까지 희귀함이 안 나오면 선택 위습 1~2개 사용 권장"
            : null;

        RecommendationCards.Children.Clear();
        if (rarePhase)
            RecommendationCards.Children.Add(new TextBlock
            {
                Text = $"첫 희귀함 찾기 · 빠른 완성 순 — {phaseHint}",
                Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 7)
            });
        if (visibleRecommendations.Count == 0)
            RecommendationCards.Children.Add(new TextBlock
            {
                Text = "패 인식 대기 중",
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 12
            });
        for (var i = 0; i < visibleRecommendations.Count; i++)
            RecommendationCards.Children.Add(BuildRecommendationCard(visibleRecommendations[i], i + 1));
        // "지금 조합 가능"은 1번 우선순위 추천의 조합 단계만 보여준다(사용자 요청).
        // 여러 추천의 단계를 섞어 보여주면 지금 뭘 눌러야 하는지 흐려진다.
        var combinePlan = _combinePlanner.Plan(visibleRecommendations.Take(1).ToList(),
            recommendationInventory, _completedTopUnits.CompletedUnitIds);
        var emergencySummons = navigation.Id.Equals("AlliedForces.EmergencyCall",
            StringComparison.OrdinalIgnoreCase)
            ? _engine.RecommendEmergencySummons(recommendations, recommendationInventory)
            : Array.Empty<EmergencySummonAdvice>();
        _overlay.Render(
            rarePhase ? "첫 희귀함 찾기 · 빠른 완성 순" : $"{goal.Name} · {navigation.Name}",
            visibleRecommendations, inventoryStats, rareRerolls,
            greenBloodAdvice,
            !_greenBloodUsage.Used &&
            GreenBloodAdvisor.HasUnusedGreenBlood(_catalog, recommendationInventory),
            combinePlan, RecognitionStatus.Text, DamageTiers.IsMagic(goal.Tier), emergencySummons,
            gorosei, _greenBloodUsage.Used,
            _specialAdvisor.Evaluate(recommendationInventory, recommendations, goal,
                _clearStats.HasData ? _clearStats : null),
            _engine.ActiveStunTarget, _engine.ActiveStunCap,
            phaseHint);
        if (autoStartMessage is not null) FooterStatus.Text = autoStartMessage;
        else if (message is not null) FooterStatus.Text = message;
    }

    // 자동 스캔 틱에서만 쓰는 얕은 갱신: 패·상태가 직전 틱과 같으면 추천 재계산과
    // 전체 UI 재구성을 건너뛰어 게임과의 CPU 경쟁(끊김)을 줄인다. 상태줄만 갱신한다.
    private void RefreshIfScanStateChanged(string? message)
    {
        var signature = BuildScanSignature();
        if (signature == _lastScanSignature)
        {
            if (!string.IsNullOrWhiteSpace(message)) FooterStatus.Text = message;
            _overlay.UpdateStatus(RecognitionStatus.Text);
            return;
        }
        _lastScanSignature = signature;
        RefreshAll(message);
    }

    private string BuildScanSignature()
    {
        var builder = new StringBuilder();
        foreach (var entry in CombinedInventory().OrderBy(x => x.UnitId, StringComparer.Ordinal))
            builder.Append(entry.UnitId).Append(':').Append(entry.Count).Append('|');
        builder.Append(_automaticStale).Append('|').Append(_automaticDisconnected).Append('|')
            .Append(_liveSessionActive).Append('|').Append(_autoStartApplied).Append('|')
            .Append(_greenBloodUsage.Used).Append('|').Append(_greenBloodUsage.UsedOnUnit);
        return builder.ToString();
    }

    private UIElement BuildRecommendationCard(Recommendation item, int rank)
    {
        var headerBody = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = $"{rank}. {RecommendationPresentation.CraftUnitName(item.CompositionUnits[0])}",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        var score = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(75, 55, 135)), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3),
            Child = new TextBlock
            {
                Text = RecommendationPresentation.CompletionPercent(item.RecipeProgress),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold
            }
        };
        Grid.SetColumn(score, 1);
        header.Children.Add(score);
        headerBody.Children.Add(header);
        headerBody.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(125, 55, 43, 90)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 8, 0, 0),
            Child = new TextBlock
            {
                Text = RecommendationPresentation.RecommendationEffectLine(item.CompositionUnits[0]),
                Foreground = new SolidColorBrush(Color.FromRgb(216, 206, 255)),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap
            }
        });
        headerBody.Children.Add(BuildCountBar(item.RecipeProgress, 7, new Thickness(0, 10, 0, 0)));

        var expander = new Expander
        {
            Header = headerBody,
            Content = BuildRecommendationDetails(item),
            IsExpanded = _expandedRouteIds.Contains(item.Route.Id),
            Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(expander,
            $"{RecommendationPresentation.CraftUnitName(item.CompositionUnits[0])}, " +
            $"{RecommendationPresentation.RecommendationEffectLine(item.CompositionUnits[0])}, " +
            $"{RecommendationPresentation.CompletionPercent(item.RecipeProgress)}, 조합식과 부족 패 상세");
        expander.Expanded += (_, _) => _expandedRouteIds.Add(item.Route.Id);
        expander.Collapsed += (_, _) => _expandedRouteIds.Remove(item.Route.Id);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 29, 41)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 11),
            Child = expander
        };
    }

    private static UIElement BuildCountBar(RecipeProgress progress, double height,
        Thickness margin = default)
    {
        var ratio = progress.CompletionRatio;
        var fill = ratio >= 0.8
            ? Color.FromRgb(74, 222, 128)
            : ratio >= 0.5 ? Color.FromRgb(251, 191, 36) : Color.FromRgb(248, 113, 113);
        var columns = new Grid { Height = height, IsHitTestVisible = false };
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0, ratio), GridUnitType.Star)
        });
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0, 1 - ratio), GridUnitType.Star)
        });
        columns.Children.Add(new Border { Background = new SolidColorBrush(fill), CornerRadius = new CornerRadius(height / 2) });
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(65, 72, 88)),
            CornerRadius = new CornerRadius(height / 2),
            Margin = margin,
            Child = columns
        };
        AutomationProperties.SetName(bar,
            $"제작 완성도 {RecommendationPresentation.CompletionPercent(progress)}");
        return bar;
    }

    private UIElement BuildRecommendationDetails(Recommendation item)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        // 부족한 패 나열은 길어질 수 있어 카드를 펼쳤을 때만 보여준다(유저 요청).
        stack.Children.Add(new TextBlock
        {
            Text = item.NextAction,
            Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var warning in item.Warnings)
            stack.Children.Add(new TextBlock
            {
                Text = "⚠ " + warning,
                Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });
        stack.Children.Add(BuildDetailSectionTitle("남은 조합 · 전설 먼저"));
        stack.Children.Add(BuildRemainingRecipe(item));

        // 드릴다운은 구조 탐색용이고, 실제 손은 안흔함부터 차례로 간다(유저 요청) —
        // 조합 순서를 낮은 티어부터 번호로 나열한다.
        if (item.RemainingCraftSteps.Count > 0)
        {
            stack.Children.Add(BuildDetailSectionTitle("조합 순서 · 안흔함부터",
                new Thickness(0, 12, 0, 4)));
            for (var i = 0; i < item.RemainingCraftSteps.Count; i++)
                stack.Children.Add(BuildRemainingRecipeCard(item.RemainingCraftSteps[i], i + 1));
        }

        stack.Children.Add(BuildDetailSectionTitle("부족한 최하위 재료", new Thickness(0, 12, 0, 4)));
        if (item.RecipeProgress.MissingLeaves.Count == 0)
            stack.Children.Add(new TextBlock
            {
                Text = "최하위 재료 모두 확보",
                Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                FontSize = 11
            });
        else
            stack.Children.Add(BuildMissingLeafGrid(item.RecipeProgress.MissingLeaves));

        stack.Children.Add(BuildDetailSectionTitle("유닛 능력", new Thickness(0, 12, 0, 4)));
        if (item.CompositionUnits.Count > 0)
            stack.Children.Add(BuildCompositionUnit(item.CompositionUnits[0]));

        if (item.ClearEvidence is not null)
            stack.Children.Add(new TextBlock
            {
                Text = item.ClearEvidence.Scope switch
                       {
                           TopScope.SoloTop => "신+ 1상위 ",
                           TopScope.MultiTop => "신+ 다상위 ",
                           _ => "신+ 클리어 "
                       } +
                       (item.ClearEvidence.AnchorLabel is null
                           ? ""
                           : $"· {item.ClearEvidence.AnchorLabel} 동반 ") +
                       $"{item.ClearEvidence.SampleCount:#,0}판 중 " +
                       $"채용률 {item.ClearEvidence.SharePercent}퍼센트",
                Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                FontSize = 11,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        return stack;
    }

    // 남은 조합을 단계식으로 보여준다(유저 요청): 전설급을 먼저 리스트업하고,
    // 전설을 펼치면 하위 희귀함, 희귀함을 펼치면 재료가 나온다.
    private UIElement BuildRemainingRecipe(Recommendation item)
    {
        var stack = new StackPanel();
        var (legends, others) = BuildDrilldown.Build(item);
        if (legends.Count == 0 && others.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "바로 조합 가능",
                Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                FontSize = 11
            });
            return stack;
        }
        foreach (var group in legends)
            stack.Children.Add(BuildDrillNode(item.Route.Id, group, null));
        foreach (var step in others)
            stack.Children.Add(BuildRemainingRecipeCard(step, null));
        return stack;
    }

    // 드릴다운 노드: 하위 단계가 있으면 펼쳐서 다음 단계를, 마지막 단계면 재료를 보여준다.
    private UIElement BuildDrillNode(string parentKey, BuildDrilldown.DrillNode node, int? number)
    {
        var key = parentKey + "|" + node.Step.UnitId;
        UIElement content;
        if (node.Children.Count > 0)
        {
            var children = new StackPanel { Margin = new Thickness(16, 0, 0, 4) };
            foreach (var child in node.Children)
                children.Children.Add(BuildDrillNode(key, child, null));
            content = children;
        }
        else
        {
            content = BuildIngredientDetail(node.Step);
        }
        var expander = new Expander
        {
            Header = BuildRemainingRecipeCard(node.Step, number),
            Content = content,
            IsExpanded = _expandedBuildNodes.Contains(key),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        expander.Expanded += (_, _) => _expandedBuildNodes.Add(key);
        expander.Collapsed += (_, _) => _expandedBuildNodes.Remove(key);
        return expander;
    }

    private static UIElement BuildIngredientDetail(RecipeCraftStep step) => new TextBlock
    {
        Text = "재료: " + string.Join(" · ", step.Ingredients
            .OrderBy(ingredient => ingredient.SelectionOrder)
            .Select(ingredient =>
                RecommendationPresentation.SafeText(ingredient.Name).Trim() +
                (ingredient.RequiredCount > 1 ? $" ×{ingredient.RequiredCount}" : ""))),
        Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(16, 3, 0, 5)
    };

    private static UIElement BuildRemainingRecipeCard(RecipeCraftStep node, int? stepNumber)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = UnitImageFactory.Create(node.Image, node.Name, 44, node.UnitId);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = (stepNumber is { } number ? $"{number}. " : "") +
                   RecommendationPresentation.CraftUnitName(node.Name, node.Tier),
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(BuildCraftActionLine(node));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var progress = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        progress.Children.Add(new TextBlock
        {
            Text = $"{Math.Round(node.CompletionRatio * 100, MidpointRounding.AwayFromZero):0}%",
            Foreground = new SolidColorBrush(Color.FromRgb(196, 181, 253)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right
        });
        progress.Children.Add(new TextBlock
        {
            Text = $"보유 {node.OwnedCount}/{node.RequiredCount}",
            Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(progress, 2);
        row.Children.Add(progress);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(19, 24, 35)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9),
            Margin = new Thickness(0, 3, 0, 3),
            Child = row
        };
    }

    // "선택할 유닛 나미 · 조합 키 [Z]"를 한 줄로 펼친다. 라벨은 죽이고
    // 실제 행동 대상(유닛 이름, 키)만 크게 보이게 한다.
    private static UIElement BuildCraftActionLine(RecipeCraftStep node)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        var select = RecommendationPresentation.CraftSelectUnitName(node);
        if (select is null)
        {
            panel.Children.Add(BuildCraftHintLabel("조합할 하위 유닛 없음"));
            return panel;
        }
        panel.Children.Add(BuildCraftHintLabel("선택할 유닛"));
        panel.Children.Add(new TextBlock
        {
            Text = select,
            Foreground = new SolidColorBrush(Color.FromRgb(196, 181, 253)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0)
        });
        if (node.CombineKey is { Length: > 0 } key)
        {
            panel.Children.Add(BuildCraftHintLabel("조합 키", left: 12));
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(42, 49, 71)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(97, 106, 140)),
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 0, 7, 1),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = key,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold
                }
            });
        }
        else if (RecommendationPresentation.CraftCompanionNames(node) is { } companions)
        {
            panel.Children.Add(BuildCraftHintLabel("함께 조합", left: 12));
            panel.Children.Add(new TextBlock
            {
                Text = companions,
                Foreground = new SolidColorBrush(Color.FromRgb(196, 181, 253)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            });
        }
        return panel;
    }

    private static TextBlock BuildCraftHintLabel(string text, double left = 0) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 0, 0)
    };

    private static UIElement BuildMissingLeafGrid(IReadOnlyList<RecipeLeafProgress> leaves)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var leaf in leaves)
            panel.Children.Add(BuildMissingLeafCard(leaf));
        return panel;
    }

    private static UIElement BuildMissingLeafCard(RecipeLeafProgress leaf)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(UnitImageFactory.Create(leaf.Image, leaf.Name, 42, leaf.UnitId));
        stack.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CraftUnitName(leaf.Name, leaf.Tier),
            Foreground = Brushes.White,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"부족 ×{leaf.MissingCount}",
            Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return new Border
        {
            Width = 122,
            MinHeight = 102,
            Background = new SolidColorBrush(Color.FromRgb(19, 24, 35)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7),
            Margin = new Thickness(0, 3, 6, 3),
            Child = stack
        };
    }

    private static TextBlock BuildDetailSectionTitle(string text, Thickness? margin = null) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)),
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = margin ?? new Thickness(0, 0, 0, 4)
    };

    private static UIElement BuildCompositionUnit(CompositionUnitDetail unit)
    {
        var stack = new StackPanel();
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CraftUnitName(unit),
            Foreground = unit.IsGoal ? new SolidColorBrush(Color.FromRgb(196, 181, 253)) : Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        var ownership = new TextBlock
        {
            Text = RecommendationPresentation.Ownership(unit),
            Foreground = unit.OwnedCount > 0
                ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                : Brushes.LightGray,
            FontSize = 10,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(ownership, 1);
        title.Children.Add(ownership);
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.AbilitySummary(unit),
            Foreground = new SolidColorBrush(Color.FromRgb(190, 198, 214)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(unit.Description))
            stack.Children.Add(new TextBlock
            {
                Text = RecommendationPresentation.SafeDescription(unit.Description),
                Foreground = new SolidColorBrush(Color.FromRgb(137, 146, 164)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(19, 24, 35)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 0),
            Child = stack
        };
    }

    private async Task ScanAsync()
    {
        if (_scanInProgress || AutoScanCheck.IsChecked != true) return;
        _scanInProgress = true;
        var generation = _scanGeneration;
        var recognizer = _recognizer;
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        try
        {
            // 첫 인식은 메모리 구조 탐색이 필요해 몇 초 걸린다. 그동안 초기 문구가 남아 있지 않게 한다.
            if (RecognitionStatus.Text == "수동 모드") RecognitionStatus.Text = "메모리 구조 탐색 중…";
            var result = await recognizer.RecognizeAsync(_settings, cancellation.Token);
            if (generation != _scanGeneration || !ReferenceEquals(recognizer, _recognizer)) return;
            LogUnknownRawcodes(result);
            if (!string.IsNullOrWhiteSpace(result.Diagnostics.ProcessVersion))
                _lastWarcraftVersion = result.Diagnostics.ProcessVersion;
            if (result.ShouldReplaceInventory)
            {
                if (!_liveSessionActive) _telemetrySessionStart = DateTimeOffset.UtcNow;
                _completedTopUnits.Observe(result.Entries);
                _completedTopUnits.ObserveGoalCraft(_settings.GoalUnitId, result.Entries);
                _greenBloodUsage.Observe(result.Entries);
                _automatic.Clear();
                foreach (var entry in result.Entries) _automatic[entry.UnitId] = entry;
                _automaticStale = false;
                _automaticDisconnected = false;
                _liveSessionActive = true;
            }
            else if (result.ShouldClearAutomaticInventory)
            {
                _automatic.Clear();
                _automaticStale = false;
                _automaticDisconnected = true;
                if (RecognitionPolicy.IsConfirmedOutOfGame(result))
                {
                    SendMatchTelemetry(); // _automatic/_completedTopUnits 리셋 전에
                    _outcome.Reset();
                    _liveSessionActive = false;
                    _autoStartApplied = false;
                    _completedTopUnits.Reset();
                    _greenBloodUsage.Reset();
                }
            }
            else
            {
                _automaticStale = _automatic.Count > 0;
                _automaticDisconnected = !RecognitionPolicy.MayUseLastGoodForRecommendations(result.State);
            }
            if (result.ShouldReplaceInventory || result.State == RecognitionState.Waiting)
            {
                _outcome.Observe(result.Diagnostics.ObservedObjects, result.Diagnostics.ForeignObjects);
                if (result.Diagnostics.MapState is { } mapState)
                {
                    _outcome.ObserveRound(mapState.MaxRound);
                    _outcome.ObserveSettlement(mapState.SettlementCopies);
                }
            }
            _liveVerification.Observe(result);
            UpdateLiveVerifyUi();
            RecognitionStatus.Text = KoreanLabels.RemoveLatin(result.Status);
            RecognitionStatus.Foreground = result.State switch
            {
                RecognitionState.Ready => Brushes.LightGreen,
                RecognitionState.Waiting => Brushes.Khaki,
                RecognitionState.TransientReadError => Brushes.Orange,
                _ => Brushes.LightCoral
            };
            var detail = result.Diagnostics.UserDisplayText;
            if (!result.ShouldReplaceInventory && _automatic.Count > 0)
                detail = string.IsNullOrWhiteSpace(detail) ? "마지막 정상 패를 유지합니다." : detail + " | 마지막 정상 패 유지";
            RefreshIfScanStateChanged(detail);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (generation == _scanGeneration)
            {
                _automaticStale = _automatic.Count > 0;
                _automaticDisconnected = false;
                RecognitionStatus.Text = "인식 오류 · 기존 패 유지";
                RecognitionStatus.Foreground = Brushes.Orange;
                RefreshIfScanStateChanged("인식 중 오류가 발생했습니다. 다음 자동 인식을 기다립니다.");
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation)) _scanCancellation = null;
            cancellation.Dispose();
            _scanInProgress = false;
        }
    }

    private void GoalCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections) return;
        // 유저가 직접 상위를 고르면 이번 판의 자동 시작(희귀함 우선) 단계를 끝낸다.
        if (_initialized) _autoStartApplied = true;
        // 상위가 바뀌면 그 상위 기준으로 학습된 항법·빌드 방향을 다시 보여준다.
        if (_initialized)
        {
            RepopulateNavigationChoices();
            RepopulateBuildVariants();
        }
        RefreshAll();
    }

    // 같은 상위라도 빌드 방향이 갈리는 유닛(니카 이감/노이감)만 선택 UI를 노출한다.
    private void RepopulateBuildVariants()
    {
        _updatingSelections = true;
        try
        {
            IReadOnlyList<BuildVariant> variants = SelectedGoal is { } goal
                ? BuildVariants.For(goal)
                : Array.Empty<BuildVariant>();
            var visible = variants.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildVariantLabel.Visibility = visible;
            BuildVariantCombo.Visibility = visible;
            BuildVariantSummaryText.Visibility = visible;
            BuildVariantCombo.ItemsSource = variants;
            BuildVariantCombo.SelectedItem = variants.FirstOrDefault();
            BuildVariantSummaryText.Text = variants.FirstOrDefault()?.Summary ?? "";
        }
        finally
        {
            _updatingSelections = false;
        }
    }

    private void BuildVariantCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections) return;
        if (BuildVariantCombo.SelectedItem is BuildVariant variant)
            BuildVariantSummaryText.Text = variant.Summary;
        RefreshAll();
    }

    private void NavigationCategoryCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections) return;
        if (NavigationCategoryCombo.SelectedItem is not NavigationCategory category) return;

        var current = NavigationCombo.SelectedItem as NavigationOption;
        var options = VisibleNavigations(category.Id);
        if (options.Count == 0) options = NavigationProfiles.ForCategory(category.Id).ToList();
        NavigationCombo.ItemsSource = options;
        NavigationCombo.SelectedItem = current is not null &&
                                       current.CategoryId.Equals(category.Id, StringComparison.OrdinalIgnoreCase)
            ? current
            : options.FirstOrDefault();
    }

    private void NavigationCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections) return;
        if (NavigationCombo.SelectedItem is NavigationOption navigation)
            NavigationSummaryText.Text = navigation.Summary;
        RefreshAll();
    }

    private void GoroseiCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections) return;
        if (GoroseiCombo.SelectedItem is GoroseiOption option)
            GoroseiSummaryText.Text = option.Summary;
        RefreshAll();
    }

    private void AutoScan_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.AutoScanEnabled = AutoScanCheck.IsChecked == true;
        if (AutoScanCheck.IsChecked == true)
        {
            _timer.Start();
            _ = ScanAsync();
        }
        else
        {
            _timer.Stop();
            _scanGeneration++;
            _scanCancellation?.Cancel();
            _automaticStale = _automatic.Count > 0;
            RecognitionStatus.Text = "수동 모드";
            RecognitionStatus.Foreground = Brushes.Khaki;
            RefreshAll(_automaticStale ? "실시간 인식을 중지했습니다. 마지막 정상 패를 표시 중입니다." : "실시간 인식을 중지했습니다.");
        }
    }

    private void ClearDataRefresh_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var enabled = ClearDataRefreshCheck.IsChecked == true;
        var changed = _settings.ClearDataAutoRefresh != enabled;
        _settings.ClearDataAutoRefresh = enabled;
        if (changed && enabled) _ = RefreshClearDataAsync();
    }

    private void ClickThrough_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.ClickThroughOverlay = ClickThroughCheck.IsChecked == true;
        _overlay.SetClickThrough(_settings.ClickThroughOverlay);
        SettingsStore.Save(_settings);
    }

    private void Telemetry_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.TelemetryEnabled = TelemetryCheck.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    /// <summary>세션 경계 확정 시점의 패·추천 상태로 익명 레코드를 만들어 전송한다. fail-silent.</summary>
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
                _telemetrySessionStart, DateTimeOffset.UtcNow, hand.Sum(x => x.Count),
                _outcome.Outcome, _outcome.OutcomeSource);
            _ = _telemetry.EnqueueAndFlushAsync(record);
        }
        catch { /* fail-silent */ }
    }

    private void LiveVerify_OnChanged(object sender, RoutedEventArgs e)
    {
        _liveVerification.Enabled = LiveVerifyCheck.IsChecked == true;
        UpdateLiveVerifyUi();
    }

    private void LiveVerifyMatch_OnClick(object sender, RoutedEventArgs e) => ConfirmLiveVerification(true);

    private void LiveVerifyMismatch_OnClick(object sender, RoutedEventArgs e) => ConfirmLiveVerification(false);

    private void ConfirmLiveVerification(bool match)
    {
        try
        {
            var path = _liveVerification.Confirm(match);
            if (path is null)
            {
                LiveVerifyStatus.Text = "아직 기록할 인식 결과가 없습니다. 게임에서 패가 잡힌 뒤 판정하세요.";
                return;
            }
        }
        catch (Exception exception)
        {
            LiveVerifyStatus.Text = $"기록 실패: {exception.Message}";
            return;
        }
        UpdateLiveVerifyUi();
    }

    private void UpdateLiveVerifyUi()
    {
        var enabled = _liveVerification.Enabled;
        LiveVerifyMatchBtn.IsEnabled = enabled;
        LiveVerifyMismatchBtn.IsEnabled = enabled;
        if (!enabled)
        {
            LiveVerifyStatus.Text = "검증 모드를 켜면 패 변화를 감지해 게임 화면과의 대조를 요청합니다.";
            return;
        }
        LiveVerifyStatus.Text = _liveVerification.Pending is { } pending
            ? $"⚠ {pending.Description}\n{_liveVerification.ChecklistSummary}"
            : $"{_liveVerification.ChecklistSummary} · 변화 대기 중 (기록: {_liveVerification.LogFilePath})";
    }

    private void EnableOverlayMove_OnClick(object sender, RoutedEventArgs e)
    {
        var wasClickThrough = _settings.ClickThroughOverlay;
        ClickThroughCheck.IsChecked = false;
        _relockAfterMove = wasClickThrough;
        _settings.ClickThroughOverlay = false;
        if (!_overlay.IsVisible)
        {
            _overlay.Show();
            OverlayButton.Content = "오버레이 숨기기";
        }
        _overlay.EnsureVisible();
        _overlay.Stats.EnsureVisible();
        _overlay.SetClickThrough(false);
        _overlay.Activate();
        FooterStatus.Text = "두 오버레이 창(내 패 상태 · 추천)의 상단을 각각 끌어 옮기세요.";
    }

    private void Overlay_OnPositionCommitted(double left, double top)
    {
        _settings.OverlayLeft = left;
        _settings.OverlayTop = top;
        SettingsStore.Save(_settings);
        FooterStatus.Text = $"추천 창 위치를 저장했습니다: 가로 {left:0}, 세로 {top:0}";
        if (!_relockAfterMove) return;
        _relockAfterMove = false;
        ClickThroughCheck.IsChecked = true;
    }

    /// <summary>
    /// 위치를 한 번도 정하지 않았으면 패 상태 창은 화면 왼쪽 가장자리, 추천 창은 오른쪽 가장자리에
    /// 세로 중앙으로 놓는다. 게임 화면 가운데를 비워 두기 위한 기본값이다.
    /// </summary>
    private void ApplyDefaultOverlayLayout()
    {
        if (System.Windows.Forms.Screen.PrimaryScreen is not { } screen) return;
        var dpi = VisualTreeHelper.GetDpi(_overlay);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
        var work = screen.WorkingArea;
        var left = work.Left / scaleX;
        var right = work.Right / scaleX;
        var top = work.Top / scaleY;
        var height = work.Height / scaleY;
        const double margin = 6;

        if (_settings.StatsOverlayLeft is null)
        {
            _overlay.Stats.Left = left + margin;
            _overlay.Stats.Top = top + Math.Max(0, (height - _overlay.Stats.Height) / 2);
        }
        if (_settings.OverlayLeft is null)
        {
            _overlay.Left = right - _overlay.Width - margin;
            _overlay.Top = top + Math.Max(0, (height - _overlay.Height) / 2);
        }
    }

    private void StatsOverlay_OnPositionCommitted(double left, double top)
    {
        _settings.StatsOverlayLeft = left;
        _settings.StatsOverlayTop = top;
        SettingsStore.Save(_settings);
        FooterStatus.Text = $"패 상태 창 위치를 저장했습니다: 가로 {left:0}, 세로 {top:0}";
        if (!_relockAfterMove) return;
        _relockAfterMove = false;
        ClickThroughCheck.IsChecked = true;
    }

    private void SaveOverlayPosition()
    {
        if (_overlay is null || !_overlay.IsLoaded) return;
        var position = _overlay.CurrentPosition();
        _settings.OverlayLeft = position.Left;
        _settings.OverlayTop = position.Top;
        if (!_overlay.Stats.IsLoaded) return;
        var statsPosition = _overlay.Stats.CurrentPosition();
        _settings.StatsOverlayLeft = statsPosition.Left;
        _settings.StatsOverlayTop = statsPosition.Top;
    }

    private void ToggleOverlay_OnClick(object sender, RoutedEventArgs e) =>
        ToggleOverlayVisibility();

    // 설정 창도 모니터 해상도(FHD~8K)에 맞춰 창 전체를 비례 확대한다.
    private void ApplyResolutionScale()
    {
        var scale = UiScale.Apply(this, 1080, 720);
        MinWidth = 920 * scale;
        MinHeight = 620 * scale;
    }

    private void ToggleOverlayVisibility()
    {
        if (_overlay.IsVisible)
        {
            _overlay.Hide();
            OverlayButton.Content = "오버레이 보이기";
        }
        else
        {
            _overlay.Show();
            _overlay.EnsureVisible();
            _overlay.SetClickThrough(_settings.ClickThroughOverlay);
            OverlayButton.Content = "오버레이 숨기기";
        }
    }

    // 워크 창이 포커스를 가진 상태에서도 오버레이를 켜고 끌 수 있는 전역 단축키.
    // 기본 Scroll Lock — 워크 기본 단축키(Alt·Ctrl+숫자·F9~F12·문자키)와 겹치지 않는다.
    private const int OverlayHotkeyId = 0xB0BA;
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Windows 11 DWM으로 제목바를 앱 테마 색에 맞춘다(네이티브 버튼·동작 유지).
    // 미지원 OS(Win10 등)에서는 다크 모드 캡션까지만 적용되고 색 지정은 무시된다.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value,
        int size);

    private static void ApplyThemedTitleBar(IntPtr handle)
    {
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)); // 다크 캡션
        var caption = 0x001F1612; // COLORREF(BGR) — 앱 배경 #12161F
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        var text = 0x00FAF4F2; // 본문 밝은 텍스트 #F2F4FA
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        var border = 0x00F65C8B; // 브랜드 보라 #8B5CF6
        _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }

    private IntPtr _hotkeyWindowHandle;
    private bool _hotkeyRegistered;
    private bool _capturingHotkey;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_initialized) return;
        var helper = new WindowInteropHelper(this);
        ApplyThemedTitleBar(helper.Handle);
        _hotkeyWindowHandle = helper.Handle;
        HwndSource.FromHwnd(helper.Handle)?.AddHook(OnWindowMessage);
        Closed += (_, _) =>
        {
            if (_hotkeyRegistered) UnregisterHotKey(_hotkeyWindowHandle, OverlayHotkeyId);
        };
        // 예전 기본값(Scroll Lock)은 키보드에 따라 입력이 안 들어와 Caps Lock으로 이전.
        if (string.IsNullOrWhiteSpace(_settings.OverlayToggleKey) ||
            _settings.OverlayToggleKey.Equals("Scroll", StringComparison.OrdinalIgnoreCase))
            _settings.OverlayToggleKey = "Capital";
        ApplyOverlayHotkey();
    }

    /// <summary>설정된 키로 전역 단축키를 다시 등록하고 화면 문구를 갱신한다.</summary>
    private void ApplyOverlayHotkey()
    {
        if (_hotkeyWindowHandle == IntPtr.Zero) return;
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hotkeyWindowHandle, OverlayHotkeyId);
            _hotkeyRegistered = false;
        }
        if (!Enum.TryParse<Key>(_settings.OverlayToggleKey, ignoreCase: true, out var key))
            key = Key.Capital;
        var label = HotkeyLabel(key);
        HotkeyBox.Text = label;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || !RegisterHotKey(_hotkeyWindowHandle, OverlayHotkeyId, 0, virtualKey))
        {
            HotkeyHint.Text = $"{label} 키는 다른 프로그램이 쓰고 있어 등록하지 못했습니다. 다른 키로 바꿔 주세요.";
            FooterStatus.Text = $"오버레이 단축키({label}) 등록 실패 — 버튼으로 토글하세요.";
            OverlayButton.ToolTip = "전역 단축키 등록 실패";
            return;
        }
        _hotkeyRegistered = true;
        HotkeyHint.Text = "게임 중에도 이 키로 오버레이를 켜고 끕니다.";
        OverlayButton.ToolTip = $"전역 단축키: {label}";
        FooterStatus.Text = $"오버레이 토글 단축키: {label} (게임 중에도 동작)";
    }

    private static string HotkeyLabel(Key key) => key switch
    {
        Key.Capital => "Caps Lock",
        Key.Scroll => "Scroll Lock",
        Key.Snapshot => "Print Screen",
        Key.Pause => "Pause",
        Key.OemTilde => "`",
        _ => key.ToString()
    };

    private void HotkeyCapture_OnClick(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyCaptureButton.Content = "입력 대기";
        HotkeyBox.Text = "키를 누르세요";
        HotkeyHint.Text = "쓸 키를 한 번 누르면 저장됩니다. Esc로 취소.";
        HotkeyBox.Focus();
    }

    private void Hotkey_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // 조합키 단독은 단축키가 될 수 없다. 누르고 있는 동안 무시하고 다음 키를 기다린다.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return;
        _capturingHotkey = false;
        HotkeyCaptureButton.Content = "키 변경";
        if (key == Key.Escape)
        {
            ApplyOverlayHotkey();
            return;
        }
        _settings.OverlayToggleKey = key.ToString();
        SettingsStore.Save(_settings);
        ApplyOverlayHotkey();
    }

    private void Hotkey_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        HotkeyCaptureButton.Content = "키 변경";
        ApplyOverlayHotkey();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == OverlayHotkeyId)
        {
            ToggleOverlayVisibility();
            handled = true;
        }
        return IntPtr.Zero;
    }

}
