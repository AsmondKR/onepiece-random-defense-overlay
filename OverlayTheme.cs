using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrandOverlay;

/// <summary>
/// 롤체/닥지/OP.GG형 HUD 색·간격.
/// 1번 피처드 + 나머지 표 + 상단 KPI 타일이다.
/// </summary>
internal static class OverlayTheme
{
    public static readonly Color Gold = Color.FromRgb(240, 199, 94);
    public static readonly Color Muted = Color.FromRgb(139, 147, 167);
    public static readonly Color Hairline = Color.FromRgb(36, 41, 52);
    public static readonly Color Chip = Color.FromRgb(20, 24, 32);
    public static readonly Color Ok = Color.FromRgb(61, 220, 132);
    public static readonly Color Warn = Color.FromRgb(240, 180, 70);
    public static readonly Color Row = Color.FromRgb(12, 14, 18);
    public static readonly Color RowAlt = Color.FromRgb(16, 19, 26);
    public static readonly Color Featured = Color.FromRgb(18, 21, 28);

    public static SolidColorBrush GoldBrush { get; } = Freeze(Gold);
    public static SolidColorBrush MutedBrush { get; } = Freeze(Muted);
    public static SolidColorBrush OkBrush { get; } = Freeze(Ok);
    public static SolidColorBrush WarnBrush { get; } = Freeze(Warn);
    public static SolidColorBrush RowBrush { get; } = Freeze(Row);
    public static SolidColorBrush RowAltBrush { get; } = Freeze(RowAlt);
    public static SolidColorBrush FeaturedBrush { get; } = Freeze(Featured);
    public static SolidColorBrush HairlineBrush { get; } = Freeze(Hairline);
    public static SolidColorBrush WhiteBrush { get; } = Freeze(Colors.White);

    public const double ChromeRadius = 16;
    public const double WellRadius = 12;
    public const double TileRadius = 10;
    public const double ImageRadius = 8;
    public const double ChipRadius = 6;

    public static void AttachRoundClip(FrameworkElement element, double radius)
    {
        void Apply()
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            element.Clip = new RectangleGeometry(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
        }
        element.SizeChanged += (_, _) => Apply();
        element.Loaded += (_, _) => Apply();
        Apply();
    }

