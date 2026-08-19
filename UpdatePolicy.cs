namespace OrandOverlay;

/// <summary>
/// 자동 업데이트를 언제 적용할지 정한다.
///
/// 교체는 앱 재시작을 동반하므로 판 도중에 하면 그 판의 인식이 끊긴다. 유저
/// 클라이언트에서는 판이 끝날 때까지 미루고, 다음 확인 주기(2분)에 다시 시도한다.
/// 개발 PC는 배포 직후 바로 확인해야 하므로 ORAND_DEV 환경변수로 예외를 둔다.
/// </summary>
public static class UpdatePolicy
{
    public static bool ShouldInstallNow(bool liveSessionActive, bool developerMachine) =>
        !liveSessionActive || developerMachine;

    /// <summary>개발 PC 여부. ORAND_DEV가 설정돼 있으면 즉시 교체한다.</summary>
    public static bool IsDeveloperMachine =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORAND_DEV"));
}
