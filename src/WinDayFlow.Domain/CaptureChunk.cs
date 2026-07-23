namespace WinDayFlow.Domain;

public sealed record CaptureChunk
{
    public const int MaximumIdentifierLength = 80;
    public const long MaximumVideoByteCount = 64L * 1024L * 1024L;

    public CaptureChunk(
        string id,
        EvidenceRelativePath videoPath,
        EvidenceRelativePath manifestPath,
        TimeRange range,
        uint frameCount,
        uint videoWidth,
        uint videoHeight,
        uint frameRateNumerator,
        uint frameRateDenominator,
        long videoByteCount,
        ulong persistenceGeneration,
        ulong targetEpoch,
        DateTimeOffset committedAtUtc,
        DateTimeOffset ingestedAtUtc,
        CaptureChunkAvailability availability = CaptureChunkAvailability.Available)
    {
        ValidateIdentifier(id);
        ArgumentNullException.ThrowIfNull(videoPath);
        ArgumentNullException.ThrowIfNull(manifestPath);
        ArgumentNullException.ThrowIfNull(range);

        var expectedDirectory = $"chunks/{id}";
        if (!string.Equals(
                videoPath.Value,
                $"{expectedDirectory}/capture.mp4",
                StringComparison.Ordinal)
            || !string.Equals(
                manifestPath.Value,
                $"{expectedDirectory}/manifest.json",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Capture evidence paths must use the canonical directory for their chunk identifier.",
                nameof(videoPath));
        }

        ArgumentOutOfRangeException.ThrowIfZero(frameCount);

        if (videoWidth < 2 || videoHeight < 2 || (videoWidth & 1U) != 0 || (videoHeight & 1U) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(videoWidth),
                "Capture dimensions must be positive even values.");
        }

        if (frameRateNumerator == 0 || frameRateDenominator == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRateNumerator),
                "Capture frame-rate components must be positive.");
        }

        if (videoByteCount is <= 0 or > MaximumVideoByteCount)
        {
            throw new ArgumentOutOfRangeException(nameof(videoByteCount));
        }

        ArgumentOutOfRangeException.ThrowIfZero(persistenceGeneration);
        ArgumentOutOfRangeException.ThrowIfZero(targetEpoch);

        if (!Enum.IsDefined(availability))
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        Id = id;
        VideoPath = videoPath;
        ManifestPath = manifestPath;
        Range = range;
        FrameCount = frameCount;
        VideoWidth = videoWidth;
        VideoHeight = videoHeight;
        FrameRateNumerator = frameRateNumerator;
        FrameRateDenominator = frameRateDenominator;
        VideoByteCount = videoByteCount;
        PersistenceGeneration = persistenceGeneration;
        TargetEpoch = targetEpoch;
        CommittedAtUtc = committedAtUtc.ToUniversalTime();
        IngestedAtUtc = ingestedAtUtc.ToUniversalTime();
        Availability = availability;
    }

    public string Id { get; }

    public EvidenceRelativePath VideoPath { get; }

    public EvidenceRelativePath ManifestPath { get; }

    public TimeRange Range { get; }

    public uint FrameCount { get; }

    public uint VideoWidth { get; }

    public uint VideoHeight { get; }

    public uint FrameRateNumerator { get; }

    public uint FrameRateDenominator { get; }

    public long VideoByteCount { get; }

    public ulong PersistenceGeneration { get; }

    public ulong TargetEpoch { get; }

    public DateTimeOffset CommittedAtUtc { get; }

    public DateTimeOffset IngestedAtUtc { get; }

    public CaptureChunkAvailability Availability { get; }

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
