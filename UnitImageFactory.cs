using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrandOverlay;

public static class UnitImageFactory
{
    /// <summary>
    /// 유닛 아이콘. 번들된 PNG(Data\images\rawcode_*.png)만 사용하고
    /// (webp 코덱·오프라인 문제 없음), 없으면 이니셜 타일. 원격 요청은 하지 않는다.
    /// </summary>
    public static FrameworkElement Create(string imageUrl, string unitName, double size,
        string? unitId = null)
    {
        if (unitId is { Length: > 0 })
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "Data", "images",
                unitId.Replace(':', '_') + ".png");
            if (File.Exists(bundled)) imageUrl = bundled;
        }
        var fallback = new Border
        {
            Width = size,
            Height = size,
            Background = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
            CornerRadius = new CornerRadius(OverlayTheme.ImageRadius),
            Child = new TextBlock
            {
                Text = FirstKoreanCharacter(unitName),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = Math.Max(11, size * 0.34),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var container = new Grid { Width = size, Height = size };
        OverlayTheme.AttachRoundClip(container, OverlayTheme.ImageRadius);
        container.Children.Add(fallback);
        // 원격(티모지지 CDN) 요청은 하지 않는다 — 서버 부하를 만들지 않기로 한 약속.
        // 번들에 없는 유닛은 이니셜 타일로 표시된다.
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            !uri.IsFile) return container;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.DecodePixelWidth = Math.Max(32, (int)Math.Ceiling(size * 2));
            bitmap.CacheOption = BitmapCacheOption.OnDemand;
            bitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
            bitmap.EndInit();
            var image = new Image
            {
                Source = bitmap,
                Width = size,
                Height = size,
                Stretch = Stretch.UniformToFill
            };
            image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
            container.Children.Add(image);
        }
        catch
        {
            // Network or codec failures leave the deterministic Korean fallback tile visible.
        }
        return container;
    }

    private static string FirstKoreanCharacter(string value)
    {
        var character = value.FirstOrDefault(current => current is >= '\uAC00' and <= '\uD7A3');
        return character == default ? "패" : character.ToString();
    }
}
