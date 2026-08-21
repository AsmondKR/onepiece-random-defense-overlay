using System.Windows;

namespace OrandOverlay;

public partial class App : System.Windows.Application
{
    // 중복 실행 방지. 두 인스턴스가 같은 설정 파일과 오버레이 핫키를 두고 다투면
    // 설정이 서로를 덮어쓰고 핫키는 한쪽만 먹는다 — 두 번째 실행은 기존 창을
    // 앞으로 부르고 조용히 끝난다.
    //
    // 자동 업데이트와의 관계: 교체 스크립트는 옛 프로세스가 죽어 exe가 지워질
    // 때까지 기다렸다가 새 버전을 실행한다. 뮤텍스는 프로세스 종료 시 OS가
    // 해제하므로 새 버전이 잠금에 막히는 일은 없다.
    private const string InstanceMutexName = @"Local\OrandOverlay.SingleInstance";
    private const string ActivationEventName = @"Local\OrandOverlay.ShowExisting";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // 워크3 패치가 앱 릴리스보다 먼저 나와도 최신 검증 프로필을 받을 수 있게 한다.
        // 최대 3초만 기다리며, 실패하면 기존 사용자 캐시/번들 프로필을 그대로 사용한다.
        MemoryProfileRefreshService.TryRefreshAsync().GetAwaiter().GetResult();

        ListenForActivationSignal();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>기존 인스턴스에 "창 보여줘" 신호를 보낸다. 실패해도 그냥 끝낸다.</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
                using (handle) handle.Set();
        }
        catch (Exception)
        {
            // 신호는 편의 기능이다 — 기존 인스턴스가 살아 있는 것만으로 목적은 달성됐다.
        }
    }

    /// <summary>백그라운드 스레드로 신호를 기다렸다가 메인 창을 앞으로 부른다.</summary>
    private void ListenForActivationSignal()
    {
        _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var thread = new Thread(() =>
        {
            var signal = _activationSignal;
            while (signal is not null)
            {
                try { signal.WaitOne(); }
                catch (ObjectDisposedException) { return; }
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is not { } window) return;
                    window.Show();
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    window.Activate();
                });
            }
        })
        { IsBackground = true, Name = "OrandOverlay.ActivationListener" };
        thread.Start();
    }
}
