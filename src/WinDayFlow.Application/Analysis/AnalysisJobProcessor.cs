using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public enum AnalysisJobProcessStatus
{
    NotReady = 0,
    NoWork = 1,
    Completed = 2,
    FailedRetryable = 3,
    FailedTerminal = 4,
    LeaseLost = 5,
}

public sealed record AnalysisJobProcessResult(
    AnalysisJobProcessStatus Status,
    Guid? JobId = null,
    AnalysisJobErrorCode? FailureCode = null);

public sealed record AnalysisJobProcessorOptions
{
    public AnalysisJobProcessorOptions(
        string leaseOwner,
        TimeSpan claimLeaseDuration,
        TimeSpan extractionTimeout,
        TimeSpan leaseSafetyMargin,
        TimeSpan retryDelay,
        string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseOwner.Length > AnalysisJobLease.MaximumOwnerLength
            || !string.Equals(leaseOwner, leaseOwner.Trim(), StringComparison.Ordinal)
            || leaseOwner.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The analysis lease owner is invalid.", nameof(leaseOwner));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            claimLeaseDuration,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            extractionTimeout,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            leaseSafetyMargin,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);
        AiEvidenceImage.ValidateIdentifier(locale, nameof(locale), 35);

        LeaseOwner = leaseOwner;
        ClaimLeaseDuration = claimLeaseDuration;
        ExtractionTimeout = extractionTimeout;
        LeaseSafetyMargin = leaseSafetyMargin;
        RetryDelay = retryDelay;
        Locale = locale;
    }

    public string LeaseOwner { get; }

    public TimeSpan ClaimLeaseDuration { get; }

    public TimeSpan ExtractionTimeout { get; }

    public TimeSpan LeaseSafetyMargin { get; }

    public TimeSpan RetryDelay { get; }

    public string Locale { get; }

    public static AnalysisJobProcessorOptions CreateDefault(string leaseOwner)
    {
        var locale = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrWhiteSpace(locale))
        {
            locale = "en-US";
        }

        return new AnalysisJobProcessorOptions(
            leaseOwner,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            locale);
    }
}

public sealed class AnalysisJobProcessor
{
    public const string TimelinePromptVersion = "timeline-v1";

    private readonly IAnalysisJobStore _jobStore;
    private readonly ICaptureChunkStore _chunkStore;
    private readonly IAiProviderProfileStore _profileStore;
    private readonly IAiAnalysisProviderFactory _providerFactory;
    private readonly IAnalysisEvidenceExtractor _evidenceExtractor;
    private readonly IAnalysisResultCommitter _resultCommitter;
    private readonly AppSettingsService _settings;
    private readonly AnalysisJobProcessorOptions _options;
    private readonly TimeProvider _timeProvider;

