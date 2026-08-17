using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace OrandOverlay;

public interface IInventoryRecognizer
{
    Task<RecognitionResult> RecognizeAsync(AppSettings settings, CancellationToken cancellationToken);
}

public sealed class ScreenRecognitionService(DataCatalog catalog) : IInventoryRecognizer
{
    private const int FeatureSize = 16;

    public Task<RecognitionResult> RecognizeAsync(AppSettings settings, CancellationToken cancellationToken)
        => Task.Run(() => Recognize(settings, cancellationToken), cancellationToken);

    private RecognitionResult Recognize(AppSettings settings, CancellationToken cancellationToken)
    {
        var screen = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("기본 화면을 찾지 못했습니다.");
        var region = ToPixels(settings.InventoryRegion, screen);
        using var capture = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(capture))
            graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);

        var templates = LoadTemplates();
        if (templates.Count == 0)
            return new RecognitionResult
            {
                Status = "템플릿 없음 · 기존 패 유지",
                State = RecognitionState.ConfigurationError,
                Diagnostics = new RecognitionDiagnostics
                {
                    Source = "Screen",
                    Detail = $"이미지 템플릿을 추가하세요: {AppPaths.TemplateDirectory}"
                }
            };

        var entries = new List<InventoryEntry>();
        var slotWidth = Math.Max(1, capture.Width / settings.GridColumns);
        var slotHeight = Math.Max(1, capture.Height / settings.GridRows);

        for (var row = 0; row < settings.GridRows; row++)
        for (var column = 0; column < settings.GridColumns; column++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = column * slotWidth;
            var y = row * slotHeight;
            var width = Math.Min(slotWidth, capture.Width - x);
            var height = Math.Min(slotHeight, capture.Height - y);
            if (width < 4 || height < 4) continue;

            using var slot = capture.Clone(new Rectangle(x, y, width, height), capture.PixelFormat);
            var feature = Feature(slot);
            if (Variance(feature) < 0.006) continue;

            var best = templates
                .Select(t => (t.UnitId, Score: Similarity(feature, t.Feature)))
                .OrderByDescending(x => x.Score)
                .First();
            if (best.Score < settings.MatchThreshold) continue;

            entries.Add(new InventoryEntry
            {
                UnitId = best.UnitId,
                Confidence = best.Score,
                Count = 1
            });
        }

        var merged = entries.GroupBy(x => x.UnitId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new InventoryEntry
            {
                UnitId = g.Key,
                Count = g.Count(),
                Confidence = g.Average(x => x.Confidence)
            })
            .ToList();
        return new RecognitionResult
        {
            Entries = merged,
            Status = $"자동 인식 {merged.Sum(x => x.Count)}장 · {DateTime.Now:HH:mm:ss}",
            State = RecognitionState.Ready,
            Diagnostics = new RecognitionDiagnostics
            {
                Source = "Screen",
                ObservedObjects = settings.GridColumns * settings.GridRows,
                MappedObjects = merged.Sum(x => x.Count),
                Detail = $"템플릿 {templates.Count}개 · 임계값 {settings.MatchThreshold:0.00}"
            }
        };
    }

    private List<(string UnitId, double[] Feature)> LoadTemplates()
    {
        var result = new List<(string, double[])>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.TemplateDirectory, "*.png"))
        {
            var id = Path.GetFileNameWithoutExtension(file).Split("__", 2)[0];
            if (!catalog.UnitsById.ContainsKey(id)) continue;
            try
            {
                using var image = new Bitmap(file);
                result.Add((id, Feature(image)));
            }
            catch
            {
                // A broken user template is ignored so recognition can continue.
            }
        }
        return result;
    }

    private static Rectangle ToPixels(NormalizedRect value, Rectangle bounds)
    {
        var x = bounds.Left + (int)Math.Round(bounds.Width * Math.Clamp(value.X, 0, 1));
        var y = bounds.Top + (int)Math.Round(bounds.Height * Math.Clamp(value.Y, 0, 1));
        var width = (int)Math.Round(bounds.Width * Math.Clamp(value.Width, 0.01, 1));
        var height = (int)Math.Round(bounds.Height * Math.Clamp(value.Height, 0.01, 1));
        width = Math.Min(width, bounds.Right - x);
        height = Math.Min(height, bounds.Bottom - y);
        return new Rectangle(x, y, Math.Max(1, width), Math.Max(1, height));
    }

    private static double[] Feature(Bitmap source)
    {
        using var resized = new Bitmap(source, new Size(FeatureSize, FeatureSize));
        var feature = new double[FeatureSize * FeatureSize * 3];
        var index = 0;
        for (var y = 0; y < FeatureSize; y++)
        for (var x = 0; x < FeatureSize; x++)
        {
            var color = resized.GetPixel(x, y);
            feature[index++] = color.R / 255d;
            feature[index++] = color.G / 255d;
            feature[index++] = color.B / 255d;
        }
        return feature;
    }

    private static double Similarity(double[] left, double[] right)
    {
        var error = 0d;
        for (var i = 0; i < left.Length; i++) error += Math.Abs(left[i] - right[i]);
        return 1 - error / left.Length;
    }

    private static double Variance(double[] values)
    {
        var mean = values.Average();
        return values.Sum(x => (x - mean) * (x - mean)) / values.Length;
    }
}
