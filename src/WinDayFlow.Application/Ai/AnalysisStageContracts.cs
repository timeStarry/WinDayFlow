namespace WinDayFlow.Application.Ai;

public enum AnalysisStage
{
    PrivacyInspection = 0,
    TimelineAnalysis = 1,
}

public enum PrivacyMatchAction
{
    AuditOnly = 0,
    RedactAndContinue = 1,
    Hold = 2,
    RequireReview = 3,
}

public enum PrivacyFailureAction
{
    Hold = 0,
    PassThrough = 1,
    RequireReview = 2,
}

public sealed record PrivacyStageOptions(
    PrivacyMatchAction OnMatch,
    PrivacyFailureAction OnError)
{
    public static PrivacyStageOptions Default { get; } = new(
        PrivacyMatchAction.RedactAndContinue,
        PrivacyFailureAction.Hold);

    public PrivacyMatchAction OnMatch { get; } = Validate(OnMatch);

    public PrivacyFailureAction OnError { get; } = Validate(OnError);

    private static T Validate<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The stage option is not defined.");
        }

        return value;
    }
}

public sealed record AnalysisStageBinding
{
    public AnalysisStageBinding(
        AnalysisStage stage,
        bool enabled,
        Guid? providerProfileId,
        long routeRevision,
        PrivacyStageOptions? privacyOptions = null)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(routeRevision);

        if (enabled
            && (!providerProfileId.HasValue || providerProfileId.Value == Guid.Empty))
        {
            throw new ArgumentException(
                "An enabled analysis stage requires a provider profile.",
                nameof(providerProfileId));
        }

        if (providerProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A stage provider identifier cannot be empty.",
                nameof(providerProfileId));
        }

        if (stage != AnalysisStage.PrivacyInspection && privacyOptions is not null)
        {
            throw new ArgumentException(
                "Privacy options are valid only for the privacy-inspection stage.",
                nameof(privacyOptions));
        }

        Stage = stage;
        Enabled = enabled;
        ProviderProfileId = providerProfileId;
        RouteRevision = routeRevision;
        PrivacyOptions = stage == AnalysisStage.PrivacyInspection
            ? privacyOptions ?? PrivacyStageOptions.Default
            : null;
    }

    public AnalysisStage Stage { get; }

    public bool Enabled { get; }

    public Guid? ProviderProfileId { get; }

    public long RouteRevision { get; }

    public PrivacyStageOptions? PrivacyOptions { get; }
}

public sealed record ProviderStageValidation(
    Guid ProviderProfileId,
    long ProviderProfileRevision,
    AnalysisStage Stage,
    DateTimeOffset ValidatedAtUtc)
{
    public Guid ProviderProfileId { get; } = ProviderProfileId != Guid.Empty
        ? ProviderProfileId
        : throw new ArgumentException("A validation requires a provider profile.", nameof(ProviderProfileId));

    public long ProviderProfileRevision { get; } = ProviderProfileRevision > 0
        ? ProviderProfileRevision
        : throw new ArgumentOutOfRangeException(nameof(ProviderProfileRevision));

    public AnalysisStage Stage { get; } = Enum.IsDefined(Stage)
        ? Stage
        : throw new ArgumentOutOfRangeException(nameof(Stage));

    public DateTimeOffset ValidatedAtUtc { get; } = ValidatedAtUtc.Offset == TimeSpan.Zero
        ? ValidatedAtUtc
        : throw new ArgumentException("Validation time must be UTC.", nameof(ValidatedAtUtc));
}

public interface IAnalysisStageBindingStore
{
    Task<IReadOnlyList<AnalysisStageBinding>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<AnalysisStageBinding> GetAsync(
        AnalysisStage stage,
        CancellationToken cancellationToken = default);

    Task<AnalysisStageBinding> SaveAsync(
        AnalysisStage stage,
        bool enabled,
        Guid? providerProfileId,
        long expectedRouteRevision,
        PrivacyStageOptions? privacyOptions,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default);

    Task<ProviderStageValidation?> GetValidationAsync(
        Guid profileId,
        long profileRevision,
        AnalysisStage stage,
        CancellationToken cancellationToken = default);

    Task<ProviderStageValidation> MarkValidatedAsync(
        Guid profileId,
        long profileRevision,
        AnalysisStage stage,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class AnalysisStageBindingConflictException : Exception
{
    public AnalysisStageBindingConflictException()
        : base("The analysis-stage binding changed during the operation.")
    {
    }
}
