namespace WinDayFlow.Application.Capture;

/// <summary>
/// Provides an additional process-local authorization gate for capture lifecycle operations.
/// Persistent capture enablement and recording consent are always checked independently.
/// </summary>
public interface ICaptureRuntimeAuthorization
{
    bool IsCaptureAuthorized { get; }

    /// <summary>
    /// Increases whenever an authorized runtime becomes unauthorized. Consumers use this
    /// generation to preserve a revocation boundary even if authorization later recovers.
    /// </summary>
    long InvalidationGeneration { get; }

    /// <summary>
    /// Attempts to issue a single-use authorization for one Start or Resume command.
    /// A null result is a fail-closed denial.
    /// </summary>
    ValueTask<ICaptureRuntimeAdmissionStamp?> TryIssueAdmissionAsync(
        CaptureAdmissionOperation operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports authorization transitions together with the latest invalidation generation.
    /// </summary>
    event EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? AuthorizationChanged;
}

public sealed class CaptureRuntimeAuthorizationChangedEventArgs : EventArgs
{
    public CaptureRuntimeAuthorizationChangedEventArgs(
        bool isCaptureAuthorized,
        long invalidationGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(invalidationGeneration);

        IsCaptureAuthorized = isCaptureAuthorized;
        InvalidationGeneration = invalidationGeneration;
    }

    public bool IsCaptureAuthorized { get; }

    public long InvalidationGeneration { get; }
}

public sealed class DenyCaptureRuntimeAuthorization : ICaptureRuntimeAuthorization
{
    private DenyCaptureRuntimeAuthorization()
    {
    }

    public static DenyCaptureRuntimeAuthorization Instance { get; } = new();

    public bool IsCaptureAuthorized => false;

    public long InvalidationGeneration => 0;

    public ValueTask<ICaptureRuntimeAdmissionStamp?> TryIssueAdmissionAsync(
        CaptureAdmissionOperation operation,
        CancellationToken cancellationToken = default)
    {
        _ = operation;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ICaptureRuntimeAdmissionStamp?>(null);
    }

    public event EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? AuthorizationChanged
    {
        add { }
        remove { }
    }
}