    public static string Num(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    public static UIElement Keycap(string text, bool primary)
    {
        return new Border
        {
            Background = new SolidColorBrush(Chip),
            BorderBrush = primary ? GoldBrush : HairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ChipRadius),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Foreground = primary ? GoldBrush : WhiteBrush,
                FontFamily = new FontFamily("Consolas, Malgun Gothic"),
                FontSize = primary ? 12 : 11,
                FontWeight = FontWeights.Bold
            }
        };
    }

    public static UIElement Keycaps(IReadOnlyList<string> commands)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < commands.Count; i++)
            row.Children.Add(Keycap(commands[i], i == 0));
        return row;
    }

    public static UIElement CommandChip(string text, bool primary) => Keycap(text, primary);

    public static UIElement CommandChips(IReadOnlyList<string> commands) => Keycaps(commands);

    public static TextBlock Section(string text) => new()
    {
        Text = text,
        Foreground = MutedBrush,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 4)
    };

    public static UIElement RecTableHeader()
    {
        var row = CompactColumns();
        row.Children.Add(HeaderCell("#", 0));
        row.Children.Add(HeaderCell("유닛", 1));
        row.Children.Add(HeaderCell("명령", 2, HorizontalAlignment.Right));
        row.Children.Add(HeaderCell("완성", 3, HorizontalAlignment.Right));
        return new Border
        {
            BorderBrush = HairlineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 8, 6, 5),
            Margin = new Thickness(0, 8, 0, 0),
            Child = row
        };
    }

    public static UIElement FeaturedBlock(FrameworkElement icon, string rankName, string? ability,
        IReadOnlyList<string> commands, string percent, string? nextAction)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = rankName,
            Foreground = WhiteBrush,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(ability))
            text.Children.Add(new TextBlock
            {
                Text = ability,
                Foreground = MutedBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        if (commands.Count > 0)
        {
            var chips = Keycaps(commands);
            chips.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 0));
            text.Children.Add(chips);
        }
        if (!string.IsNullOrWhiteSpace(nextAction))
            text.Children.Add(new TextBlock
            {
                Text = "다음  " + nextAction,
                Foreground = GoldBrush,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });

        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var score = new TextBlock
        {
            Text = percent,
            Foreground = GoldBrush,
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(score, 2);
        row.Children.Add(score);
        return row;
    }

    public static UIElement CompactRow(int rank, FrameworkElement icon, string name,
        IReadOnlyList<string> commands, string percent)
    {
        var row = CompactColumns();
        row.Children.Add(new TextBlock
        {
            Text = rank.ToString(CultureInfo.InvariantCulture),
            Foreground = MutedBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 22
        });
        icon.Width = 32;
        icon.Height = 32;
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var unit = new StackPanel { Orientation = Orientation.Horizontal };
        unit.Children.Add(icon);
        unit.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(unit, 1);
        row.Children.Add(unit);
        if (commands.Count > 0)
        {
            var chips = Keycaps(commands);
            Grid.SetColumn(chips, 2);
            row.Children.Add(chips);
        }
        var score = new TextBlock
        {
            Text = percent,
            Foreground = GoldBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(score, 3);
        row.Children.Add(score);
        return row;
    }

    public static Border FeaturedShell(UIElement child) => new()
    {
        Background = FeaturedBrush,
        BorderBrush = GoldBrush,
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(10, 10, 10, 10),
        Margin = new Thickness(0, 2, 0, 2),
        Child = child
    };

    public static Border TableShell(UIElement child, bool alt) => new()
    {
        Background = alt ? RowAltBrush : RowBrush,
        BorderBrush = HairlineBrush,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(6, 5, 6, 5),
        Child = child
    };

    public static UIElement Kpi(string label, double current, double target, string? detail)
    {
        var ok = current + 0.0001 >= target;
        var accent = ok ? Ok : Warn;
        var stack = new StackPanel { Margin = new Thickness(4, 7, 4, 7) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = MutedBrush,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        });
        var numbers = new StackPanel { Orientation = Orientation.Horizontal };
        numbers.Children.Add(new TextBlock
        {
            Text = Num(current),
            Foreground = new SolidColorBrush(accent),
            FontSize = 18,
            FontWeight = FontWeights.Bold
        });
        numbers.Children.Add(new TextBlock
        {
            Text = " / " + Num(target),
            Foreground = MutedBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3)
        });
        stack.Children.Add(numbers);
        stack.Children.Add(Bar(current, target, accent));
        if (!string.IsNullOrWhiteSpace(detail))
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = MutedBrush,
                FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        return new Border
        {
            Background = RowBrush,
            CornerRadius = new CornerRadius(TileRadius),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        };
    }

    public static UIElement Bar(double current, double target, Color accent)
    {
        var ratio = target <= 0 ? 1 : Math.Clamp(current / target, 0, 1);
        var grid = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
        grid.Children.Add(new Border { Background = new SolidColorBrush(accent) });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 45, 56)),
            CornerRadius = new CornerRadius(2),
            Child = grid
        };
    }

    public static UIElement Pair(string name, string value, Brush valueBrush, string? detail = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 6) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = MutedBrush,
            FontSize = 11
        });
        var number = new TextBlock
        {
            Text = value,
            Foreground = valueBrush,
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
                Foreground = MutedBrush,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            });
        return stack;
    }

    public static UIElement CraftRow(FrameworkElement icon, string name, string? action,
        IReadOnlyList<string> commands, string percent, string owned)
    {
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = WhiteBrush,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(action))
            text.Children.Add(new TextBlock
            {
                Text = action,
                Foreground = MutedBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            });
        if (commands.Count > 0)
        {
            var chips = Keycaps(commands);
            chips.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
            text.Children.Add(chips);
        }
        var progress = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0)
        };
        progress.Children.Add(new TextBlock
        {
            Text = percent,
            Foreground = GoldBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Right
        });
        progress.Children.Add(new TextBlock
        {
            Text = owned,
            Foreground = MutedBrush,
            FontSize = 10.5,
            TextAlignment = TextAlignment.Right
        });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(progress, 2);
        row.Children.Add(progress);
        return new Border
        {
            BorderBrush = HairlineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 6, 4, 6),
            Child = row
        };
    }

    public static UIElement MissingChip(FrameworkElement icon, string name, long missing)
    {
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 6, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = WhiteBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = "부족 ×" + missing.ToString(CultureInfo.InvariantCulture),
            Foreground = WarnBrush,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        return new Border
        {
            Background = new SolidColorBrush(Chip),
            BorderBrush = HairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ChipRadius),
            Padding = new Thickness(6, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = row
        };
    }

    public static UIElement CombineRow(int index, string target, string trigger, IReadOnlyList<string> commands)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = index.ToString(CultureInfo.InvariantCulture),
            Foreground = GoldBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Width = 18
        });
        title.Children.Add(new TextBlock
        {
            Text = target,
            Foreground = WhiteBrush,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = trigger + " 선택",
            Foreground = MutedBrush,
            FontSize = 11,
            Margin = new Thickness(18, 1, 0, 3)
        });
        if (commands.Count > 0)
        {
            var chips = Keycaps(commands);
            chips.SetValue(FrameworkElement.MarginProperty, new Thickness(18, 0, 0, 0));
            stack.Children.Add(chips);
        }
        return stack;
    }

    private static Grid CompactColumns()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return row;
    }

    private static TextBlock HeaderCell(string text, int column,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = MutedBrush,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = align
        };
        Grid.SetColumn(block, column);
        return block;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
