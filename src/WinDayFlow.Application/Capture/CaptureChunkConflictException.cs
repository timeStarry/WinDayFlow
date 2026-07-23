namespace WinDayFlow.Application.Capture;

public sealed class CaptureChunkConflictException : InvalidOperationException
{
    public CaptureChunkConflictException(string chunkId)
        : base($"Capture chunk '{chunkId}' conflicts with persisted evidence metadata.")
    {
        ChunkId = chunkId;
    }

    public string ChunkId { get; }
}
