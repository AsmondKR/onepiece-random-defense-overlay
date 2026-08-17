using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrandOverlay;

public static class UnitImageFactory
{
    public static FrameworkElement Create(string imageUrl, string unitName, double size)
    {
        var fallback = new Border
        {
            Width = size,
            Height = size,
            Background = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
            CornerRadius = new CornerRadius(5),
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
        container.Children.Add(fallback);
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)) return container;

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
