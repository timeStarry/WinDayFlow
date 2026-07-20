namespace WinDayFlow.Application.Capture;

public interface ICaptureService
{
    CaptureStatus CurrentStatus { get; }

    event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
