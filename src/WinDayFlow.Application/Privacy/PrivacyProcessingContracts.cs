using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Privacy;

public enum PrivacyScreeningVerdict
{
    Clear = 0,
    Sensitive = 1,
    Inconclusive = 2,
}

public enum PrivacyScreeningState
{
    Pending = 0,
    Inspecting = 1,
    Clear = 2,
    Redacted = 3,
    Held = 4,
    NeedsReview = 5,
    FailedRetryable = 6,
    FailedTerminal = 7,
}

public enum PrivacyFindingKind
{
    SensitiveText = 0,
    Credential = 1,
    Password = 2,
    Secret = 3,
    Other = 255,
}

public readonly record struct NormalizedPrivacyRegion
{
    public NormalizedPrivacyRegion(double x, double y, double width, double height)
    {
        if (!IsUnit(x) || !IsUnit(y) || width <= 0 || height <= 0
            || x + width > 1 || y + height > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "A privacy region must be a positive rectangle inside normalized image bounds.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    private static bool IsUnit(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 1;
}

public sealed record PrivacyFinding(
    string FrameId,
    PrivacyFindingKind Kind,
    NormalizedPrivacyRegion Region,
    double? Confidence)
{
    public string FrameId { get; } = !string.IsNullOrWhiteSpace(FrameId)
        && FrameId.Length <= 128
        && !FrameId.Any(char.IsControl)
            ? FrameId
            : throw new ArgumentException("A privacy finding requires a bounded frame identifier.", nameof(FrameId));

    public PrivacyFindingKind Kind { get; } = Enum.IsDefined(Kind)
        ? Kind
        : throw new ArgumentOutOfRangeException(nameof(Kind));

    public double? Confidence { get; } = Confidence is null
        || double.IsFinite(Confidence.Value) && Confidence is >= 0 and <= 1
            ? Confidence
            : throw new ArgumentOutOfRangeException(nameof(Confidence));
}

public sealed record PrivacyScreeningResult(
    string SchemaVersion,
    PrivacyScreeningVerdict Verdict,
    IReadOnlyList<PrivacyFinding> Findings)
{
    public const string CurrentSchemaVersion = "privacy-v1";

    public string SchemaVersion { get; } = SchemaVersion == CurrentSchemaVersion
        ? SchemaVersion
        : throw new ArgumentException("The privacy response schema is unsupported.", nameof(SchemaVersion));

    public PrivacyScreeningVerdict Verdict { get; } = Enum.IsDefined(Verdict)
        ? Verdict
        : throw new ArgumentOutOfRangeException(nameof(Verdict));

    public IReadOnlyList<PrivacyFinding> Findings { get; } = Findings is { Count: <= 256 }
        ? Findings.ToArray()
        : throw new ArgumentException("Privacy findings exceed the supported bound.", nameof(Findings));
}

public sealed record PrivacyInspectionRequest
{
    public PrivacyInspectionRequest(
        Guid correlationId,
        string captureChunkId,
        string evidenceFingerprint,
        IReadOnlyList<AiEvidenceImage> images)
    {
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("A privacy request requires a correlation identifier.", nameof(correlationId));
        }
        CaptureChunk.ValidateIdentifier(captureChunkId);
        if (evidenceFingerprint.Length != 64
            || evidenceFingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A privacy request requires a SHA-256 evidence fingerprint.", nameof(evidenceFingerprint));
        }
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count is 0 or > AiAnalysisContract.MaximumImages
            || images.Any(static image => image is null))
        {
            throw new ArgumentException("A privacy request requires bounded JPEG evidence.", nameof(images));
        }
        CorrelationId = correlationId;
        CaptureChunkId = captureChunkId;
        EvidenceFingerprint = evidenceFingerprint;
        Images = Array.AsReadOnly(images.ToArray());
    }

    public Guid CorrelationId { get; }
    public string CaptureChunkId { get; }
    public string EvidenceFingerprint { get; }
    public IReadOnlyList<AiEvidenceImage> Images { get; }
}

public sealed record PrivacyInspectionResponse(
    PrivacyScreeningResult Result,
    AiTokenUsage? TokenUsage,
    string? ProviderRequestId);

