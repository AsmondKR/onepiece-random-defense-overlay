using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OrandOverlay;

/// <summary>
/// 테스트3 구조: 세로 카드 리스트가 아니라
/// 지금 할 일 / 조합 흐름 / 초상화 보드 3구역.
/// </summary>
internal static class RecommendationBoard
{
    public static void Fill(
        Panel nowPanel,
        Panel flowPanel,
        Panel boardPanel,
        IReadOnlyList<Recommendation> recs,
        IReadOnlyList<AutoCombineStep> plan,
        string? selectedId,
        Action<string> onSelect,
        string? banner = null)
    {
        nowPanel.Children.Clear();
        flowPanel.Children.Clear();
        boardPanel.Children.Clear();

        if (recs.Count == 0)
        {
            nowPanel.Children.Add(new TextBlock
            {
                Text = "패 인식 대기 중",
                Foreground = OverlayTheme.MutedBrush,
                FontSize = 13,
                Margin = new Thickness(4, 8, 0, 8)
            });
            return;
        }

        var selected = recs.FirstOrDefault(item =>
            item.Route.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? recs[0];

        if (!string.IsNullOrWhiteSpace(banner))
            nowPanel.Children.Add(new TextBlock
            {
                Text = banner,
                Foreground = OverlayTheme.WarnBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

        nowPanel.Children.Add(NowBlock(selected, plan));
        flowPanel.Children.Add(FlowBlock(selected));
        if (selected.RecipeProgress.MissingLeaves.Count > 0)
        {
            var missing = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var leaf in selected.RecipeProgress.MissingLeaves.Take(10))
                missing.Children.Add(OverlayTheme.MissingChip(
                    UnitImageFactory.Create(leaf.Image, leaf.Name, 24, leaf.UnitId),
                    RecommendationPresentation.CraftUnitName(leaf.Name, leaf.Tier),
                    leaf.MissingCount));
            boardPanel.Children.Add(missing);
        }
        var tiles = new WrapPanel();
        for (var i = 0; i < recs.Count; i++)
            tiles.Children.Add(BoardTile(recs[i], i + 1,
                recs[i].Route.Id.Equals(selected.Route.Id, StringComparison.OrdinalIgnoreCase),
                onSelect));
        boardPanel.Children.Add(tiles);
    }

    private static UIElement NowBlock(Recommendation selected, IReadOnlyList<AutoCombineStep> plan)
    {
        var unit = selected.CompositionUnits[0];
        var icon = UnitImageFactory.Create(unit.Image, unit.Name, 56, unit.UnitId);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = plan.Count > 0 ? "지금 조합" : "다음 행동",
            Foreground = OverlayTheme.GoldBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CraftUnitName(unit),
            Foreground = OverlayTheme.WhiteBrush,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        });

        if (plan.Count > 0)
        {
            var step = plan[0];
            text.Children.Add(new TextBlock
            {
                Text = $"{step.TriggerName} 선택 → {step.TargetName}" +
                       (plan.Count > 1 ? $"  외 {plan.Count - 1}건" : ""),
                Foreground = OverlayTheme.MutedBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4)
            });
            var commands = step.Commands.Count > 0
                ? step.Commands
                : step.Key is { Length: > 0 } key ? (IReadOnlyList<string>)[key] : [];
            if (commands.Count > 0) text.Children.Add(OverlayTheme.Keycaps(commands));
        }
        else
        {
            text.Children.Add(new TextBlock
            {
                Text = selected.NextAction,
                Foreground = OverlayTheme.MutedBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4)
            });
            if (selected.CombineCommands.Count > 0)
                text.Children.Add(OverlayTheme.Keycaps(selected.CombineCommands));
        }

        if (selected.Warnings.Count > 0)
            text.Children.Add(new TextBlock
            {
                Text = selected.Warnings[0],
                Foreground = OverlayTheme.WarnBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

        var score = new TextBlock
        {
            Text = RecommendationPresentation.CompletionPercent(selected.RecipeProgress),
            Foreground = OverlayTheme.GoldBrush,
            FontSize = 32,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(score, 2);
        row.Children.Add(score);
        return row;
    }

    private static UIElement FlowBlock(Recommendation selected)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "조합 흐름  ·  안흔함부터",
            Foreground = OverlayTheme.MutedBrush,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var steps = selected.RemainingCraftSteps;
        if (steps.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "바로 조합 가능",
                Foreground = OverlayTheme.OkBrush,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var currentId = steps.FirstOrDefault(step => step.MissingCount > 0)?.UnitId
                            ?? steps[0].UnitId;
            var shown = steps.Take(8).ToList();
            for (var i = 0; i < shown.Count; i++)
            {
                if (i > 0) row.Children.Add(Arrow());
                row.Children.Add(FlowNode(shown[i],
                    shown[i].UnitId.Equals(currentId, StringComparison.OrdinalIgnoreCase)));
            }
            if (steps.Count > shown.Count)
                row.Children.Add(new TextBlock
                {
                    Text = $"  +{steps.Count - shown.Count}",
                    Foreground = OverlayTheme.MutedBrush,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
            stack.Children.Add(row);
        }

        return stack;
    }

    private static UIElement FlowNode(RecipeCraftStep step, bool current)
    {
        var commands = step.CombineCommands.Count > 0
            ? step.CombineCommands
            : step.CombineKey is { Length: > 0 } key ? (IReadOnlyList<string>)[key] : [];
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Width = 76 };
        var icon = UnitImageFactory.Create(step.Image, step.Name, 40, step.UnitId);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(icon);
        body.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.SafeText(step.Name).Trim(),
            Foreground = OverlayTheme.WhiteBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{step.OwnedCount}/{step.RequiredCount}",
            Foreground = current ? OverlayTheme.GoldBrush : OverlayTheme.MutedBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center
        });
        if (commands.Count > 0)
        {
            var chips = OverlayTheme.Keycaps(commands.Take(1).ToList());
            chips.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            chips.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
            body.Children.Add(chips);
        }
        return new Border
        {
            Background = OverlayTheme.RowBrush,
            BorderBrush = current ? OverlayTheme.GoldBrush : OverlayTheme.HairlineBrush,
            BorderThickness = new Thickness(current ? 2 : 1),
            Padding = new Thickness(5, 4, 5, 4),
            Child = body
        };
    }

    private static UIElement Arrow()
    {
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,6 0,12"),
            Stroke = OverlayTheme.GoldBrush,
            StrokeThickness = 2,
            Margin = new Thickness(6, 0, 6, 20),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static UIElement BoardTile(Recommendation item, int rank, bool selected, Action<string> onSelect)
    {
        var unit = item.CompositionUnits[0];
        var body = new StackPanel { Width = 64 };
        var iconBox = new Grid();
        var icon = UnitImageFactory.Create(unit.Image, unit.Name, 52, unit.UnitId);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        iconBox.Children.Add(icon);
        iconBox.Children.Add(new TextBlock
        {
            Text = rank.ToString(CultureInfo.InvariantCulture),
            Foreground = OverlayTheme.GoldBrush,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 0, 0, 0)
        });
        body.Children.Add(iconBox);
        body.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CompletionPercent(item.RecipeProgress),
            Foreground = OverlayTheme.GoldBrush,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        });
        var tile = new Border
        {
            Background = OverlayTheme.RowBrush,
            BorderBrush = selected ? OverlayTheme.GoldBrush : OverlayTheme.HairlineBrush,
            BorderThickness = new Thickness(selected ? 2 : 1),
            Padding = new Thickness(4, 4, 4, 4),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Child = body
        };
        AutomationProperties.SetName(tile, RecommendationPresentation.CraftUnitName(unit));
        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onSelect(item.Route.Id);
        };
        return tile;
    }
}
