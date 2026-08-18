using System.Globalization;
using System.Runtime.InteropServices;
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
    private readonly Dictionary<string, InventoryEntry> _manual = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new();
    private RecommendationEngine _engine = null!;
    private InventoryStatsCalculator _statsCalculator = null!;
    private RareRerollAdvisor _rareRerollAdvisor = null!;
    private GreenBloodAdvisor _greenBloodAdvisor = null!;
    private GreenBloodAdvisor.UsageTracker _greenBloodUsage = null!;
    private SpecialDismantleAdvisor _specialAdvisor = null!;
    private AutoCombinePlanner _combinePlanner = null!;
    private ClearBuildStats _clearStats = ClearBuildStats.Empty;
    private CompletedTopUnitTracker _completedTopUnits = null!;
    private IInventoryRecognizer _recognizer = null!;
    private readonly Dictionary<string, IInventoryRecognizer> _recognizers = new(StringComparer.OrdinalIgnoreCase);
    private OverlayWindow _overlay = null!;
    private bool _initialized;
    private bool _scanInProgress;
    private bool _automaticStale;
    private bool _automaticDisconnected;
    private bool _liveSessionActive;
    private bool _relockAfterMove;
    private bool _updatingSelections;
    private readonly HashSet<string> _expandedRouteIds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCancellation;
    private int _scanGeneration;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        try
        {
            _catalog.Load();
            _clearStats = ClearBuildStats.Load(ClearSamplePaths());
            _engine = new RecommendationEngine(_catalog, _clearStats.HasData ? _clearStats : null);
            _statsCalculator = new InventoryStatsCalculator(_catalog);
            _rareRerollAdvisor = new RareRerollAdvisor(_catalog);
            _greenBloodAdvisor = new GreenBloodAdvisor(_catalog);
            _greenBloodUsage = new GreenBloodAdvisor.UsageTracker(_catalog);
            _specialAdvisor = new SpecialDismantleAdvisor(_catalog);
            _combinePlanner = new AutoCombinePlanner(_catalog, CombineHotkeyCatalog.Load(
                Path.Combine(AppContext.BaseDirectory, "Data", "tmo-combine-hotkeys.json")));
            _completedTopUnits = new CompletedTopUnitTracker(_catalog);
            _recognizers["Screen"] = new ScreenRecognitionService(_catalog);
            _recognizers["Memory"] = new TmoAssistedMemoryRecognitionService(_catalog);
            var initialSource = RecognitionPolicy.NormalizeSource(_settings.RecognitionSource);
            _settings.RecognitionSource = initialSource;
            _recognizer = _recognizers[initialSource];
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "데이터 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var goalUnits = GoalUnits();
        GoalCombo.ItemsSource = goalUnits;
        // 수동 보정은 인식 실패를 사람이 메꾸는 경로다 — 데모 13종이 아니라
        // 카탈로그 전체에서 고를 수 있어야 한다.
        ManualUnitCombo.ItemsSource = _catalog.AllUnits
            .Where(unit => unit.Rawcodes.Count > 0)
            .OrderBy(unit => unit.Tier, StringComparer.CurrentCulture)
            .ThenBy(unit => unit.Name, StringComparer.CurrentCulture)
            .ToList();
        GoalCombo.SelectedItem = goalUnits.FirstOrDefault(x => x.Id == _settings.GoalUnitId) ?? goalUnits.FirstOrDefault();
        RepopulateNavigationChoices();
        RepopulateBuildVariants();
        GoroseiCombo.ItemsSource = GoroseiEffects.Options;
        var selectedGorosei = GoroseiEffects.Parse(_settings.GoroseiMode);
        GoroseiCombo.SelectedItem = GoroseiEffects.Options.First(option => option.Mode == selectedGorosei);
        GoroseiSummaryText.Text = GoroseiEffects.Options.First(option => option.Mode == selectedGorosei).Summary;
        ManualUnitCombo.SelectedIndex = 0;
        RegionText.Text = string.Join(", ", new[]
        {
            _settings.InventoryRegion.X, _settings.InventoryRegion.Y,
            _settings.InventoryRegion.Width, _settings.InventoryRegion.Height
        }.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)));
        ClickThroughCheck.IsChecked = _settings.ClickThroughOverlay;
        RecognitionSourceCombo.SelectedIndex = _settings.RecognitionSource == "Screen" ? 1 : 0;
        AutoScanCheck.IsChecked = _settings.AutoScanEnabled;
        ClearDataRefreshCheck.IsChecked = _settings.ClearDataAutoRefresh;
        DataVersionText.Text = $"데이터 {_catalog.Data.DataVersion} · {_catalog.Data.Disclaimer}" +
                               ClearStatsSummary();

        _overlay = new OverlayWindow();
        _overlay.RestorePosition(_settings.OverlayLeft, _settings.OverlayTop);
        _overlay.PositionCommitted += Overlay_OnPositionCommitted;
        _overlay.HiddenByUser += () => OverlayButton.Content = "오버레이 보이기";
        _overlay.Show();
        _overlay.SetClickThrough(_settings.ClickThroughOverlay);
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.CaptureIntervalSeconds, 0.5, 10));
        _timer.Tick += async (_, _) => await ScanAsync();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            SaveOverlayPosition();
            _overlay.CloseForApplication();
            SettingsStore.Save(_settings);
        };
        _initialized = true;
        RefreshAll("티모지지 실시간 패 연동을 준비하는 중입니다.");
        if (_settings.AutoScanEnabled)
        {
            _timer.Start();
            _ = ScanAsync();
        }
        _ = RefreshClearDataAsync();
        _ = AutoUpdateAsync();
    }

    // 새 릴리스가 있으면 확인 없이 내려받아 교체하고 자동 재시작한다(유저 지시).
    // 같은 태그를 이미 시도했다면(교체 실패·버전 미상승 등) 반복하지 않는다.
    private async Task AutoUpdateAsync()
    {
        var service = new UpdateService();
        var update = await service.CheckAsync();
        if (update is null) return;
        if (update.Tag.Equals(_settings.LastAttemptedUpdateTag, StringComparison.OrdinalIgnoreCase))
        {
            await Dispatcher.InvokeAsync(() =>
                FooterStatus.Text = $"{update.Tag} 자동 업데이트가 이전에 완료되지 않았습니다 — 릴리스 페이지에서 수동으로 받아주세요.");
            return;
        }
        if (!UpdateService.CanSelfInstall)
        {
            await Dispatcher.InvokeAsync(() =>
                FooterStatus.Text = $"새 버전 {update.Tag} 공개 — 단일 exe 배포가 아니어서 자동 교체를 건너뜁니다.");
            return;
        }
        await Dispatcher.InvokeAsync(() =>
            FooterStatus.Text = $"{update.Tag} 업데이트를 내려받는 중입니다. 완료되면 자동으로 재시작합니다.");
        try
        {
            _settings.LastAttemptedUpdateTag = update.Tag;
            SettingsStore.Save(_settings);
            await service.DownloadAndInstallAsync(update);
            await Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
                FooterStatus.Text = $"{update.Tag} 자동 업데이트에 실패했습니다 — 다음 실행 때 다시 시도하지 않습니다.");
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
        TmoClearRefreshService.CacheFile
    ];

    private string ClearStatsSummary() => _clearStats.HasData
        ? $" · 신+ 클리어 {_clearStats.TotalGodPlusSamples:#,0}판 학습" +
          $"({_clearStats.OldestSampleAt:MM.dd}~{_clearStats.NewestSampleAt:MM.dd})"
        : " · 클리어 데이터 없음(수작업 우선순위 사용)";

    /// <summary>
    /// 앱 시작 후 한 번, 번들 스냅샷 이후의 신규 신+ 클리어 기록을 증분 수신한다.
    /// 실패해도 기존 번들+캐시 데이터로 계속 동작한다.
    /// </summary>
    private async Task RefreshClearDataAsync()
    {
        if (!_settings.ClearDataAutoRefresh) return;
        var newerThan = _clearStats.NewestSampleAt ?? DateTimeOffset.UtcNow.AddDays(-14);
        var fresh = await new TmoClearRefreshService().FetchNewSamplesAsync(newerThan);
        if (fresh.Count == 0) return;
        TmoClearRefreshService.MergeIntoCache(TmoClearRefreshService.CacheFile, fresh,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        var reloaded = await Task.Run(() => ClearBuildStats.Load(ClearSamplePaths()));
        if (!reloaded.HasData) return;
        await Dispatcher.InvokeAsync(() =>
        {
            _clearStats = reloaded;
            _engine = new RecommendationEngine(_catalog, _clearStats);
            DataVersionText.Text = $"데이터 {_catalog.Data.DataVersion} · {_catalog.Data.Disclaimer}" +
                                   ClearStatsSummary();
            // 새 데이터로 학습된 상위·항법 목록을 갱신한다(선택은 최대한 유지).
            _updatingSelections = true;
            var currentGoalId = SelectedGoal?.Id ?? _settings.GoalUnitId;
            var goalUnits = GoalUnits();
            GoalCombo.ItemsSource = goalUnits;
            GoalCombo.SelectedItem = goalUnits.FirstOrDefault(x => x.Id == currentGoalId)
                                     ?? goalUnits.FirstOrDefault();
            _updatingSelections = false;
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
        IEnumerable<InventoryEntry> automatic = includeAutomatic
            ? _automatic.Values
            : Enumerable.Empty<InventoryEntry>();
        return InventoryMerge.ApplyCorrections(automatic, _manual.Values,
                id => _catalog.Unit(id).Tags.Contains("greenblood", StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(x => _manual.ContainsKey(x.UnitId))
            .ThenBy(x => _catalog.Unit(x.UnitId).Name)
            .ToList();
    }

    private void RefreshAll(string? message = null)
    {
        if (!_initialized) return;
        var goal = GoalCombo.SelectedItem as UnitDefinition;
        if (goal is null) return;
        var navigation = NavigationCombo.SelectedItem as NavigationOption ??
                         NavigationProfiles.Find(_settings.NavigationMode);
        _settings.GoalUnitId = goal.Id;
        _settings.NavigationMode = navigation.Id;
        GoalCompletedButton.Content = _completedTopUnits.Contains(goal.Id)
            ? "완료 표시 해제"
            : "목표 조합 완료로 표시";
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
        var gorosei = (GoroseiCombo.SelectedItem as GoroseiOption)?.Mode
                      ?? GoroseiEffects.Parse(_settings.GoroseiMode);
        _settings.GoroseiMode = gorosei.ToString();
        var buildVariant = (BuildVariantCombo.SelectedItem as BuildVariant)?.Id
                           ?? BuildVariants.AutoId;
        var recommendations = _engine.RecommendNearestCrafts(goal.Id, recommendationInventory,
            navigationMode: navigation.Id, gorosei: gorosei, buildVariant: buildVariant);
        var inventoryStats = _statsCalculator.Calculate(recommendationInventory);
        var rareRerolls = _rareRerollAdvisor.Evaluate(recommendationInventory, recommendations);
        IReadOnlyList<GreenBloodAdvice> greenBloodAdvice = _greenBloodUsage.Used
            ? Array.Empty<GreenBloodAdvice>()
            : _greenBloodAdvisor.Evaluate(goal, recommendationInventory,
                recommendations, _clearStats.HasData ? _clearStats : null);
        GreenBloodUsedButton.Content = _greenBloodUsage.Used
            ? "그린블러드 사용 표시 해제"
            : "그린블러드 사용 완료로 표시";

        InventoryList.Items.Clear();
        foreach (var item in inventory)
        {
            var origin = _manual.ContainsKey(item.UnitId)
                ? "수동"
                : "자동" + (_automaticStale ? " · 이전 스냅샷" : "");
            InventoryList.Items.Add($"{_catalog.Unit(item.UnitId).Name}  ×{item.Count}   {origin}");
        }
        if (inventory.Count == 0) InventoryList.Items.Add("보유 패 없음");

        RecommendationCards.Children.Clear();
        if (recommendations.Count == 0)
            RecommendationCards.Children.Add(new TextBlock
            {
                Text = "패 인식 대기 중",
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 12
            });
        for (var i = 0; i < recommendations.Count; i++)
            RecommendationCards.Children.Add(BuildRecommendationCard(recommendations[i], i + 1));
        // "지금 조합 가능"은 1번 우선순위 추천의 조합 단계만 보여준다(사용자 요청).
        // 여러 추천의 단계를 섞어 보여주면 지금 뭘 눌러야 하는지 흐려진다.
        var combinePlan = _combinePlanner.Plan(recommendations.Take(1).ToList(),
            recommendationInventory);
        var emergencySummons = navigation.Id.Equals("AlliedForces.EmergencyCall",
            StringComparison.OrdinalIgnoreCase)
            ? _engine.RecommendEmergencySummons(recommendations, recommendationInventory)
            : Array.Empty<EmergencySummonAdvice>();
        _overlay.Render($"{goal.Name} · {navigation.Name}", recommendations, inventoryStats, rareRerolls,
            greenBloodAdvice,
            !_greenBloodUsage.Used &&
            GreenBloodAdvisor.HasUnusedGreenBlood(_catalog, recommendationInventory),
            combinePlan, RecognitionStatus.Text, DamageTiers.IsMagic(goal.Tier), emergencySummons,
            gorosei, _greenBloodUsage.Used,
            _specialAdvisor.Evaluate(recommendationInventory, recommendations, goal,
                _clearStats.HasData ? _clearStats : null),
            _engine.ActiveStunTarget, _engine.ActiveStunCap);
        if (message is not null) FooterStatus.Text = message;
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
        headerBody.Children.Add(new TextBlock
        {
            Text = item.NextAction,
            Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)),
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 4),
            TextWrapping = TextWrapping.Wrap
        });

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

    private static UIElement BuildRecommendationDetails(Recommendation item)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(BuildDetailSectionTitle("남은 조합"));
        stack.Children.Add(BuildRemainingRecipe(item.RemainingCraftSteps));

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

        foreach (var warning in item.Warnings)
            stack.Children.Add(new TextBlock
            {
                Text = "⚠ " + warning,
                Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                FontSize = 11,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        return stack;
    }

    private static UIElement BuildRemainingRecipe(IReadOnlyList<RecipeCraftStep> steps)
    {
        var stack = new StackPanel();
        if (steps.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "바로 조합 가능",
                Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                FontSize = 11
            });
            return stack;
        }
        for (var i = 0; i < steps.Count; i++)
            stack.Children.Add(BuildRemainingRecipeCard(steps[i], i + 1));
        return stack;
    }

    private static UIElement BuildRemainingRecipeCard(RecipeCraftStep node, int stepNumber)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = UnitImageFactory.Create(node.Image, node.Name, 42);
        icon.Margin = new Thickness(0, 0, 9, 0);
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"{stepNumber}. {RecommendationPresentation.CraftUnitName(node.Name, node.Tier)}",
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = $"남은 제작 ×{node.MissingCount}",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        });
        text.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CraftIngredientLine(node),
            Foreground = new SolidColorBrush(Color.FromRgb(196, 181, 253)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var owned = new TextBlock
        {
            Text = $"보유 {node.OwnedCount}/{node.RequiredCount}",
            Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(owned, 2);
        row.Children.Add(owned);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(19, 24, 35)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 3, 0, 3),
            Child = row
        };
    }

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
        stack.Children.Add(UnitImageFactory.Create(leaf.Image, leaf.Name, 42));
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
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"보유 {leaf.OwnedCount}/{leaf.RequiredCount}",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 9,
            TextAlignment = TextAlignment.Center
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
            var result = await recognizer.RecognizeAsync(_settings, cancellation.Token);
            if (generation != _scanGeneration || !ReferenceEquals(recognizer, _recognizer)) return;
            LogUnknownRawcodes(result);
            if (result.ShouldReplaceInventory)
            {
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
                if (RecognitionPolicy.IsConfirmedOutOfGame(result) && _liveSessionActive) _manual.Clear();
                _automaticStale = false;
                _automaticDisconnected = true;
                if (RecognitionPolicy.IsConfirmedOutOfGame(result))
                {
                    _liveSessionActive = false;
                    _completedTopUnits.Reset();
                    _greenBloodUsage.Reset();
                }
            }
            else
            {
                _automaticStale = _automatic.Count > 0;
                _automaticDisconnected = !RecognitionPolicy.MayUseLastGoodForRecommendations(result.State);
            }
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
            RefreshAll(detail);
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
                RefreshAll("인식 중 오류가 발생했습니다. 다음 자동 인식을 기다립니다.");
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation)) _scanCancellation = null;
            cancellation.Dispose();
            _scanInProgress = false;
        }
    }

    private void AddUnit_OnClick(object sender, RoutedEventArgs e)
    {
        if (ManualUnitCombo.SelectedItem is not UnitDefinition unit) return;
        if (_manual.TryGetValue(unit.Id, out var entry)) entry.Count++;
        else _manual[unit.Id] = new InventoryEntry { UnitId = unit.Id, Count = 1, IsManual = true };
        if (_manual[unit.Id].Count == 0) _manual.Remove(unit.Id);
        RefreshAll($"{unit.Name} 패를 수동으로 추가했습니다.");
    }

    private void RemoveUnit_OnClick(object sender, RoutedEventArgs e)
    {
        if (ManualUnitCombo.SelectedItem is not UnitDefinition unit) return;
        if (!InventoryMerge.CanDecrement(CombinedInventory(), unit.Id))
        {
            RefreshAll($"{unit.Name} 수량은 이미 0이라 더 제거하지 않았습니다.");
            return;
        }
        if (_manual.TryGetValue(unit.Id, out var entry)) entry.Count--;
        else _manual[unit.Id] = new InventoryEntry { UnitId = unit.Id, Count = -1, IsManual = true };
        if (_manual[unit.Id].Count == 0) _manual.Remove(unit.Id);
        RefreshAll($"{unit.Name} 패를 제거했습니다.");
    }

    private void ClearManual_OnClick(object sender, RoutedEventArgs e)
    {
        _manual.Clear();
        RefreshAll("수동 보정 패를 모두 초기화했습니다.");
    }

    private void GoalCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections) return;
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

    private void RecognitionSource_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || RecognitionSourceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string source) return;
        if (!_recognizers.TryGetValue(source, out var recognizer)) return;
        _scanGeneration++;
        _scanCancellation?.Cancel();
        _settings.RecognitionSource = source;
        _recognizer = recognizer;
        _automatic.Clear();
        _automaticStale = false;
        _automaticDisconnected = false;
        _liveSessionActive = false;
        _completedTopUnits.Reset();
        RecognitionStatus.Text = source == "Memory" ? "메모리 대기" : "화면 대기";
        RefreshAll(source == "Memory"
            ? "읽기 전용 워크 메모리 프로필을 사용합니다. 지원하지 않는 버전은 자동으로 차단됩니다."
            : "화면 아이콘 템플릿 인식으로 전환했습니다.");
    }

    private void ClickThrough_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.ClickThroughOverlay = ClickThroughCheck.IsChecked == true;
        _overlay.SetClickThrough(_settings.ClickThroughOverlay);
        SettingsStore.Save(_settings);
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
        _overlay.SetClickThrough(false);
        _overlay.Activate();
        FooterStatus.Text = "오버레이 상단을 끌어 원하는 위치로 옮기세요.";
    }

    private void Overlay_OnPositionCommitted(double left, double top)
    {
        _settings.OverlayLeft = left;
        _settings.OverlayTop = top;
        SettingsStore.Save(_settings);
        FooterStatus.Text = $"오버레이 위치를 저장했습니다: 가로 {left:0}, 세로 {top:0}";
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
    }

    private void ToggleOverlay_OnClick(object sender, RoutedEventArgs e) =>
        ToggleOverlayVisibility();

    // 그린블러드 사용 순간을 스캔이 못 봤을 때의 수동 토글(게임 종료 시 자동 초기화).
    private void GreenBloodUsed_OnClick(object sender, RoutedEventArgs e)
    {
        _greenBloodUsage.Toggle();
        RefreshAll(_greenBloodUsage.Used
            ? "그린블러드를 사용한 것으로 표시했습니다. 사용처 안내를 숨깁니다."
            : "그린블러드 사용 표시를 해제했습니다.");
    }

    // 조합한 상위가 카드존을 안 거치고 필드로 나가면 메모리 인식이 못 본다.
    // 스캔이 놓친 완성 상위를 수동으로 표시/해제한다(게임 종료 시 자동 초기화).
    private void GoalCompleted_OnClick(object sender, RoutedEventArgs e)
    {
        if (GoalCombo.SelectedItem is not UnitDefinition goal) return;
        if (!_completedTopUnits.ToggleCompleted(goal.Id)) return;
        RefreshAll(_completedTopUnits.Contains(goal.Id)
            ? $"{goal.Name} 조합 완료로 표시했습니다. 추천이 다음 상위·지원으로 넘어갑니다."
            : $"{goal.Name} 완료 표시를 해제했습니다.");
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_initialized) return;
        var helper = new WindowInteropHelper(this);
        ApplyThemedTitleBar(helper.Handle);
        // 예전 기본값(Scroll Lock)은 키보드에 따라 입력이 안 들어와 Caps Lock으로 이전.
        if (string.IsNullOrWhiteSpace(_settings.OverlayToggleKey) ||
            _settings.OverlayToggleKey.Equals("Scroll", StringComparison.OrdinalIgnoreCase))
            _settings.OverlayToggleKey = "Capital";
        if (!Enum.TryParse<Key>(_settings.OverlayToggleKey, ignoreCase: true, out var key))
            key = Key.Capital;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || !RegisterHotKey(helper.Handle, OverlayHotkeyId, 0, virtualKey))
        {
            FooterStatus.Text = $"오버레이 단축키({_settings.OverlayToggleKey}) 등록 실패 — 버튼으로 토글하세요.";
            return;
        }
        HwndSource.FromHwnd(helper.Handle)?.AddHook(OnWindowMessage);
        Closed += (_, _) => UnregisterHotKey(helper.Handle, OverlayHotkeyId);
        var keyLabel = key switch
        {
            Key.Capital => "Caps Lock",
            Key.Scroll => "Scroll Lock",
            _ => key.ToString()
        };
        OverlayButton.ToolTip = $"전역 단축키: {keyLabel}";
        FooterStatus.Text = $"오버레이 토글 단축키: {keyLabel} (게임 중에도 동작)";
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

    private void ApplyRegion_OnClick(object sender, RoutedEventArgs e)
    {
        var parts = RegionText.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts.Any(x => !double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            FooterStatus.Text = "영역은 0.64, 0.58, 0.34, 0.36 형식으로 입력하세요.";
            return;
        }
        var values = parts.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        if (values.Any(x => x < 0 || x > 1) || values[0] + values[2] > 1 || values[1] + values[3] > 1)
        {
            FooterStatus.Text = "모든 값은 0~1이고 가로 위치+너비, 세로 위치+높이는 1 이하여야 합니다.";
            return;
        }
        _settings.InventoryRegion = new NormalizedRect(values[0], values[1], values[2], values[3]);
        SettingsStore.Save(_settings);
        FooterStatus.Text = "해상도 독립 인벤토리 영역을 저장했습니다.";
    }

    private void CopyTemplatePath_OnClick(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(AppPaths.TemplateDirectory);
        FooterStatus.Text = $"템플릿 폴더 경로를 복사했습니다: {AppPaths.TemplateDirectory}";
    }
}
