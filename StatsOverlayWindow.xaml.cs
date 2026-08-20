using System.Windows;
namespace OrandOverlay;

/// <summary>
/// 내 패 상태(수치·정리 안내) 전용 오버레이.
/// 추천 창과 분리해 각자 원하는 위치에 놓을 수 있게 한다.
/// 패널 채우기는 추천 창의 렌더링 코드가 이 창의 패널을 그대로 쓴다.
/// </summary>
public partial class StatsOverlayWindow : OverlayWindowBase
{
    public StatsOverlayWindow() => InitializeComponent();

    protected override double DesignWidth => 228;
    protected override double DesignHeight => 700;
    protected override UIElement? ClickThroughIndicator => ClickThroughBadge;
}