public interface IPrivacyInspectionProvider
{
    Task<PrivacyInspectionResponse> InspectPrivacyAsync(
        PrivacyInspectionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PrivacyScreeningSnapshot(
    Guid Id,
    string CaptureChunkId,
    Guid ProviderProfileId,
    long ProviderProfileRevision,
    long RouteRevision,
    string InputFingerprint,
    PrivacyScreeningState State,
    PrivacyScreeningVerdict? Verdict,
    PrivacyScreeningResult? Result,
    EvidenceRelativePath? DerivativeManifestPath,
    string? OutputFingerprint,
    int Attempt,
    int? ErrorCode,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface IPrivacyScreeningStore
{
    Task<PrivacyScreeningSnapshot?> GetAsync(
        string captureChunkId,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        string inputFingerprint,
        CancellationToken cancellationToken = default);

    Task<PrivacyScreeningSnapshot> SaveAsync(
        PrivacyScreeningSnapshot screening,
        CancellationToken cancellationToken = default);

    Task<PrivacyScreeningSnapshot?> FindByOutputAsync(
        string captureChunkId,
        string outputFingerprint,
        CancellationToken cancellationToken = default);
}

public enum PrivacyEvidenceStatus
{
    ReadyOriginal = 0,
    ReadyRedacted = 1,
    Held = 2,
    NeedsReview = 3,
    BlockedByRule = 4,
    NotReady = 5,
}

public sealed record PrivacyEvidenceSelection(
    PrivacyEvidenceStatus Status,
    CaptureChunkFingerprint? Fingerprint,
    EvidenceRelativePath? ManifestPath,
    Guid? ScreeningId,
    long? ScreeningRevision,
    Guid? ProviderProfileId = null,
    long? ProviderProfileRevision = null,
    long PrivacyRouteRevision = 0)
{
    public bool IsReady => Status is PrivacyEvidenceStatus.ReadyOriginal
        or PrivacyEvidenceStatus.ReadyRedacted;
}

public interface IPrivacyScreeningService
{
    Task<PrivacyEvidenceSelection> PrepareAsync(
        CaptureChunk chunk,
        CaptureChunkFingerprint originalFingerprint,
        Guid logicalOperationId,
        CancellationToken cancellationToken = default);
}

public sealed record PrivacyRedactionResult(
    EvidenceRelativePath ManifestPath,
    CaptureChunkFingerprint Fingerprint,
    int RedactedFrameCount);

public interface IPrivacyEvidenceRedactor
{
    Task<PrivacyRedactionResult> RedactAsync(
        Guid screeningId,
        CaptureChunk chunk,
        CaptureChunkFingerprint sourceFingerprint,
        IReadOnlyList<PrivacyFinding> findings,
        CancellationToken cancellationToken = default);
}

public enum EvidenceSendDecisionKind
{
    Allowed = 0,
    AllowedByOverride = 1,
    BlockedByRule = 2,
    BlockedMissingContext = 3,
}

public sealed record EvidenceSendDecision(
    EvidenceSendDecisionKind Kind,
    IReadOnlyList<CaptureContextRuleMatch> RuleMatches)
{
    public bool IsAllowed => Kind is EvidenceSendDecisionKind.Allowed
        or EvidenceSendDecisionKind.AllowedByOverride;
}

public interface IEvidenceSendPolicy
{
    Task<EvidenceSendDecision> EvaluateAsync(
        CaptureChunk chunk,
        AnalysisStage stage,
        AiProviderProfileSnapshot profile,
        AnalysisStageBinding route,
        CaptureChunkFingerprint evidenceFingerprint,
        Guid logicalOperationId,
        CancellationToken cancellationToken = default);
}

public sealed record EvidenceSendOverride(
    Guid Id,
    string CaptureChunkId,
    AnalysisStage Stage,
    Guid ProviderProfileId,
    long ProviderProfileRevision,
    long RouteRevision,
    string EvidenceFingerprint,
    Guid LogicalOperationId,
    int RemainingUses,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public interface IEvidenceSendOverrideStore
{
    Task<EvidenceSendOverride> CreateAsync(
        EvidenceSendOverride value,
        CancellationToken cancellationToken = default);

    Task<bool> TryConsumeAsync(
        string captureChunkId,
        AnalysisStage stage,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        string evidenceFingerprint,
        Guid logicalOperationId,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default);
}

public enum ProviderInvocationOutcome
{
    Started = 0,
    Succeeded = 1,
    FailedRetryable = 2,
    FailedTerminal = 3,
    Cancelled = 4,
}

public sealed record ProviderInvocationUsage(
    long? InputTokens,
    long? OutputTokens)
{
    public long? InputTokens { get; } = InputTokens is null or >= 0
        ? InputTokens
        : throw new ArgumentOutOfRangeException(nameof(InputTokens));

    public long? OutputTokens { get; } = OutputTokens is null or >= 0
        ? OutputTokens
        : throw new ArgumentOutOfRangeException(nameof(OutputTokens));
}

public sealed record ProviderInvocationStart(
    Guid Id,
    AnalysisStage Stage,
    Guid ProviderProfileId,
    long ProviderProfileRevision,
    long RouteRevision,
    string EndpointOrigin,
    string EvidenceFingerprint,
    int ItemCount,
    long ByteCount,
    DateTimeOffset StartedAtUtc,
    Guid CorrelationId);

public interface IProviderInvocationStore
{
    Task StartAsync(
        ProviderInvocationStart invocation,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid invocationId,
        ProviderInvocationOutcome outcome,
        ProviderInvocationUsage? usage,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);
}
