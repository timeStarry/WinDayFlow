using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Capture;

public sealed record CaptureContextApplication
{
    public CaptureContextApplication(
        string applicationId,
        string displayName,
        ApplicationIdentityKind identityKind,
        string identityValue,
        uint processId,
        uint cpuUsageBasisPoints,
        long workingSetBytes,
        long privateMemoryBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityValue);
        if (applicationId.Length > 320 || applicationId.Any(char.IsControl)
            || !string.Equals(applicationId, applicationId.Trim(), StringComparison.Ordinal)
            || displayName.Length > 160 || displayName.Any(char.IsControl)
            || !string.Equals(displayName, displayName.Trim(), StringComparison.Ordinal)
            || !Enum.IsDefined(identityKind)
            || processId == 0 || cpuUsageBasisPoints > 10_000
            || workingSetBytes < 0 || privateMemoryBytes < 0)
        {
            throw new ArgumentException("The capture application context is invalid.");
        }

        ApplicationId = applicationId;
        DisplayName = displayName;
        IdentityKind = identityKind;
        IdentityValue = identityValue;
        ProcessId = processId;
        CpuUsageBasisPoints = cpuUsageBasisPoints;
        WorkingSetBytes = workingSetBytes;
        PrivateMemoryBytes = privateMemoryBytes;
    }

    public string ApplicationId { get; }
    public string DisplayName { get; }
    public ApplicationIdentityKind IdentityKind { get; }
    public string IdentityValue { get; }
    public uint ProcessId { get; }
    public uint CpuUsageBasisPoints { get; }
    public long WorkingSetBytes { get; }
    public long PrivateMemoryBytes { get; }
}

public sealed record CaptureContextRuleMatch(Guid RuleId, long RuleRevision)
{
    public Guid RuleId { get; } = RuleId != Guid.Empty
        ? RuleId
        : throw new ArgumentException("A rule match requires an identifier.", nameof(RuleId));

    public long RuleRevision { get; } = RuleRevision > 0
        ? RuleRevision
        : throw new ArgumentOutOfRangeException(nameof(RuleRevision));
}

public sealed record CaptureContextSample
{
    public CaptureContextSample(
        string captureChunkId,
        int ordinal,
        DateTimeOffset sampledAt,
        CaptureContextApplication? application,
        IReadOnlyList<CaptureContextRuleMatch>? ruleMatches = null,
        long? evaluatedRuleSetRevision = null,
        bool applicationContextAvailable = false,
        bool windowContextAvailable = false)
    {
        CaptureChunk.ValidateIdentifier(captureChunkId);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (evaluatedRuleSetRevision is <= 0
            || (!evaluatedRuleSetRevision.HasValue
                && (applicationContextAvailable || windowContextAvailable)))
        {
            throw new ArgumentException(
                "Capture rule-evaluation metadata is inconsistent.",
                nameof(evaluatedRuleSetRevision));
        }

        CaptureChunkId = captureChunkId;
        Ordinal = ordinal;
        SampledAt = sampledAt;
        Application = application;
        RuleMatches = Array.AsReadOnly((ruleMatches ?? []).ToArray());
        EvaluatedRuleSetRevision = evaluatedRuleSetRevision;
        ApplicationContextAvailable = applicationContextAvailable;
        WindowContextAvailable = windowContextAvailable;
    }

    public string CaptureChunkId { get; }
    public int Ordinal { get; }
    public DateTimeOffset SampledAt { get; }
    public CaptureContextApplication? Application { get; }
    public IReadOnlyList<CaptureContextRuleMatch> RuleMatches { get; }
    public long? EvaluatedRuleSetRevision { get; }
    public bool ApplicationContextAvailable { get; }
    public bool WindowContextAvailable { get; }
}

public sealed record CaptureContextRuleEvaluation
{
    public CaptureContextRuleEvaluation(
        long ruleSetRevision,
        bool applicationContextAvailable,
        bool windowContextAvailable,
        IReadOnlyList<CaptureContextRuleMatch>? ruleMatches = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ruleSetRevision);

        RuleSetRevision = ruleSetRevision;
        ApplicationContextAvailable = applicationContextAvailable;
        WindowContextAvailable = windowContextAvailable;
        RuleMatches = Array.AsReadOnly((ruleMatches ?? []).ToArray());
    }

    public long RuleSetRevision { get; }
    public bool ApplicationContextAvailable { get; }
    public bool WindowContextAvailable { get; }
    public IReadOnlyList<CaptureContextRuleMatch> RuleMatches { get; }
}

public interface ICaptureRuleObservationSource
{
    CaptureContextRuleEvaluation? FindAt(DateTimeOffset sampledAt);
}

public interface ICaptureManifestContextSource
{
    Task<IReadOnlyList<CaptureContextSample>> ReadContextAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default);
}

public interface ICaptureContextStore
{
    Task ReplaceAsync(
        CaptureChunk chunk,
        IReadOnlyList<CaptureContextSample> samples,
        CaptureExclusionRuleSet rules,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureContextSample>> ListAsync(
        string captureChunkId,
        CancellationToken cancellationToken = default);
}
