namespace WinDayFlow.Application.Capture;

public interface ICaptureBackend
{
    CaptureStatus CurrentStatus { get; }

    event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

    Task StartAsync(
        ICaptureRuntimeAdmissionStamp admissionStamp,
        CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(
        ICaptureRuntimeAdmissionStamp admissionStamp,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
