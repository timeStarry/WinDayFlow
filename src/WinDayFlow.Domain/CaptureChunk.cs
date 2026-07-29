namespace WinDayFlow.Domain;

public sealed record CaptureChunk
{
    public const int MaximumIdentifierLength = 80;
    public const long MaximumFrameByteCount = 64L * 1024L * 1024L;
    public const uint MaximumFramesPerChunk = 720;

    public CaptureChunk(
        string id,
        EvidenceRelativePath manifestPath,
        TimeRange range,
        uint capturedFrameCount,
        uint frameCount,
        uint frameWidth,
        uint frameHeight,
        long frameByteCount,
        ulong persistenceGeneration,
        ulong targetEpoch,
        DateTimeOffset committedAtUtc,
        DateTimeOffset ingestedAtUtc,
        CaptureChunkAvailability availability = CaptureChunkAvailability.Available,
        CaptureProcessTelemetry? processTelemetry = null)
    {
        ValidateIdentifier(id);
        ArgumentNullException.ThrowIfNull(manifestPath);
        ArgumentNullException.ThrowIfNull(range);

        if (!string.Equals(
                manifestPath.Value,
                $"chunks/{id}/manifest.json",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The capture manifest must use the canonical chunk directory.",
                nameof(manifestPath));
        }

        if (capturedFrameCount == 0
            || frameCount == 0
            || frameCount > capturedFrameCount
            || frameCount > MaximumFramesPerChunk)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (frameWidth < 2
            || frameHeight < 2
            || (frameWidth & 1U) != 0
            || (frameHeight & 1U) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameWidth),
                "Capture dimensions must be positive even values.");
        }

        if (frameByteCount is <= 0 or > MaximumFrameByteCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameByteCount));
        }

        ArgumentOutOfRangeException.ThrowIfZero(persistenceGeneration);
        ArgumentOutOfRangeException.ThrowIfZero(targetEpoch);
        if (!Enum.IsDefined(availability))
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        Id = id;
        ManifestPath = manifestPath;
        Range = range;
        CapturedFrameCount = capturedFrameCount;
        FrameCount = frameCount;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        FrameByteCount = frameByteCount;
        PersistenceGeneration = persistenceGeneration;
        TargetEpoch = targetEpoch;
        CommittedAtUtc = committedAtUtc.ToUniversalTime();
        IngestedAtUtc = ingestedAtUtc.ToUniversalTime();
        Availability = availability;
        ProcessTelemetry = processTelemetry;
    }

    public string Id { get; }
    public EvidenceRelativePath ManifestPath { get; }
    public TimeRange Range { get; }
    public uint CapturedFrameCount { get; }
    public uint FrameCount { get; }
    public uint FrameWidth { get; }
    public uint FrameHeight { get; }
    public long FrameByteCount { get; }
    public ulong PersistenceGeneration { get; }
    public ulong TargetEpoch { get; }
    public DateTimeOffset CommittedAtUtc { get; }
    public DateTimeOffset IngestedAtUtc { get; }
    public CaptureChunkAvailability Availability { get; }
    public CaptureProcessTelemetry? ProcessTelemetry { get; }

    public static void ValidateIdentifier(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > MaximumIdentifierLength
            || !string.Equals(id, id.Trim(), StringComparison.Ordinal)
            || id.Any(static character =>
                !(character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_')))
        {
            throw new ArgumentException(
                "A capture chunk identifier must be canonical lowercase ASCII.",
                nameof(id));
        }
    }
}
