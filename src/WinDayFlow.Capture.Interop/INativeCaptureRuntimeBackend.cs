using WinDayFlow.Application.Capture;

namespace WinDayFlow.Capture.Interop;

internal interface INativeCaptureRuntimeBackend
    : INativeCaptureAuthorizationTarget,
      ICaptureChunkCommitNotifier
{
    NativeCaptureCapabilities Capabilities { get; }

    CaptureStatus CurrentStatus { get; }

    Task UpdateTimingAsync(
        uint captureIntervalMilliseconds,
        uint chunkDurationMilliseconds,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "The native capture backend does not support timing updates."));

    event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

    Task<NativeCaptureCommandAdmissionV1?> TryIssueCommandAdmissionAsync(
        CaptureAdmissionOperation operation,
        ulong expectedRuntimePolicyRevision,
        ulong expectedPersistenceGeneration,
        ulong expectedTargetEpoch,
        CancellationToken cancellationToken = default);

    Task StartAuthorizedAsync(
        NativeCaptureCommandAdmissionV1 admission,
        CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAuthorizedAsync(
        NativeCaptureCommandAdmissionV1 admission,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task RequestStopForShutdownAsync();

    Task WaitStoppedForShutdownAsync(uint timeoutMilliseconds);

    Task StopEventPumpAsync();

    NativeCaptureResult DestroyForShutdown();

    void CompleteOwnedShutdown();

    void DisposeSafelyAfterConstructionFailure();
}
