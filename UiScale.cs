using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OrandOverlay;

/// <summary>
/// 화면 해상도에 따른 UI 자동 배율. 기본 크기(720×700 오버레이)가 2K(논리 높이
/// 1440)에서 다듬어졌으므로 그걸 1.0 기준으로 삼고, 논리 높이가 더 큰 화면에서만
/// 비례해 키운다(4K 100% → 1.5배, 8K → 3배). 2K 이하(FHD 포함)는 그대로 둬서
/// 게임 화면을 더 가리지 않는다. 윈도우 배율(DPI)이 이미 반영된 논리 좌표를
/// 쓰므로 배율을 올려 쓰는 사용자는 이중으로 커지지 않는다.
/// </summary>
public static class UiScale
{
    public const double BaselineHeight = 1440.0;

    public static double ForWindow(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var screen = handle == IntPtr.Zero
                ? System.Windows.Forms.Screen.PrimaryScreen
                : System.Windows.Forms.Screen.FromHandle(handle);
            if (screen is null) return 1.0;
            var dpi = VisualTreeHelper.GetDpi(window);
            return FromScreen(screen.Bounds.Height, dpi.DpiScaleY);
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>물리 픽셀 높이와 윈도우 배율(DPI)로 배율을 계산한다. 확대 전용.</summary>
    public static double FromScreen(double physicalHeight, double dpiScale)
    {
        var logicalHeight = physicalHeight / Math.Max(0.5, dpiScale);
        return Math.Clamp(logicalHeight / BaselineHeight, 1.0, 3.0);
    }

    /// <summary>창 내용에 배율을 적용하고 창의 기준 크기도 같은 비율로 맞춘다.</summary>
    public static double Apply(Window window, double baseWidth, double baseHeight)
    {
        var scale = ForWindow(window);
        if (window.Content is FrameworkElement root)
            root.LayoutTransform = Math.Abs(scale - 1.0) < 0.01
                ? Transform.Identity
                : new ScaleTransform(scale, scale);
        window.Width = baseWidth * scale;
        window.Height = baseHeight * scale;
        return scale;
    }
}
