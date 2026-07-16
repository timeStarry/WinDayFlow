namespace WinDayFlow.Application.Capture;

public enum CaptureReasonCode
{
    None = 0,
    ConsentRequired = 1,
    UserPaused = 2,
    UserStopped = 3,
    ExcludedApplication = 4,
    ExcludedWindow = 5,
    SessionLocked = 6,
    SecureDesktop = 7,
    RemoteSession = 8,
    PresentationMode = 9,
    SystemSleep = 10,
    DisplayUnavailable = 11,
    AccessLost = 12,
    StorageConstrained = 13,
    PolicyBlocked = 14,
    BackendUnavailable = 15,
    BackendFault = 16,
    Shutdown = 17,
}
