using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrandOverlay;

public partial class OverlayWindow : OverlayWindowBase
{
    private string _lastRecommendationSignature = "";
    private string _lastStatsSignature = "";
    private string _lastRerollSignature = "";
    private IReadOnlyList<Recommendation> _recommendations = [];
    private IReadOnlyList<AutoCombineStep> _combinePlan = [];
    private string? _selectedRouteId;
    private string? _clusterHeadRouteId;
    private Func<Recommendation, IReadOnlyList<Recommendation>>? _storyChildren;
    private Func<IReadOnlyList<Recommendation>, string?, IReadOnlyList<Recommendation>>? _recascade;

    public OverlayWindow()
    {
        InitializeComponent();
        var appVersion = UpdateService.CurrentVersion;
        OverlayVersionText.Text = $"v{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}";
        Loaded += (_, _) =>
        {
            OverlayTheme.AttachRoundClip(NowWell, OverlayTheme.WellRadius);
            OverlayTheme.AttachRoundClip(FlowWell, OverlayTheme.WellRadius);
            OverlayTheme.AttachRoundClip(BoardWell, OverlayTheme.WellRadius);
        };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) Stats.Show();
            else Stats.Hide();
        };
    }

    protected override double DesignWidth => 540;
    protected override double DesignHeight => 740;
    protected override UIElement? ClickThroughIndicator => ClickThroughBadge;

    public StatsOverlayWindow Stats { get; } = new();

    public override void SetClickThrough(bool enabled)
    {
        base.SetClickThrough(enabled);
        Stats.SetClickThrough(enabled);
    }

    private WrapPanel EmergencyPanel => Stats.EmergencyPanel;
    private TextBlock EmergencyHeader => Stats.EmergencyHeader;
    private UniformGrid CoreKpiPanel => Stats.CoreKpiPanel;
    private StackPanel CurrentStatsPanel => Stats.CurrentStatsPanel;
    private WrapPanel RareRerollPanel => Stats.RareRerollPanel;
    private WrapPanel SpecialPanel => Stats.SpecialPanel;
    private TextBlock SpecialHeader => Stats.SpecialHeader;
    private WrapPanel GreenBloodPanel => Stats.GreenBloodPanel;
    private TextBlock GreenBloodHeader => Stats.GreenBloodHeader;

    public event Action<FrameworkElement>? ReRecommendRequested;

    private void ReRecommendButton_OnClick(object sender, RoutedEventArgs e) =>
        ReRecommendRequested?.Invoke((FrameworkElement)sender);

    private void SettlementButton_OnClick(object sender, RoutedEventArgs e)
    {
        // 레이아웃 유지용 플레이스홀더 — 정산 동작은 임시 비활성화됐다.
    }

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
        string? phaseHint = null,
        Func<Recommendation, IReadOnlyList<Recommendation>>? storyChildren = null,
        Func<IReadOnlyList<Recommendation>, string?, IReadOnlyList<Recommendation>>? recascade = null)
    {
        _storyChildren = storyChildren;
        _recascade = recascade;
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
        RenderEmergencySummons(emergencySummons ?? []);

        var signature = RecommendationSignature(recommendations) + "|" +
                        string.Join("|", combinePlan.Select(step =>
                            $"{step.TargetUnitId}:{step.TriggerUnitId}:{step.Key}:{string.Join(",", step.Commands)}"));
        if (signature == _lastRecommendationSignature) return;
        _lastRecommendationSignature = signature;
        _recommendations = recommendations;
        _combinePlan = combinePlan;
        PreserveBoardSelection();
        ApplySelectionCascade();
        FillBoard();
    }

    private void FillBoard()
    {
        var head = ClusterHead();
        var children = head is null
            ? []
            : _storyChildren?.Invoke(head) ?? [];
        if (!BoardSelection.IsKnown(_recommendations, children, _selectedRouteId))
            _selectedRouteId = head?.Route.Id;
        RecommendationBoard.Fill(NowPanel, FlowPanel, BoardPanel, _recommendations, _combinePlan,
            _selectedRouteId, SelectRoute, PhaseHintText.Visibility == Visibility.Visible
                ? PhaseHintText.Text
                : null, children, head?.Route.Id);
    }

    private void SelectRoute(string routeId)
    {
        _selectedRouteId = routeId;
        var head = ClusterHead();
        var currentChildren = head is null
            ? []
            : _storyChildren?.Invoke(head) ?? [];
        if (BoardSelection.Contains(_recommendations, routeId) &&
            !BoardSelection.Contains(currentChildren, routeId))
            _clusterHeadRouteId = BoardSelection.Find(_recommendations, routeId)!.Route.Id;
        ApplySelectionCascade();
        FillBoard();
    }

    private void PreserveBoardSelection()
    {
        var head = ClusterHead();
        var children = head is null
            ? []
            : _storyChildren?.Invoke(head) ?? [];
        if (BoardSelection.IsKnown(_recommendations, children, _selectedRouteId)) return;
        _selectedRouteId = head?.Route.Id;
        _clusterHeadRouteId = head?.Route.Id;
    }

    private Recommendation? ClusterHead()
    {
        if (_recommendations.Count == 0) return null;
        var id = BoardSelection.ClusterHeadId(
            _recommendations, [], _selectedRouteId, _clusterHeadRouteId);
        return BoardSelection.Find(_recommendations, id) ?? _recommendations[0];
    }

    private void ApplySelectionCascade()
    {
        if (_recascade is null || _recommendations.Count == 0) return;
        var head = ClusterHead();
        _recommendations = _recascade(_recommendations, head?.Route.Id ?? _selectedRouteId);
        _clusterHeadRouteId = ClusterHead()?.Route.Id;
    }

    private string? _lastSpecialSignature;

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
            SpecialPanel.Children.Add(AdviceChip(
                item.Dismantle ? $"{item.Name} 분해" : $"{item.Name} 유지",
                item.Dismantle ? OverlayTheme.WarnBrush : OverlayTheme.MutedBrush));
    }

    private string? _lastEmergencySignature;

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
            EmergencyPanel.Children.Add(AdviceChip(
                item.Count > 1 ? $"{item.Name} ×{item.Count}" : item.Name,
                OverlayTheme.WarnBrush));
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
        if (used)
        {
            GreenBloodHeader.Text = "그린블러드 · 사용됨";
            GreenBloodHeader.Visibility = Visibility.Visible;
            GreenBloodPanel.Visibility = Visibility.Collapsed;
            return;
        }
        GreenBloodHeader.Text = owned ? "그린블러드 · 보유 중" : "그린블러드";
        var visible = advice.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GreenBloodHeader.Visibility = visible;
        GreenBloodPanel.Visibility = visible;
        foreach (var item in advice)
            GreenBloodPanel.Children.Add(AdviceChip(item.Name,
                item.Seraphim ? OverlayTheme.GoldBrush : OverlayTheme.OkBrush));
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
            RareRerollPanel.Children.Add(AdviceChip("추천 계산 후 표시", OverlayTheme.MutedBrush));
            return;
        }
        if (advice.Count == 0)
        {
            RareRerollPanel.Children.Add(AdviceChip("버릴 희귀패 없음", OverlayTheme.OkBrush));
            return;
        }

        foreach (var item in advice.Take(8))
            RareRerollPanel.Children.Add(AdviceChip(
                item.Sell ? $"{item.Name} 판매×{item.RerollCount}" : $"{item.Name} 리롤×{item.RerollCount}",
                item.Sell ? OverlayTheme.OkBrush : OverlayTheme.WarnBrush));
        if (advice.Count > 8)
            RareRerollPanel.Children.Add(AdviceChip($"외 {advice.Count - 8}종", OverlayTheme.MutedBrush));
    }

    private static UIElement AdviceChip(string text, Brush color) => new Border
    {
        Background = OverlayTheme.RowBrush,
        BorderBrush = OverlayTheme.HairlineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(OverlayTheme.ChipRadius),
        Padding = new Thickness(8, 4, 8, 4),
        Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock
        {
            Text = text,
            Foreground = color,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        }
    };

    private void RenderCurrentStats(InventoryStatSummary stats, bool magicGoal, GoroseiMode gorosei,
        double stunTarget, double stunCap)
    {
        var signature = stats + "|" + magicGoal + "|" + gorosei + "|" + stunTarget + "|" + stunCap;
        if (signature == _lastStatsSignature) return;
        _lastStatsSignature = signature;
        CurrentStatsPanel.Children.Clear();
        CoreKpiPanel.Children.Clear();

        var slowTarget = GoroseiEffects.AdjustSlowTarget(102, gorosei);
        var armorTarget = GoroseiEffects.AdjustArmorTarget(211, gorosei);
        var magicArmorTarget = GoroseiEffects.AdjustMagicArmorTarget(1, gorosei);
        if (GoroseiEffects.StatsNote(gorosei) is { } goroseiNote)
            CurrentStatsPanel.Children.Add(AdviceChip(goroseiNote, OverlayTheme.WarnBrush));

        CoreKpiPanel.Children.Add(OverlayTheme.Kpi("스턴", stats.Stun, stunTarget, ""));
        CoreKpiPanel.Children.Add(OverlayTheme.Kpi("이감", stats.TotalSlow, slowTarget,
            stats.TriggeredSlow > 0
                ? $"고정 {FormatNumber(stats.Slow)} · 발동 {FormatNumber(stats.TriggeredSlow)}"
                : $"고정 {FormatNumber(stats.Slow)}"));
        CoreKpiPanel.Children.Add(magicGoal
            ? OverlayTheme.Kpi("마방깎", stats.MagicArmorReduction, magicArmorTarget,
                gorosei == GoroseiMode.Warcury ? "워큐리 보정: 마방깎 10" : "마방깎 1기 이상")
            : OverlayTheme.Kpi("방깎", stats.TotalArmorReduction, armorTarget,
                ArmorBreakdown(stats)));

        if (magicGoal)
        {
            CurrentStatsPanel.Children.Add(StatChip("단일", $"{stats.SingleDamageProviders}기",
                stats.SingleDamageProviders > 0));
            CurrentStatsPanel.Children.Add(StatChip("끝딜", $"{stats.FinisherDamageProviders}기",
                stats.FinisherDamageProviders > 0));
        }

        CurrentStatsPanel.Children.Add(StatChip("공증", FormatNumber(stats.TotalAttackBoost), true));
        CurrentStatsPanel.Children.Add(StatChip("공속", FormatNumber(stats.AttackSpeed), true));
        if (magicGoal) CurrentStatsPanel.Children.Add(StatChip("마뎀증", FormatNumber(stats.MagicAmp), true));
        CurrentStatsPanel.Children.Add(StatChip("체젠", FormatNumber(stats.HealthRegen), true));
        CurrentStatsPanel.Children.Add(StatChip("마젠", FormatNumber(stats.ManaRegen), true));
        if (!magicGoal)
            CurrentStatsPanel.Children.Add(StatChip("암브", $"{stats.ArmorBreakProviders}기",
                stats.ArmorBreakProviders > 0));
        CurrentStatsPanel.Children.Add(StatChip("보잡", $"{stats.BossControlProviders}기",
            stats.BossControlProviders > 0));
        CurrentStatsPanel.Children.Add(StatChip("광보잡", $"{stats.BerserkControlProviders}기",
            stats.BerserkControlProviders > 0));
        CurrentStatsPanel.Children.Add(StatChip("공중이동", $"{stats.AirMovementProviders}기",
            stats.AirMovementProviders > 0));
        CurrentStatsPanel.Children.Add(StatChip("순간이동", $"{stats.TeleportProviders}기",
            stats.TeleportProviders > 0));
        CurrentStatsPanel.Children.Add(StatChip("바제스", $"{stats.BurgessProviders}기",
            stats.BurgessProviders > 0));
    }

    private static UIElement StatChip(string name, string value, bool accent)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = OverlayTheme.MutedBrush,
            FontSize = 12
        });
        var number = new TextBlock
        {
            Text = value,
            Foreground = accent ? OverlayTheme.GoldBrush : OverlayTheme.MutedBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold
        };
        Grid.SetColumn(number, 1);
        row.Children.Add(number);
        return row;
    }

    private static string ArmorBreakdown(InventoryStatSummary stats)
    {
        var values = new List<string> { $"고정 {FormatNumber(stats.ArmorReduction)}" };
        if (stats.TriggeredArmorReduction > 0)
            values.Add($"발동 {FormatNumber(stats.TriggeredArmorReduction)}");
        if (stats.StackingArmorReduction > 0)
            values.Add($"중첩 {FormatNumber(stats.StackingArmorReduction)}");
        if (stats.SingleArmorReduction > 0)
            values.Add($"단일 {FormatNumber(stats.SingleArmorReduction)}");
        return string.Join(" · ", values);
    }

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
}