    public AnalysisJobProcessor(
        IAnalysisJobStore jobStore,
        ICaptureChunkStore chunkStore,
        IAiProviderProfileStore profileStore,
        IAiAnalysisProviderFactory providerFactory,
        IAnalysisEvidenceExtractor evidenceExtractor,
        IAnalysisResultCommitter resultCommitter,
        AppSettingsService settings,
        AnalysisJobProcessorOptions options,
        TimeProvider? timeProvider = null)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _providerFactory = providerFactory
            ?? throw new ArgumentNullException(nameof(providerFactory));
        _evidenceExtractor = evidenceExtractor
            ?? throw new ArgumentNullException(nameof(evidenceExtractor));
        _resultCommitter = resultCommitter
            ?? throw new ArgumentNullException(nameof(resultCommitter));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AnalysisJobProcessResult> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = await GetRunnableProfileBeforeClaimAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return new AnalysisJobProcessResult(AnalysisJobProcessStatus.NotReady);
        }

        var claimedAt = GetUtcNow();
        var current = await _jobStore
            .TryClaimNextAsync(
                _options.LeaseOwner,
                claimedAt,
                _options.ClaimLeaseDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return new AnalysisJobProcessResult(AnalysisJobProcessStatus.NoWork);
        }

        try
        {
            var readiness = await CheckClaimedJobReadinessAsync(
                    current,
                    cancellationToken)
                .ConfigureAwait(false);
            if (readiness.Status != ClaimedJobReadinessStatus.Ready)
            {
                return await FailForReadinessAsync(current, readiness.Status, cancellationToken)
                    .ConfigureAwait(false);
            }

            profile = readiness.Profile!;
            var chunk = await _chunkStore
                .GetAsync(current.CaptureChunkId, cancellationToken)
                .ConfigureAwait(false);
            if (chunk is null || chunk.Availability != CaptureChunkAvailability.Available)
            {
                return await FailAsync(
                        current,
                        AnalysisJobErrorCode.EvidenceMissing,
                        AnalysisFailureDisposition.Terminal,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            current = await TransitionAsync(
                    current,
                    AnalysisJobState.Claimed,
                    AnalysisJobState.Extracting,
                    cancellationToken)
                .ConfigureAwait(false);
            current = await EnsureLeaseCoversAsync(
                    current,
                    _options.ExtractionTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            AnalysisEvidenceBatch evidence;
            try
            {
                evidence = await ExtractWithTimeoutAsync(chunk, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return await FailAsync(
                        current,
                        AnalysisJobErrorCode.OperationTimedOut,
                        AnalysisFailureDisposition.Retryable,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var (code, disposition) = MapEvidenceFailure(exception);
                return await FailAsync(
                        current,
                        code,
                        disposition,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            AiAnalysisRequest request;
            try
            {
                request = new AiAnalysisRequest(
                    Guid.NewGuid(),
                    current.Id,
                    current.Attempt,
                    current.CaptureChunkId,
                    evidence.ArtifactPath,
                    chunk.Range,
                    TimelinePromptVersion,
                    AiAnalysisContract.CurrentSchemaVersion,
                    _options.Locale,
                    evidence.Images,
                    evidence.Context);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                return await FailAsync(
                        current,
                        AnalysisJobErrorCode.EvidenceInvalid,
                        AnalysisFailureDisposition.Terminal,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            current = await TransitionAsync(
                    current,
                    AnalysisJobState.Extracting,
                    AnalysisJobState.Observing,
                    cancellationToken)
                .ConfigureAwait(false);

            readiness = await CheckClaimedJobReadinessAsync(current, cancellationToken)
                .ConfigureAwait(false);
            if (readiness.Status != ClaimedJobReadinessStatus.Ready)
            {
                return await FailForReadinessAsync(current, readiness.Status, cancellationToken)
                    .ConfigureAwait(false);
            }

            profile = readiness.Profile!;
            current = await EnsureLeaseCoversAsync(
                    current,
                    profile.Profile.RequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            AiAnalysisResponse response;
            try
            {
                var provider = await _providerFactory
                    .CreateAsync(profile, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    EnsureProviderMatchesProfile(provider, profile);
                    response = await provider
                        .AnalyzeAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    (provider as IDisposable)?.Dispose();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return await FailAsync(
                        current,
                        AnalysisJobErrorCode.OperationTimedOut,
                        AnalysisFailureDisposition.Retryable,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var mapped = MapProviderFailure(exception);
                return await FailAsync(
                        current,
                        mapped.Code,
                        mapped.Disposition,
                        mapped.RetryDelay ?? _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            current = await TransitionAsync(
                    current,
                    AnalysisJobState.Observing,
                    AnalysisJobState.Summarizing,
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<TimelineEntry> entries;
            try
            {
                var activities = AiAnalysisResponseValidator.Validate(request, response);
                entries = activities
                    .Select((activity, index) => TimelineEntry.FromActivity(
                        CreateTimelineEntryId(current.Id, index),
                        activity,
                        current.AnalysisVersion))
                    .ToArray();
            }
            catch (Exception exception) when (exception is
                AiAnalysisValidationException
                or ArgumentException
                or OverflowException)
            {
                return await FailAsync(
                        current,
                        AnalysisJobErrorCode.ProviderResponseInvalid,
                        AnalysisFailureDisposition.Terminal,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            readiness = await CheckClaimedJobReadinessAsync(current, cancellationToken)
                .ConfigureAwait(false);
            if (readiness.Status != ClaimedJobReadinessStatus.Ready)
            {
                return await FailForReadinessAsync(current, readiness.Status, cancellationToken)
                    .ConfigureAwait(false);
            }

            current = await TransitionAsync(
                    current,
                    AnalysisJobState.Summarizing,
                    AnalysisJobState.Committing,
                    cancellationToken)
                .ConfigureAwait(false);

            AnalysisResultCommitStatus commitStatus;
            try
            {
                commitStatus = await _resultCommitter
                    .TryCommitAsync(
                        current.Lease!,
                        current.ProviderProfileId,
                        current.ProviderProfileRevision,
                        entries,
                        GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return await FailAsync(
                        current,
                        AnalysisJobErrorCode.PersistenceFailure,
                        AnalysisFailureDisposition.Retryable,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return commitStatus switch
            {
                AnalysisResultCommitStatus.Committed => new AnalysisJobProcessResult(
                    AnalysisJobProcessStatus.Completed,
                    current.Id),
                AnalysisResultCommitStatus.LeaseLost => new AnalysisJobProcessResult(
                    AnalysisJobProcessStatus.LeaseLost,
                    current.Id),
                AnalysisResultCommitStatus.CloudAnalysisDisabled => await FailAsync(
                        current,
                        AnalysisJobErrorCode.ProviderUnavailable,
                        AnalysisFailureDisposition.Retryable,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false),
                AnalysisResultCommitStatus.ProviderRevisionChanged => await FailAsync(
                        current,
                        AnalysisJobErrorCode.ProviderRejected,
                        AnalysisFailureDisposition.Terminal,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false),
                AnalysisResultCommitStatus.EntryConflict => await FailAsync(
                        current,
                        AnalysisJobErrorCode.PersistenceFailure,
                        AnalysisFailureDisposition.Terminal,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    "The analysis result committer returned an unsupported status."),
            };
        }
        catch (AnalysisLeaseLostException)
        {
            return new AnalysisJobProcessResult(
                AnalysisJobProcessStatus.LeaseLost,
                current.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await FailAsync(
                    current,
                    AnalysisJobErrorCode.PersistenceFailure,
                    AnalysisFailureDisposition.Retryable,
                    _options.RetryDelay,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static Guid CreateTimelineEntryId(Guid jobId, int activityIndex)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A timeline entry job identifier cannot be empty.", nameof(jobId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(activityIndex);
        var name = string.Concat(
            TimelinePromptVersion,
            ":",
            jobId.ToString("D", CultureInfo.InvariantCulture),
            ":",
            activityIndex.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private async Task<AiProviderProfileSnapshot?> GetRunnableProfileBeforeClaimAsync(
        CancellationToken cancellationToken)
    {
        if (!_settings.Current.CloudAnalysisEnabled)
        {
            return null;
        }

        var profile = await _profileStore
            .GetActiveAsync(cancellationToken)
            .ConfigureAwait(false);
        return _settings.Current.CloudAnalysisEnabled
            && profile is { IsComplete: true, IsValidated: true }
                ? profile
                : null;
    }

    private async Task<ClaimedJobReadiness> CheckClaimedJobReadinessAsync(
        AnalysisJob job,
        CancellationToken cancellationToken)
    {
        var profile = await _profileStore
            .GetActiveAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profile is null
            || profile.Profile.Id != job.ProviderProfileId
            || profile.Revision != job.ProviderProfileRevision
            || !profile.IsComplete
            || !profile.IsValidated)
        {
            return new ClaimedJobReadiness(
                ClaimedJobReadinessStatus.ProviderRevisionChanged,
                profile);
        }

        return !_settings.Current.CloudAnalysisEnabled
            ? new ClaimedJobReadiness(ClaimedJobReadinessStatus.CloudAnalysisDisabled, profile)
            : new ClaimedJobReadiness(ClaimedJobReadinessStatus.Ready, profile);
    }

    private Task<AnalysisJobProcessResult> FailForReadinessAsync(
        AnalysisJob current,
        ClaimedJobReadinessStatus readiness,
        CancellationToken cancellationToken)
    {
        return readiness switch
        {
            ClaimedJobReadinessStatus.ProviderRevisionChanged => FailAsync(
                current,
                AnalysisJobErrorCode.ProviderRejected,
                AnalysisFailureDisposition.Terminal,
                _options.RetryDelay,
                cancellationToken),
            ClaimedJobReadinessStatus.CloudAnalysisDisabled => FailAsync(
                current,
                AnalysisJobErrorCode.ProviderUnavailable,
                AnalysisFailureDisposition.Retryable,
                _options.RetryDelay,
                cancellationToken),
            _ => throw new InvalidOperationException(
                "A ready analysis job cannot be failed for configuration readiness."),
        };
    }

    private async Task<AnalysisJob> TransitionAsync(
        AnalysisJob current,
        AnalysisJobState expected,
        AnalysisJobState next,
        CancellationToken cancellationToken)
    {
        var transitioned = await _jobStore
            .TryTransitionAsync(
                current.Lease!,
                expected,
                next,
                GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        return transitioned ?? throw new AnalysisLeaseLostException();
    }

    private async Task<AnalysisJob> EnsureLeaseCoversAsync(
        AnalysisJob current,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken)
    {
        var now = GetUtcNow();
        var requiredExpiry = now.Add(operationTimeout).Add(_options.LeaseSafetyMargin);
        if (current.Lease!.ExpiresAtUtc >= requiredExpiry)
        {
            return current;
        }

        var renewed = await _jobStore
            .TryRenewLeaseAsync(
                current.Lease,
                now,
                requiredExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        return renewed ?? throw new AnalysisLeaseLostException();
    }

    private async Task<AnalysisEvidenceBatch> ExtractWithTimeoutAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ExtractionTimeout);
        return await _evidenceExtractor
            .ExtractAsync(chunk, timeout.Token)
            .ConfigureAwait(false);
    }

    private async Task<AnalysisJobProcessResult> FailAsync(
        AnalysisJob current,
        AnalysisJobErrorCode code,
        AnalysisFailureDisposition disposition,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var failed = await _jobStore
            .TryFailAsync(
                current.Lease!,
                new AnalysisJobFailure(code),
                disposition,
                GetUtcNow(),
                NormalizeRetryDelay(retryDelay),
                cancellationToken)
            .ConfigureAwait(false);
        if (failed is null)
        {
            return new AnalysisJobProcessResult(
                AnalysisJobProcessStatus.LeaseLost,
                current.Id);
        }

        return new AnalysisJobProcessResult(
            failed.State == AnalysisJobState.FailedRetryable
                ? AnalysisJobProcessStatus.FailedRetryable
                : AnalysisJobProcessStatus.FailedTerminal,
            failed.Id,
            failed.Failure?.Code ?? code);
    }

    private static void EnsureProviderMatchesProfile(
        IAiAnalysisProvider provider,
        AiProviderProfileSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var required = AiProviderCapabilities.VisionAnalysis
            | AiProviderCapabilities.StructuredOutput;
        if (provider.Profile != profile.Profile
            || (provider.Capabilities & required) != required)
        {
            throw new AiProviderException(
                AiProviderErrorCode.UnsupportedCapability,
                "The AI provider does not match the claimed profile capabilities.",
                Guid.Empty,
                isRetryable: false);
        }
    }

    private static (AnalysisJobErrorCode Code, AnalysisFailureDisposition Disposition)
        MapEvidenceFailure(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => (
                AnalysisJobErrorCode.EvidenceMissing,
                AnalysisFailureDisposition.Terminal),
            InvalidDataException or ArgumentException or OverflowException => (
                AnalysisJobErrorCode.EvidenceInvalid,
                AnalysisFailureDisposition.Terminal),
            UnauthorizedAccessException => (
                AnalysisJobErrorCode.ExtractionFailed,
                AnalysisFailureDisposition.Terminal),
            TimeoutException => (
                AnalysisJobErrorCode.OperationTimedOut,
                AnalysisFailureDisposition.Retryable),
            IOException => (
                AnalysisJobErrorCode.ExtractionFailed,
                AnalysisFailureDisposition.Retryable),
            _ => (
                AnalysisJobErrorCode.ExtractionFailed,
                AnalysisFailureDisposition.Retryable),
        };
    }

    private static ProviderFailure MapProviderFailure(Exception exception)
    {
        if (exception is not AiProviderException providerFailure)
        {
            return exception is TimeoutException
                ? new ProviderFailure(
                    AnalysisJobErrorCode.OperationTimedOut,
                    AnalysisFailureDisposition.Retryable,
                    RetryDelay: null)
                : new ProviderFailure(
                    exception is IOException
                        ? AnalysisJobErrorCode.ProviderUnavailable
                        : AnalysisJobErrorCode.ProviderRejected,
                    exception is IOException
                        ? AnalysisFailureDisposition.Retryable
                        : AnalysisFailureDisposition.Terminal,
                    RetryDelay: null);
        }

        var code = providerFailure.ErrorCode switch
        {
            AiProviderErrorCode.RateLimited => AnalysisJobErrorCode.ProviderRateLimited,
            AiProviderErrorCode.NetworkUnavailable
                or AiProviderErrorCode.ProviderUnavailable =>
                AnalysisJobErrorCode.ProviderUnavailable,
            AiProviderErrorCode.Timeout => AnalysisJobErrorCode.OperationTimedOut,
            AiProviderErrorCode.InvalidResponse =>
                AnalysisJobErrorCode.ProviderResponseInvalid,
            AiProviderErrorCode.InvalidConfiguration
                or AiProviderErrorCode.AuthenticationFailed
                or AiProviderErrorCode.AccessDenied
                or AiProviderErrorCode.ModelNotFound
                or AiProviderErrorCode.UnsupportedCapability
                or AiProviderErrorCode.RequestRejected
                or AiProviderErrorCode.RequestTooLarge
                or AiProviderErrorCode.ContentRejected =>
                AnalysisJobErrorCode.ProviderRejected,
            _ => AnalysisJobErrorCode.Unknown,
        };
        return new ProviderFailure(
            code,
            providerFailure.IsRetryable
                ? AnalysisFailureDisposition.Retryable
                : AnalysisFailureDisposition.Terminal,
            providerFailure.RetryAfter);
    }

    private static TimeSpan NormalizeRetryDelay(TimeSpan retryDelay)
    {
        var maximum = TimeSpan.FromDays(1);
        return retryDelay > maximum ? maximum : retryDelay;
    }

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private enum ClaimedJobReadinessStatus
    {
        Ready,
        CloudAnalysisDisabled,
        ProviderRevisionChanged,
    }

    private sealed record ClaimedJobReadiness(
        ClaimedJobReadinessStatus Status,
        AiProviderProfileSnapshot? Profile);

    private sealed record ProviderFailure(
        AnalysisJobErrorCode Code,
        AnalysisFailureDisposition Disposition,
        TimeSpan? RetryDelay);

    private sealed class AnalysisLeaseLostException : Exception;
}
