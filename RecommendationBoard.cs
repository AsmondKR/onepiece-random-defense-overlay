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
    /// <summary>창 폭은 이 칸 3개가 한 화면에 들어오는 크기로 맞춘다.</summary>
    public const int FlowVisibleSteps = 3;
    public const double FlowNodeWidth = 140;
    public static void Fill(
        Panel nowPanel,
        Panel flowPanel,
        Panel boardPanel,
        IReadOnlyList<Recommendation> recs,
        IReadOnlyList<AutoCombineStep> plan,
        string? selectedId,
        Action<string> onSelect,
        string? banner = null,
        IReadOnlyList<Recommendation>? selectedChildren = null,
        string? clusterHeadId = null)
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
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 10, 0, 10)
            });
            nowPanel.Children.Add(new TextBlock
            {
                Text = "게임이 잡히면 지금 할 일과 후보 보드가 여기에 뜹니다.",
                Foreground = OverlayTheme.MutedBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 4)
            });
            return;
        }

        var children = selectedChildren ?? [];
        var selected = BoardSelection.Resolve(recs, children, selectedId) ?? recs[0];
        var viewingChild = BoardSelection.Contains(children, selected.Route.Id);
        var clusterHead = BoardSelection.Find(recs,
                             BoardSelection.ClusterHeadId(recs, children, selected.Route.Id, clusterHeadId))
                         ?? recs[0];
        var nowPlan = viewingChild ? Array.Empty<AutoCombineStep>() : plan;

        if (!string.IsNullOrWhiteSpace(banner))
            nowPanel.Children.Add(new TextBlock
            {
                Text = banner,
                Foreground = OverlayTheme.WarnBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

        nowPanel.Children.Add(NowBlock(selected, nowPlan));
        flowPanel.Children.Add(FlowBlock(selected));
        var missingLeaves = RecommendationPresentation.BoardMissingLeaves(
            selected.RecipeProgress, viewingChild);
        if (missingLeaves.Count > 0)
        {
            if (viewingChild)
                boardPanel.Children.Add(new TextBlock
                {
                    Text = "부족한 흔함",
                    Foreground = OverlayTheme.MutedBrush,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            var missing = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var leaf in missingLeaves.Take(12))
                missing.Children.Add(OverlayTheme.MissingChip(
                    UnitImageFactory.Create(leaf.Image, leaf.Name, 24, leaf.UnitId),
                    RecommendationPresentation.CraftUnitName(leaf.Name, leaf.Tier),
                    leaf.MissingCount));
            boardPanel.Children.Add(missing);
        }
        var tiles = new WrapPanel();
        var childIds = children.Select(item => item.Route.GoalUnitId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in recs)
        {
            var isHead = BoardSelection.Matches(rec, clusterHead.Route.Id);
            if (!isHead && childIds.Contains(rec.Route.GoalUnitId)) continue;
            tiles.Children.Add(isHead
                ? RenderCluster(new BoardCluster(rec, children), selected.Route.Id, onSelect)
                : BoardTile(rec, BoardSelection.Matches(rec, selected.Route.Id), onSelect));
        }
        boardPanel.Children.Add(tiles);
    }

    public readonly record struct BoardCluster(Recommendation Head, IReadOnlyList<Recommendation> Children);

    public static IReadOnlyList<BoardCluster> Clusters(IReadOnlyList<Recommendation> recs)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clusters = new List<BoardCluster>();
        foreach (var rec in recs)
        {
            if (rec.ClusterParentUnitId is { Length: > 0 }) continue;
            if (!used.Add(rec.Route.GoalUnitId)) continue;
            var children = recs
                .Where(child => child.ClusterParentUnitId is { } parent &&
                                parent.Equals(rec.Route.GoalUnitId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var child in children)
                used.Add(child.Route.GoalUnitId);
            clusters.Add(new BoardCluster(rec, children));
        }
        foreach (var rec in recs)
        {
            if (!used.Add(rec.Route.GoalUnitId)) continue;
            clusters.Add(new BoardCluster(rec, []));
        }
        return clusters;
    }

    private static UIElement NowBlock(Recommendation selected, IReadOnlyList<AutoCombineStep> plan)
    {
        var unit = selected.CompositionUnits[0];
        var icon = UnitImageFactory.Create(unit.Image, unit.Name, 48, unit.UnitId);
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
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 42
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
            FontSize = 24,
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
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
            Text = "조합 흐름",
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
            for (var i = 0; i < steps.Count; i++)
            {
                if (i > 0) row.Children.Add(Arrow());
                row.Children.Add(FlowNode(steps[i],
                    steps[i].UnitId.Equals(currentId, StringComparison.OrdinalIgnoreCase)));
            }
            stack.Children.Add(row);
        }

        return stack;
    }

    private static UIElement FlowNode(RecipeCraftStep step, bool current)
    {
        var keys = RecommendationPresentation.CraftActionKeys(step);
        var selectName = RecommendationPresentation.CraftSelectUnitName(step);
        var companions = RecommendationPresentation.CraftCompanionNames(step);
        var select = step.Ingredients.OrderBy(item => item.SelectionOrder).FirstOrDefault();
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Width = FlowNodeWidth };
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
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 30,
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
        if (selectName is { Length: > 0 } && select is not null)
        {
            var pick = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            };
            var pickIcon = UnitImageFactory.Create("", select.Name, 18, select.UnitId);
            pickIcon.VerticalAlignment = VerticalAlignment.Center;
            pickIcon.Margin = new Thickness(0, 0, 4, 0);
            pick.Children.Add(pickIcon);
            pick.Children.Add(new TextBlock
            {
                Text = selectName,
                Foreground = current ? OverlayTheme.GoldBrush : OverlayTheme.WhiteBrush,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 88
            });
            pick.Children.Add(new TextBlock
            {
                Text = " 선택",
                Foreground = OverlayTheme.MutedBrush,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            });
            body.Children.Add(pick);
        }
        else if (companions is { Length: > 0 })
        {
            body.Children.Add(new TextBlock
            {
                Text = "함께 " + companions,
                Foreground = OverlayTheme.MutedBrush,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 28,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        if (keys.Count > 0)
        {
            var chips = OverlayTheme.Keycaps(keys.Take(1).ToList());
            chips.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            chips.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
            body.Children.Add(chips);
        }
        return new Border
        {
            Background = current ? OverlayTheme.FeaturedBrush : OverlayTheme.RowBrush,
            BorderBrush = current ? OverlayTheme.GoldBrush : OverlayTheme.HairlineBrush,
            BorderThickness = new Thickness(current ? 2 : 1),
            CornerRadius = new CornerRadius(OverlayTheme.TileRadius),
            Padding = new Thickness(5, 6, 5, 6),
            ToolTip = RecommendationPresentation.CraftIngredientLine(step),
            Child = body
        };
    }

    private static UIElement Arrow()
    {
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L7,5 0,10"),
            Stroke = OverlayTheme.GoldBrush,
            StrokeThickness = 2,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static UIElement RenderCluster(BoardCluster cluster, string selectedId, Action<string> onSelect)
    {
        if (cluster.Children.Count == 0)
            return BoardTile(cluster.Head, cluster.Head.Route.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase),
                onSelect);

        var selectedInCluster = cluster.Head.Route.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase) ||
                                cluster.Children.Any(child =>
                                    child.Route.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(BoardPortrait(cluster.Head, 48, 11,
            cluster.Head.Route.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase), onSelect));
        row.Children.Add(new Border
        {
            Width = 1,
            Height = 34,
            Background = OverlayTheme.GoldBrush,
            Opacity = 0.5,
            Margin = new Thickness(6, 0, 6, 10),
            VerticalAlignment = VerticalAlignment.Center
        });
        foreach (var child in cluster.Children)
            row.Children.Add(BoardPortrait(child, 32, 10,
                child.Route.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase), onSelect));
        return new Border
        {
            Background = selectedInCluster ? OverlayTheme.FeaturedBrush : OverlayTheme.RowBrush,
            BorderBrush = selectedInCluster ? OverlayTheme.GoldBrush : OverlayTheme.HairlineBrush,
            BorderThickness = new Thickness(selectedInCluster ? 1.5 : 1),
            CornerRadius = new CornerRadius(OverlayTheme.WellRadius),
            Padding = new Thickness(8, 8, 8, 6),
            Margin = new Thickness(0, 0, 10, 10),
            Child = row
        };
    }

    private static UIElement BoardPortrait(Recommendation item, double size, double percentSize,
        bool selected, Action<string> onSelect)
    {
        var unit = item.CompositionUnits[0];
        var icon = UnitImageFactory.Create(unit.Image, unit.Name, size, unit.UnitId);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var body = new StackPanel { Width = size + 8 };
        var ring = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderBrush = selected ? OverlayTheme.GoldBrush : OverlayTheme.HairlineBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(OverlayTheme.ImageRadius),
            Child = icon
        };
        body.Children.Add(ring);
        body.Children.Add(new TextBlock
        {
            Text = RecommendationPresentation.CompletionPercent(item.RecipeProgress),
            Foreground = OverlayTheme.GoldBrush,
            FontSize = percentSize,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        });
        var hit = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = body,
            Margin = new Thickness(0, 0, 4, 0)
        };
        hit.ToolTip = RecommendationPresentation.CraftUnitName(unit);
        AutomationProperties.SetName(hit, RecommendationPresentation.CraftUnitName(unit));
        hit.MouseEnter += (_, _) => ring.BorderBrush = OverlayTheme.GoldBrush;
        hit.MouseLeave += (_, _) =>
            ring.BorderBrush = selected ? OverlayTheme.GoldBrush : OverlayTheme.HairlineBrush;
        hit.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onSelect(item.Route.Id);
        };
        return hit;
    }

    private static UIElement BoardTile(Recommendation item, bool selected, Action<string> onSelect)
    {
        var unit = item.CompositionUnits[0];
        var body = new StackPanel { Width = 64 };
        var icon = UnitImageFactory.Create(unit.Image, unit.Name, 52, unit.UnitId);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(icon);
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
            Background = selected ? OverlayTheme.FeaturedBrush : OverlayTheme.RowBrush,
            BorderBrush = selected ? OverlayTheme.GoldBrush : OverlayTheme.HairlineBrush,
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(OverlayTheme.TileRadius),
            Padding = new Thickness(6, 6, 6, 6),
            Margin = new Thickness(0, 0, 10, 10),
            Cursor = Cursors.Hand,
            Child = body
        };
        tile.ToolTip = RecommendationPresentation.CraftUnitName(unit);
        AutomationProperties.SetName(tile, RecommendationPresentation.CraftUnitName(unit));
        tile.MouseEnter += (_, _) =>
        {
            if (!selected) tile.BorderBrush = OverlayTheme.GoldBrush;
            tile.Background = OverlayTheme.FeaturedBrush;
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.BorderBrush = selected ? OverlayTheme.GoldBrush : OverlayTheme.HairlineBrush;
            tile.Background = selected ? OverlayTheme.FeaturedBrush : OverlayTheme.RowBrush;
        };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onSelect(item.Route.Id);
        };
        return tile;
    }
}
