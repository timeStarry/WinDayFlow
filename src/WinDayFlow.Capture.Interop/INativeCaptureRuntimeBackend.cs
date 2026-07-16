using WinDayFlow.Application.Capture;

namespace WinDayFlow.Capture.Interop;

internal interface INativeCaptureRuntimeBackend
    : ICaptureBackend, INativeCaptureAuthorizationTarget
{
    NativeCaptureCapabilities Capabilities { get; }

    Task RequestStopForShutdownAsync();

    Task WaitStoppedForShutdownAsync(uint timeoutMilliseconds);

    Task StopEventPumpAsync();

    NativeCaptureResult DestroyForShutdown();

    void CompleteOwnedShutdown();

    void DisposeSafelyAfterConstructionFailure();
}
