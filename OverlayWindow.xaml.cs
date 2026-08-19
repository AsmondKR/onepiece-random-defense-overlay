using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        OverlayVersionText.Text = $"v{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}";
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
            RecommendationPanel.Children.Add(new TextBlock
            {
                Text = "패 인식 대기 중",
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 12,
                Margin = new Thickness(2, 4, 0, 0)
            });

        for (var i = 0; i < recommendations.Count; i++)
            RecommendationPanel.Children.Add(RecommendationCard(recommendations[i], i + 1));

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
            $"{step.TargetUnitId}:{step.TriggerUnitId}:{step.Key}"));
        if (signature == _lastCombinePlanSignature) return;
        _lastCombinePlanSignature = signature;
        CombinePanel.Children.Clear();
        var visible = plan.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CombineHeader.Visibility = visible;
        CombinePanel.Visibility = visible;
        foreach (var step in plan.Take(6))
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
            stack.Children.Add(new TextBlock
            {
                Text = step.TargetName,
                Foreground = new SolidColorBrush(Color.FromRgb(186, 230, 253)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{step.TriggerName} 선택 → {step.Key} 키",
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            CombinePanel.Children.Add(stack);
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
                Background = new SolidColorBrush(Color.FromArgb(95, 69, 42, 10)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 6, 7, 6),
                Margin = new Thickness(0, 0, 0, 5),
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

        // 오로성(신+ 판별 효과)에 따라 이감·깎기 목표가 올라간다.
        var slowTarget = GoroseiEffects.AdjustSlowTarget(102, gorosei);
        var armorTarget = GoroseiEffects.AdjustArmorTarget(211, gorosei);
        var magicArmorTarget = GoroseiEffects.AdjustMagicArmorTarget(1, gorosei);
        if (GoroseiEffects.StatsNote(gorosei) is { } goroseiNote)
            CurrentStatsPanel.Children.Add(new TextBlock
            {
                Text = goroseiNote,
                Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

        // 스턴 목표·상한은 공략(엔진 프로필)이 정하고, 어떤 기물로 채울지는
        // 스턴 세트 선택이 알아서 짠다. 카드에는 수치만 보여준다.
        CurrentStatsPanel.Children.Add(CoreStatCard("스턴", stats.Stun, stunTarget, ""));
        CurrentStatsPanel.Children.Add(CoreStatCard("이감", stats.TotalSlow, slowTarget,
            stats.TriggeredSlow > 0
                ? $"고정 {FormatNumber(stats.Slow)} · 발동 {FormatNumber(stats.TriggeredSlow)}"
                : $"고정 {FormatNumber(stats.Slow)}"));
        // 마딜 상위는 방깎 211이 무의미하다(신+ 실측 중앙값 0). 마감 깎기 카드를
        // 마방깎으로 바꾸고, 목표는 "소스 1점 이상"(워큐리 시 10)이다.
        CurrentStatsPanel.Children.Add(magicGoal
            ? CoreStatCard("마방깎", stats.MagicArmorReduction, magicArmorTarget,
                gorosei == GoroseiMode.Warcury
                    ? "워큐리 보정: 마방깎 10 필요"
                    : "마방깎 유닛 1기 이상 (예: 갓 에넬 · 후지토라)")
            : CoreStatCard("방깎", stats.TotalArmorReduction, armorTarget,
                ArmorBreakdown(stats)));

        if (magicGoal)
        {
            // 고인물 검증: 클리어가 목적이면 단일·끝딜 기수 밸런스가 핵심 지표다.
            // 단일=만피 몹을 현퍼뎀 스킬로 직접 찝기, 끝딜=딸피 라인몹 막타.
            CurrentStatsPanel.Children.Add(SectionLabel("딜 밸런스"));
            CurrentStatsPanel.Children.Add(ProviderRow("단일", stats.SingleDamageProviders,
                "만피 몹 찝어서 현퍼뎀"));
            CurrentStatsPanel.Children.Add(ProviderRow("끝딜", stats.FinisherDamageProviders,
                "딸피 라인몹 막타"));
            if (stats.SingleDamageProviders > 0 && stats.FinisherDamageProviders > 0)
                CurrentStatsPanel.Children.Add(new TextBlock
                {
                    Text = "단·끝 구성: 스킬 대상 직접 컨트롤 필요",
                    Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
        }

        CurrentStatsPanel.Children.Add(SectionLabel("공격 지원"));
        CurrentStatsPanel.Children.Add(ValueRow("공증", stats.TotalAttackBoost,
            stats.TriggeredAttackBoost > 0
                ? $"고정 {FormatNumber(stats.AttackBoost)} · 발동 {FormatNumber(stats.TriggeredAttackBoost)}"
                : null));
        CurrentStatsPanel.Children.Add(ValueRow("공속", stats.AttackSpeed));
        if (magicGoal) CurrentStatsPanel.Children.Add(ValueRow("마뎀증", stats.MagicAmp));
        CurrentStatsPanel.Children.Add(ValueRow("체젠", stats.HealthRegen));
        CurrentStatsPanel.Children.Add(ValueRow("마젠", stats.ManaRegen));

        CurrentStatsPanel.Children.Add(SectionLabel("특수 역할"));
        if (!magicGoal)
            CurrentStatsPanel.Children.Add(ProviderRow("암브", stats.ArmorBreakProviders,
                "누적형 · 방깎 합계와 별도"));
        CurrentStatsPanel.Children.Add(ProviderRow("보잡", stats.BossControlProviders));
        CurrentStatsPanel.Children.Add(ProviderRow("광보잡", stats.BerserkControlProviders));
        CurrentStatsPanel.Children.Add(ProviderRow("공중이동", stats.AirMovementProviders));
        CurrentStatsPanel.Children.Add(ProviderRow("순간이동", stats.TeleportProviders));
        CurrentStatsPanel.Children.Add(ProviderRow("바제스", stats.BurgessProviders));
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

    private static UIElement CoreStatCard(string name, double current, double target, string detail)
    {
        var reached = current + 0.0001 >= target;
        var accent = reached ? Color.FromRgb(74, 222, 128) : Color.FromRgb(251, 191, 36);
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        });
        // 게임 중 흘끗 보는 핵심 숫자 — 카드에서 가장 커야 한다.
        var value = new TextBlock
        {
            Text = $"{FormatNumber(current)} / {FormatNumber(target)}",
            Foreground = new SolidColorBrush(accent),
            FontSize = 14,
            FontWeight = FontWeights.Bold
        };
        Grid.SetColumn(value, 1);
        title.Children.Add(value);

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(StatBar(current, target, accent));
        if (!string.IsNullOrWhiteSpace(detail))
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(160, 12, 16, 26)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = stack
        };
    }

    private static UIElement StatBar(double current, double target, Color accent)
    {
        var ratio = target <= 0 ? 1 : Math.Clamp(current / target, 0, 1);
        var grid = new Grid { Height = 5, Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(3)
        });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(65, 72, 88)),
            CornerRadius = new CornerRadius(3),
            Child = grid
        };
    }

    private static UIElement ValueRow(string name, double value, string? detail = null)
    {
        var stack = new StackPanel();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 12 });
        var number = new TextBlock
        {
            Text = FormatNumber(value),
            Foreground = new SolidColorBrush(Color.FromRgb(196, 181, 253)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(number, 1);
        row.Children.Add(number);
        stack.Children.Add(row);
        if (!string.IsNullOrWhiteSpace(detail))
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap
            });
        return new Border { Padding = new Thickness(4, 3, 4, 3), Child = stack };
    }

    private static UIElement ProviderRow(string name, int providers, string? detail = null)
    {
        var stack = new StackPanel();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 12 });
        var value = new TextBlock
        {
            Text = $"{providers}기",
            Foreground = providers > 0
                ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                : new SolidColorBrush(Color.FromRgb(166, 177, 196)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        stack.Children.Add(row);
        if (!string.IsNullOrWhiteSpace(detail))
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 196)),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap
            });
        return new Border { Padding = new Thickness(4, 3, 4, 3), Child = stack };
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(178, 154, 255)),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 10, 0, 4)
    };

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
            item.ClearEvidence is null
                ? ""
                : $"{item.ClearEvidence.SampleCount}:{item.ClearEvidence.SharePercent}")));

    private UIElement RecommendationCard(Recommendation item, int rank)
    {
        var headerStack = new StackPanel();
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock
        {
            Text = $"{rank}. {RecommendationPresentation.CraftUnitName(item.CompositionUnits[0])}",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        var score = new TextBlock
        {
            Text = RecommendationPresentation.CompletionPercent(item.RecipeProgress),
            Foreground = new SolidColorBrush(Color.FromRgb(178, 154, 255)),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(score, 1);
        title.Children.Add(score);
        headerStack.Children.Add(title);
        headerStack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(110, 55, 43, 90)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 6, 0, 0),
            Child = new TextBlock
            {
                Text = RecommendationPresentation.RecommendationEffectLine(item.CompositionUnits[0]),
                Foreground = new SolidColorBrush(Color.FromRgb(216, 206, 255)),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap
            }
        });
        if (item.ClearEvidence is not null)
            headerStack.Children.Add(new TextBlock
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
                       $"{item.ClearEvidence.SampleCount:#,0}판 · " +
                       $"채용률 {item.ClearEvidence.SharePercent}퍼센트",
                Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0)
            });
        headerStack.Children.Add(CountBar(item.RecipeProgress));

        if (item.Warnings.Count > 0)
            headerStack.Children.Add(new TextBlock
            {
                Text = "⚠ " + item.Warnings[0], Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            });

        var expander = new Expander
        {
            Header = headerStack,
            Content = RecommendationDetailsPanel(item),
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
            Background = new SolidColorBrush(Color.FromArgb(215, 33, 38, 52)),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 9), Child = expander
        };
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
        stack.Children.Add(new TextBlock
        {
            Text = item.NextAction,
            Foreground = Brushes.LightGray,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 7),
            TextWrapping = TextWrapping.Wrap
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
        text.Children.Add(CraftActionLine(node));
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
            Text = $"{node.OwnedCount}/{node.RequiredCount}",
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
            Background = new SolidColorBrush(Color.FromArgb(100, 14, 18, 28)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9),
            Margin = new Thickness(0, 5, 0, 0),
            Child = row
        };
    }

    // "선택할 유닛 나미 · 조합 키 [Z]"를 한 줄로 펼친다. 라벨은 죽이고
    // 실제 행동 대상(유닛 이름, 키)만 크게 보이게 한다.
    private static UIElement CraftActionLine(RecipeCraftStep node)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        var select = RecommendationPresentation.CraftSelectUnitName(node);
        if (select is null)
        {
            panel.Children.Add(CraftHintLabel("조합할 하위 유닛 없음"));
            return panel;
        }
        panel.Children.Add(CraftHintLabel("선택할 유닛"));
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
            panel.Children.Add(CraftHintLabel("조합 키", left: 12));
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
            panel.Children.Add(CraftHintLabel("함께 조합", left: 12));
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

    private static TextBlock CraftHintLabel(string text, double left = 0) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 0, 0)
    };

    private static TextBlock DetailSectionTitle(string text, Thickness? margin = null) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)),
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = margin ?? new Thickness(0)
    };

    private static UIElement CompositionUnitRow(CompositionUnitDetail unit)
    {
        var stack = new StackPanel();
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CraftUnitName(unit),
            Foreground = unit.IsGoal ? new SolidColorBrush(Color.FromRgb(196, 181, 253)) : Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        var ownership = new TextBlock
        {
            Text = RecommendationPresentation.Ownership(unit),
            Foreground = unit.OwnedCount > 0
                ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                : new SolidColorBrush(Color.FromRgb(173, 179, 192)),
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0)
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
            Margin = new Thickness(0, 2, 0, 0)
        });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(100, 14, 18, 28)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(0, 4, 0, 0),
            Child = stack
        };
    }

    private static UIElement MissingLeavesPanel(RecipeProgress progress)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = "부족한 최하위 재료",
            Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
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

    private static UIElement MissingLeafCard(RecipeLeafProgress leaf)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(UnitImageFactory.Create(leaf.Image, leaf.Name, 40, leaf.UnitId));
        stack.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CraftUnitName(leaf.Name, leaf.Tier),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"부족 ×{leaf.MissingCount}",
            Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center
        });
        return new Border
        {
            Width = 125,
            MinHeight = 108,
            Background = new SolidColorBrush(Color.FromArgb(100, 14, 18, 28)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5),
            Margin = new Thickness(0, 3, 5, 2),
            Child = stack
        };
    }

}
