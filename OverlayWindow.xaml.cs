using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace OrandOverlay;

public partial class OverlayWindow : OverlayWindowBase
{
    private readonly HashSet<string> _expandedRouteIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedBuildNodes = new(StringComparer.OrdinalIgnoreCase);
    private string _lastRecommendationSignature = "";
    private string _lastStatsSignature = "";
    private string _lastRerollSignature = "";

    public OverlayWindow()
    {
        InitializeComponent();
        var appVersion = UpdateService.CurrentVersion;
        OverlayVersionText.Text = $"v{appVersion.Major}.{appVersion.Minor}.{appVersion.Build} 테스트2";
        // 패 상태 창은 추천 창과 함께 뜨고 함께 사라진다(위치만 각자 기억).
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) Stats.Show();
            else Stats.Hide();
        };
    }

    protected override double DesignWidth => 520;
    protected override double DesignHeight => 700;
    protected override UIElement? ClickThroughIndicator => ClickThroughBadge;

    /// <summary>내 패 상태 창. 추천과 분리해 각자 배치할 수 있다.</summary>
    public StatsOverlayWindow Stats { get; } = new();

    public override void SetClickThrough(bool enabled)
    {
        base.SetClickThrough(enabled);
        Stats.SetClickThrough(enabled);
    }

    // 렌더링 코드는 아래 패널들을 그대로 쓴다. 실제 표시는 패 상태 창이 맡는다.
    private StackPanel CombinePanel => Stats.CombinePanel;
    private TextBlock CombineHeader => Stats.CombineHeader;
    private StackPanel EmergencyPanel => Stats.EmergencyPanel;
    private TextBlock EmergencyHeader => Stats.EmergencyHeader;
    private UniformGrid CoreKpiPanel => Stats.CoreKpiPanel;
    private StackPanel CurrentStatsPanel => Stats.CurrentStatsPanel;
    private StackPanel RareRerollPanel => Stats.RareRerollPanel;
    private StackPanel SpecialPanel => Stats.SpecialPanel;
    private TextBlock SpecialHeader => Stats.SpecialHeader;
    private StackPanel GreenBloodPanel => Stats.GreenBloodPanel;
    private TextBlock GreenBloodHeader => Stats.GreenBloodHeader;

    public event Action<FrameworkElement>? ReRecommendRequested;
    public event Action? SettlementRequested;

    private void ReRecommendButton_OnClick(object sender, RoutedEventArgs e) =>
        ReRecommendRequested?.Invoke((FrameworkElement)sender);

    private void SettlementButton_OnClick(object sender, RoutedEventArgs e) =>
        SettlementRequested?.Invoke();

    /// <summary>패 변화가 없는 스캔 틱에서 상태줄(시각)만 가볍게 갱신할 때 쓴다.</summary>
    public void UpdateStatus(string status) => StatusText.Text = status;

    public void Render(string goalName, IReadOnlyList<Recommendation> recommendations,
        InventoryStatSummary stats, IReadOnlyList<RareRerollAdvice> rareRerolls,
        IReadOnlyList<GreenBloodAdvice> greenBloodAdvice, bool greenBloodOwned,
        IReadOnlyList<AutoCombineStep> combinePlan, string status, bool magicGoal = false,
        IReadOnlyList<EmergencySummonAdvice>? emergencySummons = null,
        GoroseiMode gorosei = GoroseiMode.None,
        bool greenBloodUsed = false,
        IReadOnlyList<SpecialDismantleAdvice>? specialAdvice = null,
        double stunTarget = 1.4,
        double stunCap = 1.5,
        string? phaseHint = null)
    {
        GoalText.Text = goalName;
        PhaseHintText.Text = phaseHint ?? "";
        PhaseHintText.Visibility = phaseHint is { Length: > 0 }
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Text = status;
        RenderCurrentStats(stats, magicGoal, gorosei, stunTarget, stunCap);
        RenderRareRerolls(rareRerolls, recommendations.Count > 0);
        RenderSpecialAdvice(specialAdvice ?? []);
        RenderGreenBloodAdvice(greenBloodAdvice, greenBloodOwned, greenBloodUsed);
        RenderCombinePlan(combinePlan);
        RenderEmergencySummons(emergencySummons ?? []);

        var signature = RecommendationSignature(recommendations);
        if (signature == _lastRecommendationSignature) return;
        _lastRecommendationSignature = signature;
        var scrollOffset = RecommendationScroll.VerticalOffset;
        RecommendationPanel.Children.Clear();

        if (recommendations.Count == 0)
        {
            RecommendationPanel.Children.Add(new TextBlock
            {
                Text = "패 인식 대기 중",
                Foreground = OverlayTheme.MutedBrush,
                FontSize = 12,
                Margin = new Thickness(2, 8, 0, 0)
            });
        }
        else
        {
            RecommendationPanel.Children.Add(FeaturedRecommendation(recommendations[0]));
            if (recommendations.Count > 1)
            {
                RecommendationPanel.Children.Add(OverlayTheme.RecTableHeader());
                for (var i = 1; i < recommendations.Count; i++)
                    RecommendationPanel.Children.Add(TableRecommendation(recommendations[i], i + 1, i % 2 == 1));
            }
        }

        Dispatcher.BeginInvoke(new Action(() =>
            RecommendationScroll.ScrollToVerticalOffset(scrollOffset)));
    }

    private string? _lastCombinePlanSignature;

    /// <summary>
    /// 지금 재료가 전부 모인 조합을 "무엇을 선택해 어떤 키"까지 보여준다.
    /// 자동 입력 실행기가 붙기 전까지는 이 안내가 조합하기의 실체다.
    /// </summary>
    private void RenderCombinePlan(IReadOnlyList<AutoCombineStep> plan)
    {
        var signature = string.Join("|", plan.Select(step =>
            $"{step.TargetUnitId}:{step.TriggerUnitId}:{step.Key}:{string.Join(",", step.Commands)}"));
        if (signature == _lastCombinePlanSignature) return;
        _lastCombinePlanSignature = signature;
        CombinePanel.Children.Clear();
        var visible = plan.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CombineHeader.Visibility = visible;
        CombinePanel.Visibility = visible;
        var index = 1;
        foreach (var step in plan.Take(6))
        {
            var commands = step.Commands.Count > 0
                ? step.Commands
                : step.Key is { Length: > 0 } key ? (IReadOnlyList<string>)[key] : [];
            CombinePanel.Children.Add(
                OverlayTheme.CombineRow(index++, step.TargetName, step.TriggerName, commands));
        }
        if (plan.Count > 6)
            CombinePanel.Children.Add(new TextBlock
            {
                Text = $"그 외 {plan.Count - 6}건",
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 10
            });
    }

    private string? _lastSpecialSignature;

    /// <summary>특수함 유닛의 분해(갱벳지 재료 확보) 여부를 안내한다.</summary>
    private void RenderSpecialAdvice(IReadOnlyList<SpecialDismantleAdvice> advice)
    {
        var signature = string.Join("|", advice.Select(item =>
            $"{item.UnitId}:{item.Dismantle}:{item.Reason}"));
        if (signature == _lastSpecialSignature) return;
        _lastSpecialSignature = signature;
        SpecialPanel.Children.Clear();
        var visible = advice.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SpecialHeader.Visibility = visible;
        SpecialPanel.Visibility = visible;
        foreach (var item in advice)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
            stack.Children.Add(new TextBlock
            {
                Text = item.Dismantle ? $"{item.Name} — 분해 추천" : $"{item.Name} — 유지",
                Foreground = new SolidColorBrush(item.Dismantle
                    ? Color.FromRgb(251, 191, 36)
                    : Color.FromRgb(249, 168, 212)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = item.Reason,
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            SpecialPanel.Children.Add(stack);
        }
    }

    private string? _lastEmergencySignature;

    /// <summary>긴급소집 항법에서 특별함 와일드카드 3장을 어디에 쓸지 안내한다.</summary>
    private void RenderEmergencySummons(IReadOnlyList<EmergencySummonAdvice> advice)
    {
        var signature = string.Join("|", advice.Select(item =>
            $"{item.UnitId}:{item.Count}:{item.Reason}"));
        if (signature == _lastEmergencySignature) return;
        _lastEmergencySignature = signature;
        EmergencyPanel.Children.Clear();
        var visible = advice.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmergencyHeader.Visibility = visible;
        EmergencyPanel.Visibility = visible;
        foreach (var item in advice)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
            stack.Children.Add(new TextBlock
            {
                Text = item.Count > 1 ? $"{item.Name} ×{item.Count}" : item.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(254, 202, 202)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = item.Reason,
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            EmergencyPanel.Children.Add(stack);
        }
    }

    private string? _lastGreenBloodSignature;

    private void RenderGreenBloodAdvice(IReadOnlyList<GreenBloodAdvice> advice, bool owned,
        bool used)
    {
        var signature = owned + "|" + used + "|" + string.Join("|", advice.Select(item =>
            $"{item.UnitId}:{item.Reason}:{item.Warning}"));
        if (signature == _lastGreenBloodSignature) return;
        _lastGreenBloodSignature = signature;
        GreenBloodPanel.Children.Clear();
        // 신 기준 그린블러드는 에그헤드 클리어로 획득이 확정이라 계획을 항상 노출한다.
        // 사용됨 상태도 조용히 숨기지 않고 이유를 보여준다(왜 안 뜨는지 알 수 있게).
        if (used)
        {
            GreenBloodHeader.Text = "그린블러드 · 사용됨";
            GreenBloodHeader.Visibility = Visibility.Visible;
            GreenBloodPanel.Visibility = Visibility.Collapsed;
            return;
        }
        GreenBloodHeader.Text = owned ? "그린블러드 사용처 · 보유 중" : "그린블러드 사용처";
        var visible = advice.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GreenBloodHeader.Visibility = visible;
        GreenBloodPanel.Visibility = visible;
        for (var i = 0; i < advice.Count; i++)
        {
            var item = advice[i];
            var stack = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0) };
            stack.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {item.Name}",
                Foreground = new SolidColorBrush(Color.FromRgb(134, 239, 172)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = item.Reason,
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            if (item.Warning is not null)
                stack.Children.Add(new TextBlock
                {
                    Text = "⚠ " + item.Warning,
                    Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
            GreenBloodPanel.Children.Add(stack);
        }
    }

    private void RenderRareRerolls(IReadOnlyList<RareRerollAdvice> advice, bool hasPlan)
    {
        var signature = hasPlan + "|" + string.Join("|", advice.Select(item =>
            $"{item.UnitId}:{item.OwnedCount}:{item.NeededCount}:{item.RerollCount}:{item.Sell}"));
        if (signature == _lastRerollSignature) return;
        _lastRerollSignature = signature;
        RareRerollPanel.Children.Clear();

        if (!hasPlan)
        {
            RareRerollPanel.Children.Add(RerollMessage("추천 계산 후 표시", false));
            return;
        }
        if (advice.Count == 0)
        {
            RareRerollPanel.Children.Add(RerollMessage("버릴 희귀패 없음", true));
            return;
        }

        foreach (var item in advice.Take(8))
        {
            var stack = new StackPanel();
            var title = new Grid();
            title.ColumnDefinitions.Add(new ColumnDefinition());
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.Children.Add(new TextBlock
            {
                Text = item.Name,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            var count = new TextBlock
            {
                Text = item.Sell ? $"판매 ×{item.RerollCount}" : $"리롤 ×{item.RerollCount}",
                Foreground = new SolidColorBrush(item.Sell
                    ? Color.FromRgb(74, 222, 128)
                    : Color.FromRgb(251, 191, 36)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 0, 0, 0)
            };
            Grid.SetColumn(count, 1);
            title.Children.Add(count);
            stack.Children.Add(title);
            stack.Children.Add(new TextBlock
            {
                Text = item.Reason,
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            RareRerollPanel.Children.Add(new Border
            {
                BorderBrush = OverlayTheme.HairlineBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 5, 0, 5),
                Child = stack
            });
        }

        if (advice.Count > 8)
            RareRerollPanel.Children.Add(RerollMessage($"그 외 {advice.Count - 8}종", false));
    }

    private static UIElement RerollMessage(string message, bool safe)
    {
        return new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(safe
                ? Color.FromRgb(74, 222, 128)
                : Color.FromRgb(166, 177, 196)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 2, 0, 4)
        };
    }

    private void RenderCurrentStats(InventoryStatSummary stats, bool magicGoal, GoroseiMode gorosei,
        double stunTarget, double stunCap)
    {
        var signature = stats + "|" + magicGoal + "|" + gorosei + "|" + stunTarget + "|" + stunCap;
        if (signature == _lastStatsSignature) return;
        _lastStatsSignature = signature;
        CurrentStatsPanel.Children.Clear();
        CoreKpiPanel.Children.Clear();

        // 오로성(신+ 판별 효과)에 따라 이감·깎기 목표가 올라간다.
        var slowTarget = GoroseiEffects.AdjustSlowTarget(102, gorosei);
        var armorTarget = GoroseiEffects.AdjustArmorTarget(211, gorosei);
        var magicArmorTarget = GoroseiEffects.AdjustMagicArmorTarget(1, gorosei);
        if (GoroseiEffects.StatsNote(gorosei) is { } goroseiNote)
            CurrentStatsPanel.Children.Add(new TextBlock
            {
                Text = goroseiNote,
                Foreground = OverlayTheme.WarnBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

        CoreKpiPanel.Children.Add(OverlayTheme.Kpi("스턴", stats.Stun, stunTarget, ""));
        CoreKpiPanel.Children.Add(OverlayTheme.Kpi("이감", stats.TotalSlow, slowTarget,
            stats.TriggeredSlow > 0
                ? $"고정 {FormatNumber(stats.Slow)} · 발동 {FormatNumber(stats.TriggeredSlow)}"
                : $"고정 {FormatNumber(stats.Slow)}"));
        CoreKpiPanel.Children.Add(magicGoal
            ? OverlayTheme.Kpi("마방깎", stats.MagicArmorReduction, magicArmorTarget,
                gorosei == GoroseiMode.Warcury
                    ? "워큐리 보정: 마방깎 10 필요"
                    : "마방깎 1기 이상")
            : OverlayTheme.Kpi("방깎", stats.TotalArmorReduction, armorTarget,
                ArmorBreakdown(stats)));

        var pairs = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        if (magicGoal)
        {
            CurrentStatsPanel.Children.Add(SectionLabel("딜 밸런스"));
            pairs.Children.Add(ProviderRow("단일", stats.SingleDamageProviders, "만피 몹 현퍼뎀"));
            pairs.Children.Add(ProviderRow("끝딜", stats.FinisherDamageProviders, "딸피 막타"));
            CurrentStatsPanel.Children.Add(pairs);
            if (stats.SingleDamageProviders > 0 && stats.FinisherDamageProviders > 0)
                CurrentStatsPanel.Children.Add(new TextBlock
                {
                    Text = "단·끝 구성: 스킬 대상 직접 컨트롤 필요",
                    Foreground = OverlayTheme.WarnBrush,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 4)
                });
            pairs = new UniformGrid { Columns = 2 };
        }

        CurrentStatsPanel.Children.Add(SectionLabel("공격 지원"));
        pairs.Children.Add(ValueRow("공증", stats.TotalAttackBoost,
            stats.TriggeredAttackBoost > 0
                ? $"고정 {FormatNumber(stats.AttackBoost)} · 발동 {FormatNumber(stats.TriggeredAttackBoost)}"
                : null));
        pairs.Children.Add(ValueRow("공속", stats.AttackSpeed));
        if (magicGoal) pairs.Children.Add(ValueRow("마뎀증", stats.MagicAmp));
        pairs.Children.Add(ValueRow("체젠", stats.HealthRegen));
        pairs.Children.Add(ValueRow("마젠", stats.ManaRegen));
        CurrentStatsPanel.Children.Add(pairs);

        CurrentStatsPanel.Children.Add(SectionLabel("특수 역할"));
        var roles = new UniformGrid { Columns = 2 };
        if (!magicGoal)
            roles.Children.Add(ProviderRow("암브", stats.ArmorBreakProviders, "누적형"));
        roles.Children.Add(ProviderRow("보잡", stats.BossControlProviders));
        roles.Children.Add(ProviderRow("광보잡", stats.BerserkControlProviders));
        roles.Children.Add(ProviderRow("공중이동", stats.AirMovementProviders));
        roles.Children.Add(ProviderRow("순간이동", stats.TeleportProviders));
        roles.Children.Add(ProviderRow("바제스", stats.BurgessProviders));
        CurrentStatsPanel.Children.Add(roles);
    }

    private static string ArmorBreakdown(InventoryStatSummary stats)
    {
        var values = new List<string> { $"고정 {FormatNumber(stats.ArmorReduction)}" };
        if (stats.TriggeredArmorReduction > 0)
            values.Add($"발동 {FormatNumber(stats.TriggeredArmorReduction)}");
        if (stats.StackingArmorReduction > 0)
            values.Add($"중첩 {FormatNumber(stats.StackingArmorReduction)}");
        if (stats.SingleArmorReduction > 0)
            values.Add($"단일 {FormatNumber(stats.SingleArmorReduction)} (합계 제외)");
        return string.Join(" · ", values);
    }

    private static UIElement ValueRow(string name, double value, string? detail = null) =>
        OverlayTheme.Pair(name, FormatNumber(value), OverlayTheme.GoldBrush, detail);

    private static UIElement ProviderRow(string name, int providers, string? detail = null) =>
        OverlayTheme.Pair(name, $"{providers}기",
            providers > 0 ? OverlayTheme.OkBrush : OverlayTheme.MutedBrush, detail);

    private static TextBlock SectionLabel(string text) => OverlayTheme.Section(text);

    private static string FormatNumber(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string RecommendationSignature(IReadOnlyList<Recommendation> recommendations) =>
        string.Join("|", recommendations.Select(item => string.Join("~",
            item.Route.Id,
            item.Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            item.RecipeProgress.OwnedLeafCount,
            item.RecipeProgress.RequiredLeafCount,
            string.Join(",", item.RecipeProgress.Leaves.Select(leaf =>
                $"{leaf.UnitId}:{leaf.OwnedCount}:{leaf.RequiredCount}")),
            string.Join(",", item.RemainingCraftSteps.Select(step =>
                $"{step.UnitId}:{step.OwnedCount}:{step.RequiredCount}")),
            string.Join(",", item.CompositionUnits.Select(unit => $"{unit.UnitId}:{unit.OwnedCount}")),
            string.Join(",", item.Warnings),
            string.Join(",", item.CombineCommands),
            item.ClearEvidence is null
                ? ""
                : $"{item.ClearEvidence.SampleCount}:{item.ClearEvidence.SharePercent}")));

    private UIElement FeaturedRecommendation(Recommendation item)
    {
        var unit = item.CompositionUnits[0];
        var header = OverlayTheme.FeaturedBlock(
            UnitImageFactory.Create(unit.Image, unit.Name, 64, unit.UnitId),
            "1  " + RecommendationPresentation.CraftUnitName(unit),
            RecommendationPresentation.RecommendationEffectLine(unit),
            item.CombineCommands,
            RecommendationPresentation.CompletionPercent(item.RecipeProgress),
            item.NextAction);
        if (item.Warnings.Count > 0)
        {
            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(new TextBlock
            {
                Text = item.Warnings[0],
                Foreground = OverlayTheme.WarnBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(76, 4, 0, 0)
            });
            header = stack;
        }
        return OverlayTheme.FeaturedShell(RecommendationExpander(item, unit, header));
    }

    private UIElement TableRecommendation(Recommendation item, int rank, bool alt)
    {
        var unit = item.CompositionUnits[0];
        var header = OverlayTheme.CompactRow(
            rank,
            UnitImageFactory.Create(unit.Image, unit.Name, 32, unit.UnitId),
            RecommendationPresentation.CraftUnitName(unit),
            item.CombineCommands,
            RecommendationPresentation.CompletionPercent(item.RecipeProgress));
        return OverlayTheme.TableShell(RecommendationExpander(item, unit, header), alt);
    }

    private Expander RecommendationExpander(Recommendation item, CompositionUnitDetail unit, UIElement header)
    {
        var expander = new Expander
        {
            Header = header,
            Content = RecommendationDetailsPanel(item),
            IsExpanded = _expandedRouteIds.Contains(item.Route.Id),
            Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(expander,
            $"{RecommendationPresentation.CraftUnitName(unit)}, " +
            $"{RecommendationPresentation.RecommendationEffectLine(unit)}, " +
            $"{RecommendationPresentation.CompletionPercent(item.RecipeProgress)}, 조합식과 부족 패 상세");
        expander.Expanded += (_, _) => _expandedRouteIds.Add(item.Route.Id);
        expander.Collapsed += (_, _) => _expandedRouteIds.Remove(item.Route.Id);
        return expander;
    }

    private static UIElement CountBar(RecipeProgress progress)
    {
        var ratio = progress.CompletionRatio;
        var fill = ratio >= 0.8
            ? Color.FromRgb(74, 222, 128)
            : ratio >= 0.5 ? Color.FromRgb(251, 191, 36) : Color.FromRgb(248, 113, 113);
        var columns = new Grid { Height = 6, IsHitTestVisible = false };
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0, ratio), GridUnitType.Star)
        });
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0, 1 - ratio), GridUnitType.Star)
        });
        columns.Children.Add(new Border { Background = new SolidColorBrush(fill), CornerRadius = new CornerRadius(3) });
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(65, 72, 88)),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 9, 0, 0),
            Child = columns
        };
        AutomationProperties.SetName(bar,
            $"제작 완성도 {RecommendationPresentation.CompletionPercent(progress)}");
        return bar;
    }

    private UIElement RecommendationDetailsPanel(Recommendation item)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        // 부족한 패 나열은 길어질 수 있어 카드를 펼쳤을 때만 보여준다(유저 요청).
        stack.Children.Add(CountBar(item.RecipeProgress));
        stack.Children.Add(new TextBlock
        {
            Text = item.NextAction,
            Foreground = OverlayTheme.MutedBrush,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 7),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var warning in item.Warnings)
            stack.Children.Add(new TextBlock
            {
                Text = "⚠ " + warning,
                Foreground = OverlayTheme.WarnBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 7)
            });
        stack.Children.Add(DetailSectionTitle("남은 조합 · 전설 먼저"));
        stack.Children.Add(RemainingRecipePanel(item));
        // 드릴다운은 구조 탐색용, 실제 조합 순서는 안흔함부터 번호로 나열(유저 요청).
        if (item.RemainingCraftSteps.Count > 0)
        {
            stack.Children.Add(DetailSectionTitle("조합 순서 · 안흔함부터",
                new Thickness(0, 10, 0, 4)));
            for (var i = 0; i < item.RemainingCraftSteps.Count; i++)
                stack.Children.Add(RemainingRecipeCard(item.RemainingCraftSteps[i], i + 1));
        }
        stack.Children.Add(MissingLeavesPanel(item.RecipeProgress));
        stack.Children.Add(DetailSectionTitle("유닛 능력", new Thickness(0, 10, 0, 0)));
        if (item.CompositionUnits.Count > 0)
            stack.Children.Add(CompositionUnitRow(item.CompositionUnits[0]));
        return stack;
    }

    // 남은 조합 단계식 표시(유저 요청): 전설급 먼저, 펼치면 하위 희귀함, 또 펼치면 재료.
    private UIElement RemainingRecipePanel(Recommendation item)
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
            stack.Children.Add(DrillNodeElement(item.Route.Id, group, null));
        foreach (var step in others)
            stack.Children.Add(RemainingRecipeCard(step, null));
        return stack;
    }

    // 드릴다운 노드: 하위 단계가 있으면 펼쳐서 다음 단계를, 마지막 단계면 재료를 보여준다.
    private UIElement DrillNodeElement(string parentKey, BuildDrilldown.DrillNode node, int? number)
    {
        var key = parentKey + "|" + node.Step.UnitId;
        UIElement content;
        if (node.Children.Count > 0)
        {
            var children = new StackPanel { Margin = new Thickness(14, 0, 0, 4) };
            foreach (var child in node.Children)
                children.Children.Add(DrillNodeElement(key, child, null));
            content = children;
        }
        else
        {
            content = IngredientDetail(node.Step);
        }
        var expander = new Expander
        {
            Header = RemainingRecipeCard(node.Step, number),
            Content = content,
            IsExpanded = _expandedBuildNodes.Contains(key),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        expander.Expanded += (_, _) => _expandedBuildNodes.Add(key);
        expander.Collapsed += (_, _) => _expandedBuildNodes.Remove(key);
        return expander;
    }

    private static UIElement IngredientDetail(RecipeCraftStep step) => new TextBlock
    {
        Text = "재료: " + string.Join(" · ", step.Ingredients
            .OrderBy(ingredient => ingredient.SelectionOrder)
            .Select(ingredient =>
                RecommendationPresentation.SafeText(ingredient.Name).Trim() +
                (ingredient.RequiredCount > 1 ? $" ×{ingredient.RequiredCount}" : ""))),
        Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(14, 3, 0, 5)
    };

    private static UIElement RemainingRecipeCard(RecipeCraftStep node, int? stepNumber)
    {
        var select = RecommendationPresentation.CraftSelectUnitName(node);
        var action = select is null ? "조합할 하위 유닛 없음" : select + " 선택";
        var companions = RecommendationPresentation.CraftCompanionNames(node);
        if (companions is not null) action += " · " + companions;
        var commands = node.CombineCommands.Count > 0
            ? node.CombineCommands
            : node.CombineKey is { Length: > 0 } key ? (IReadOnlyList<string>)[key] : [];
        return OverlayTheme.CraftRow(
            UnitImageFactory.Create(node.Image, node.Name, 32, node.UnitId),
            (stepNumber is { } number ? $"{number}. " : "") +
            RecommendationPresentation.CraftUnitName(node.Name, node.Tier),
            action,
            commands,
            $"{Math.Round(node.CompletionRatio * 100, MidpointRounding.AwayFromZero):0}%",
            $"{node.OwnedCount}/{node.RequiredCount}");
    }

    private static TextBlock DetailSectionTitle(string text, Thickness? margin = null)
    {
        var block = OverlayTheme.Section(text);
        if (margin is { } value) block.Margin = value;
        return block;
    }

    private static UIElement CompositionUnitRow(CompositionUnitDetail unit)
    {
        var stack = new StackPanel();
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CraftUnitName(unit),
            Foreground = unit.IsGoal ? OverlayTheme.GoldBrush : OverlayTheme.WhiteBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        var ownership = new TextBlock
        {
            Text = RecommendationPresentation.Ownership(unit),
            Foreground = unit.OwnedCount > 0 ? OverlayTheme.OkBrush : OverlayTheme.MutedBrush,
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(ownership, 1);
        title.Children.Add(ownership);
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.AbilitySummary(unit),
            Foreground = OverlayTheme.MutedBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return stack;
    }

    private static UIElement MissingLeavesPanel(RecipeProgress progress)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(OverlayTheme.Section("부족한 최하위 재료"));
        if (progress.MissingLeaves.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "최하위 재료 모두 확보",
                Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                FontSize = 11
            });
            return stack;
        }

        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var leaf in progress.MissingLeaves)
            grid.Children.Add(MissingLeafCard(leaf));
        stack.Children.Add(grid);
        return stack;
    }

    private static UIElement MissingLeafCard(RecipeLeafProgress leaf) =>
        OverlayTheme.MissingChip(
            UnitImageFactory.Create(leaf.Image, leaf.Name, 28, leaf.UnitId),
            RecommendationPresentation.CraftUnitName(leaf.Name, leaf.Tier),
            leaf.MissingCount);

}
