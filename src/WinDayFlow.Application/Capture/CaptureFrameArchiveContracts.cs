using WinDayFlow.Domain;

namespace WinDayFlow.Application.Capture;

public sealed record CaptureFrameDescriptor
{
    public CaptureFrameDescriptor(
        string captureChunkId,
        uint index,
        DateTimeOffset capturedAt,
        ulong offsetMilliseconds,
        EvidenceRelativePath relativePath,
        uint byteCount,
        string sha256)
    {
        CaptureChunk.ValidateIdentifier(captureChunkId);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (byteCount is < 4 or > 2 * 1024 * 1024
            || sha256.Length != 64
            || sha256.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException("The frame integrity metadata is invalid.");
        }

        CaptureChunkId = captureChunkId;
        Index = index;
        CapturedAt = capturedAt;
        OffsetMilliseconds = offsetMilliseconds;
        RelativePath = relativePath;
        ByteCount = byteCount;
        Sha256 = sha256;
    }

    public string CaptureChunkId { get; }
    public uint Index { get; }
    public DateTimeOffset CapturedAt { get; }
    public ulong OffsetMilliseconds { get; }
    public EvidenceRelativePath RelativePath { get; }
    public uint ByteCount { get; }
    public string Sha256 { get; }
}

public interface ICaptureFrameArchive
{
    Task<IReadOnlyList<CaptureFrameDescriptor>> ListFramesAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadFrameBytesAsync(
        CaptureFrameDescriptor frame,
        CancellationToken cancellationToken = default);
}
