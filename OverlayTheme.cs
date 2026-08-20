using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrandOverlay;

/// <summary>롤체지지·닥지지형 오버레이 팔레트. RPG 보라 카드가 아니라 전적 HUD.</summary>
internal static class OverlayTheme
{
    public static readonly Color Gold = Color.FromRgb(232, 197, 71);
    public static readonly Color Muted = Color.FromRgb(180, 187, 198);
    public static readonly Color Hairline = Color.FromRgb(44, 49, 58);
    public static readonly Color Chip = Color.FromRgb(26, 30, 38);
    public static readonly Color Ok = Color.FromRgb(61, 220, 132);
    public static readonly Color Warn = Color.FromRgb(227, 179, 65);
    public static readonly Color Row = Color.FromRgb(20, 23, 29);

    public static SolidColorBrush GoldBrush { get; } = Freeze(Gold);
    public static SolidColorBrush MutedBrush { get; } = Freeze(Muted);
    public static SolidColorBrush OkBrush { get; } = Freeze(Ok);
    public static SolidColorBrush WarnBrush { get; } = Freeze(Warn);
    public static SolidColorBrush RowBrush { get; } = Freeze(Row);
    public static SolidColorBrush HairlineBrush { get; } = Freeze(Hairline);

    public static UIElement CommandChip(string text, bool primary)
    {
        return new Border
        {
            Background = new SolidColorBrush(Chip),
            BorderBrush = primary ? GoldBrush : HairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Foreground = primary ? GoldBrush : Brushes.White,
                FontSize = primary ? 12 : 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    public static UIElement CommandChips(IReadOnlyList<string> commands)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < commands.Count; i++)
            row.Children.Add(CommandChip(commands[i], i == 0));
        return row;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
