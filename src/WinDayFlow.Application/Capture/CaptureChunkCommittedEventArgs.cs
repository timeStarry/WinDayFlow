namespace WinDayFlow.Application.Capture;

/// <summary>
/// Signals that durable capture evidence may have changed. Consumers must rescan
/// committed manifests instead of treating this notification as evidence.
/// </summary>
public sealed class CaptureChunkCommittedEventArgs : EventArgs
{
    private CaptureChunkCommittedEventArgs()
    {
    }

    public static CaptureChunkCommittedEventArgs WakeHint { get; } = new();
}

public interface ICaptureChunkCommitNotifier
{
    event EventHandler<CaptureChunkCommittedEventArgs>? ChunkCommitted;
}
