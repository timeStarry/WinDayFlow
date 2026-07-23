namespace WinDayFlow.Domain;

public sealed record AnalysisJob
{
    public const int MaximumAnalysisVersionLength = 128;

    public AnalysisJob(
        Guid id,
        string captureChunkId,
        Guid providerProfileId,
        long providerProfileRevision,
        string analysisVersion,
        string inputFingerprint,
        AnalysisJobState state,
        int attempt,
        int maxAttempts,
        DateTimeOffset? notBeforeUtc,
        AnalysisJobLease? lease,
        AnalysisJobFailure? failure,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? completedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An analysis job requires an identifier.", nameof(id));
        }

        CaptureChunk.ValidateIdentifier(captureChunkId);
        if (providerProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "An analysis job requires a provider profile identifier.",
                nameof(providerProfileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(providerProfileRevision);

        ArgumentException.ThrowIfNullOrWhiteSpace(analysisVersion);
        if (analysisVersion.Length > MaximumAnalysisVersionLength
            || !string.Equals(analysisVersion, analysisVersion.Trim(), StringComparison.Ordinal)
            || analysisVersion.Any(char.IsControl))
        {
            throw new ArgumentException("The analysis version is invalid.", nameof(analysisVersion));
        }

        ValidateFingerprint(inputFingerprint);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (maxAttempts is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (attempt < 0 || attempt > maxAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        var created = createdAtUtc.ToUniversalTime();
        var updated = updatedAtUtc.ToUniversalTime();
        var notBefore = notBeforeUtc?.ToUniversalTime();
        var completed = completedAtUtc?.ToUniversalTime();
        if (updated < created || (completed.HasValue && completed.Value < updated))
        {
            throw new ArgumentException("Analysis job timestamps are not monotonic.", nameof(updatedAtUtc));
        }

        ValidateState(
            state,
            id,
            attempt,
            maxAttempts,
            notBefore,
            lease,
            failure,
            updated,
            completed);

        Id = id;
        CaptureChunkId = captureChunkId;
        ProviderProfileId = providerProfileId;
        ProviderProfileRevision = providerProfileRevision;
        AnalysisVersion = analysisVersion;
        InputFingerprint = inputFingerprint;
        State = state;
        Attempt = attempt;
        MaxAttempts = maxAttempts;
        NotBeforeUtc = notBefore;
        Lease = lease;
        Failure = failure;
        CreatedAtUtc = created;
        UpdatedAtUtc = updated;
        CompletedAtUtc = completed;
    }

    public Guid Id { get; }

    public string CaptureChunkId { get; }

    public Guid ProviderProfileId { get; }

    public long ProviderProfileRevision { get; }

    public string AnalysisVersion { get; }

    public string InputFingerprint { get; }

    public AnalysisJobState State { get; }

    public int Attempt { get; }

    public int MaxAttempts { get; }

    public DateTimeOffset? NotBeforeUtc { get; }

    public AnalysisJobLease? Lease { get; }

    public AnalysisJobFailure? Failure { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public static AnalysisJob CreatePending(
        Guid id,
        string captureChunkId,
        Guid providerProfileId,
        long providerProfileRevision,
        string analysisVersion,
        string inputFingerprint,
        int maxAttempts,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? notBeforeUtc = null)
    {
        var created = createdAtUtc.ToUniversalTime();
        return new AnalysisJob(
            id,
            captureChunkId,
            providerProfileId,
            providerProfileRevision,
            analysisVersion,
            inputFingerprint,
            AnalysisJobState.Pending,
            attempt: 0,
            maxAttempts,
            notBeforeUtc ?? created,
            lease: null,
            failure: null,
            created,
            created,
            completedAtUtc: null);
    }

    private static void ValidateFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64
            || fingerprint.Any(static character =>
                !(character is >= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "An analysis input fingerprint must be a 256-bit uppercase hexadecimal value.",
                nameof(fingerprint));
        }
    }

    private static void ValidateState(
        AnalysisJobState state,
        Guid id,
        int attempt,
        int maxAttempts,
        DateTimeOffset? notBefore,
        AnalysisJobLease? lease,
        AnalysisJobFailure? failure,
        DateTimeOffset updated,
        DateTimeOffset? completed)
    {
        var active = AnalysisJobStateMachine.IsActive(state);
        var terminal = AnalysisJobStateMachine.IsTerminal(state);
        if (active != (lease is not null)
            || (lease is not null
                && (lease.JobId != id
                    || lease.Attempt != attempt
                    || lease.ExpiresAtUtc <= updated)))
        {
            throw new ArgumentException("Analysis job lease fields do not match its state.", nameof(lease));
        }

        if (terminal != completed.HasValue)
        {
            throw new ArgumentException(
                "Analysis job completion time does not match its state.",
                nameof(completed));
        }

        var failed = state is AnalysisJobState.FailedRetryable or AnalysisJobState.FailedTerminal;
        if (failed != (failure is not null))
        {
            throw new ArgumentException("Analysis job failure fields do not match its state.", nameof(failure));
        }

        var waiting = state is AnalysisJobState.Pending or AnalysisJobState.FailedRetryable;
        if (waiting != notBefore.HasValue)
        {
            throw new ArgumentException(
                "Analysis job eligibility time does not match its state.",
                nameof(notBefore));
        }

        if (state == AnalysisJobState.FailedRetryable && notBefore < updated)
        {
            throw new ArgumentException(
                "A retryable analysis job cannot become eligible before its failure transition.",
                nameof(notBefore));
        }

        if (state == AnalysisJobState.Pending && attempt != 0
            || state == AnalysisJobState.FailedRetryable && (attempt == 0 || attempt >= maxAttempts)
            || state is not AnalysisJobState.Pending and not AnalysisJobState.Cancelled && attempt == 0)
        {
            throw new ArgumentException("Analysis job attempt count does not match its state.", nameof(attempt));
        }
    }
}
