using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OrandOverlay;

/// <summary>
/// 화면 해상도에 따른 UI 자동 배율. FHD(논리 높이 1080)를 1.0 기준으로, 창이 놓인
/// 모니터의 논리 높이에 비례해 창 내용과 크기를 함께 키운다. 윈도우 배율(DPI)이
/// 이미 반영된 논리 좌표를 쓰므로, 4K를 100% 배율로 쓰는 경우처럼 화면 대비
/// UI가 작아지는 상황만 보정되고 배율을 올려 쓰는 사용자는 이중으로 커지지 않는다.
/// </summary>
public static class UiScale
{
    public const double BaselineHeight = 1080.0;

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

    /// <summary>물리 픽셀 높이와 윈도우 배율(DPI)로 배율을 계산한다.</summary>
    public static double FromScreen(double physicalHeight, double dpiScale)
    {
        var logicalHeight = physicalHeight / Math.Max(0.5, dpiScale);
        return Math.Clamp(logicalHeight / BaselineHeight, 0.8, 4.0);
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
