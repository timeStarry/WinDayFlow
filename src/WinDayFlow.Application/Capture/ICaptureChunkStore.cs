using WinDayFlow.Domain;

namespace WinDayFlow.Application.Capture;

public interface ICaptureChunkStore
{
    Task<CaptureChunkIngestResult> IngestCommittedAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default);

    Task<CaptureChunk?> GetAsync(
        string chunkId,
        CancellationToken cancellationToken = default);
}

public sealed record CaptureChunkIngestResult(CaptureChunk Chunk, bool Created);
