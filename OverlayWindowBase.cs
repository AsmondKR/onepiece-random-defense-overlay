using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace OrandOverlay;

/// <summary>
/// 게임 위에 띄우는 창들의 공통 동작.
/// 창을 둘(패 수치 / 추천)로 나누면서 클릭 통과·드래그 이동·해상도 배율·모니터 클램프를
/// 한 곳에 모았다. 각 창은 설계 크기와 클릭 통과 표시만 채워 넣는다.
/// </summary>
public abstract class OverlayWindowBase : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private bool _clickThrough;
    private bool _allowClose;

    protected OverlayWindowBase()
    {
        SourceInitialized += (_, _) => ApplyClickThroughStyle();
        Loaded += (_, _) =>
        {
            ApplyResolutionScale();
            ClampToVisibleMonitor();
            if (Content is FrameworkElement chrome)
                OverlayTheme.AttachRoundClip(chrome, OverlayTheme.ChromeRadius);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyResolutionScale();
            ClampToVisibleMonitor();
        }));
        Closing += OverlayWindow_OnClosing;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    /// <summary>배율 계산 기준이 되는 설계 크기.</summary>
    protected abstract double DesignWidth { get; }
    protected abstract double DesignHeight { get; }

    /// <summary>클릭 통과 중임을 알리는 표시(없으면 null).</summary>
    protected virtual UIElement? ClickThroughIndicator => null;

    public event Action<double, double>? PositionCommitted;
    public event Action? HiddenByUser;

    public void RestorePosition(double? left, double? top)
    {
        if (left is not double savedLeft || top is not double savedTop ||
            !double.IsFinite(savedLeft) || !double.IsFinite(savedTop)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = savedLeft;
        Top = savedTop;
    }

    public OverlayPosition CurrentPosition()
    {
        ClampToVisibleMonitor();
        return new OverlayPosition(Left, Top);
    }

    public void EnsureVisible() => ClampToVisibleMonitor();

    public void CloseForApplication()
    {
        _allowClose = true;
        Close();
    }

    public virtual void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        if (ClickThroughIndicator is { } indicator)
            indicator.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyClickThroughStyle();
    }

    private void ApplyClickThroughStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GwlExStyle);
        style = _clickThrough
            ? style | WsExTransparent | WsExToolWindow
            : (style & ~WsExTransparent) | WsExToolWindow;
        SetWindowLong(handle, GwlExStyle, style);
        // Extended hit-test styles can stay cached by the window manager. Refreshing the
        // non-client state makes an overlay that was click-through immediately draggable.
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    protected void DragArea_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough || e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;

        var startLeft = Left;
        var startTop = Top;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            // WPF DragMove can fail after WS_EX_TRANSPARENT was removed because the child
            // element still owns mouse capture. Hand the press to the native caption move
            // loop instead; SendMessage returns when the user releases the button.
            ReleaseCapture();
            SendMessage(handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
        else
        {
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
        }

        // Do not run the monitor clamp after a normal drag. On mixed-DPI desktops it can
        // reinterpret the just-moved coordinates and visibly snap the overlay back. Restore
        // and display-topology changes still use the safety clamp.
        if (double.IsFinite(Left) && double.IsFinite(Top) &&
            (Math.Abs(Left - startLeft) >= 0.5 || Math.Abs(Top - startTop) >= 0.5))
        {
            // 드래그로 다른 해상도의 모니터에 옮겨졌을 수 있으니 배율을 다시 잡는다.
            ApplyResolutionScale();
            PositionCommitted?.Invoke(Left, Top);
        }
    }

    protected void OverlayWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        HiddenByUser?.Invoke();
    }

    protected void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyResolutionScale();
            ClampToVisibleMonitor();
            PositionCommitted?.Invoke(Left, Top);
        }));
    }

    // 오버레이가 놓인 모니터 해상도(FHD~8K)에 맞춰 창 전체를 비례 확대한다.
    protected void ApplyResolutionScale() => UiScale.Apply(this, DesignWidth, DesignHeight);

    protected void ClampToVisibleMonitor()
    {
        var scale = VisualTreeHelper.GetDpi(this);
        var scaleX = scale.DpiScaleX > 0 ? scale.DpiScaleX : 1;
        var scaleY = scale.DpiScaleY > 0 ? scale.DpiScaleY : 1;
        var width = (ActualWidth > 0 ? ActualWidth : Width) * scaleX;
        var height = (ActualHeight > 0 ? ActualHeight : Height) * scaleY;
        var workAreas = System.Windows.Forms.Screen.AllScreens
            .Select(screen => screen.WorkingArea)
            .Select(area => new OverlayBounds(area.Left, area.Top, area.Width, area.Height))
            .ToList();
        var position = OverlayPositionPolicy.ClampToNearestWorkArea(
            Left * scaleX, Top * scaleY, width, height, workAreas);
        Left = position.Left / scaleX;
        Top = position.Top / scaleY;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
}
