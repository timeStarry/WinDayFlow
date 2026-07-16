namespace WinDayFlow.Application.Capture;

public enum CaptureState
{
    Unavailable = 0,
    Stopped = 1,
    Starting = 2,
    Recording = 3,
    Pausing = 4,
    Paused = 5,
    Resuming = 6,
    Stopping = 7,
    Faulted = 8,
    BlockedByConsent = 9,
}
