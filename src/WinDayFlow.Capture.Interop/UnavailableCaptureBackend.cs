using WinDayFlow.Application.Capture;

namespace WinDayFlow.Capture.Interop;

public sealed class UnavailableCaptureBackend : ICaptureBackend
{
    private const string UnavailableDetail =
        "当前开发版本尚未接入原生录制组件。";

    private static readonly CaptureStatus Status = new(
        CaptureState.Unavailable,
        DateTimeOffset.UnixEpoch,
        UnavailableDetail,
        Reason: CaptureReasonCode.BackendUnavailable);

    private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;

    public CaptureStatus CurrentStatus => Status;

    public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
    {
        add => _statusChanged += value;
        remove => _statusChanged -= value;
    }

    public Task StartAsync(
        ICaptureRuntimeAdmissionStamp admissionStamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admissionStamp);
        return NotSupportedAsync(cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        NotSupportedAsync(cancellationToken);

    public Task ResumeAsync(
        ICaptureRuntimeAdmissionStamp admissionStamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admissionStamp);
        return NotSupportedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        NotSupportedAsync(cancellationToken);

    private static Task NotSupportedAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromException(new NotSupportedException(UnavailableDetail));
    }
}
