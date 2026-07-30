using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Privacy;
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
    public const string TimelinePromptVersion = "timeline-v5";

    private readonly IAnalysisJobStore _jobStore;
    private readonly ICaptureChunkStore _chunkStore;
    private readonly IAiProviderProfileStore _profileStore;
    private readonly IAiAnalysisProviderFactory _providerFactory;
    private readonly IAnalysisEvidenceExtractor _evidenceExtractor;
    private readonly IAnalysisResultCommitter _resultCommitter;
    private readonly AppSettingsService _settings;
    private readonly AnalysisProviderSendGate _sendGate;
    private readonly AnalysisJobProcessorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAnalysisWindowStore? _windowStore;
    private readonly IAnalysisStageBindingStore? _stageBindingStore;
    private readonly IProviderInvocationStore? _invocationStore;
    private readonly ICaptureChunkFingerprintProvider? _fingerprintProvider;
    private readonly IPrivacyScreeningService? _privacyScreeningService;
    private readonly IEvidenceSendPolicy? _sendPolicy;

    public AnalysisJobProcessor(
        IAnalysisJobStore jobStore,
        ICaptureChunkStore chunkStore,
        IAiProviderProfileStore profileStore,
        IAiAnalysisProviderFactory providerFactory,
        IAnalysisEvidenceExtractor evidenceExtractor,
        IAnalysisResultCommitter resultCommitter,
        AppSettingsService settings,
        AnalysisJobProcessorOptions options,
        TimeProvider? timeProvider = null,
        IAnalysisWindowStore? windowStore = null,
        IAnalysisStageBindingStore? stageBindingStore = null,
        IProviderInvocationStore? invocationStore = null,
        ICaptureChunkFingerprintProvider? fingerprintProvider = null,
        IPrivacyScreeningService? privacyScreeningService = null,
        IEvidenceSendPolicy? sendPolicy = null,
        AnalysisProviderSendGate? sendGate = null)
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
        _sendGate = sendGate ?? new AnalysisProviderSendGate();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowStore = windowStore ?? jobStore as IAnalysisWindowStore;
        _stageBindingStore = stageBindingStore;
        _invocationStore = invocationStore;
        _fingerprintProvider = fingerprintProvider;
        _privacyScreeningService = privacyScreeningService;
        _sendPolicy = sendPolicy;
    }

    public async Task<AnalysisJobProcessResult> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = await GetRunnableRouteBeforeClaimAsync(cancellationToken)
            .ConfigureAwait(false);
        if (route is null)
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

            route = readiness.Route!;
            var profile = route.Profile;
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

            var window = _windowStore is null
                ? new AnalysisWindowSnapshot(
                    chunk.Range,
                    [new AnalysisWindowMember(
                        chunk,
                        new CaptureChunkFingerprint(current.InputFingerprint),
                        chunk.Range)],
                    [])
                : await _windowStore
                    .GetWindowAsync(current.Id, cancellationToken)
                    .ConfigureAwait(false);
            if (window is null
                || !window.Members.Any(member => string.Equals(
                    member.Chunk.Id,
                    current.CaptureChunkId,
                    StringComparison.Ordinal)))
            {
                return await FailAsync(
                        current,
                        AnalysisJobErrorCode.EvidenceInvalid,
                        AnalysisFailureDisposition.Terminal,
                        _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_stageBindingStore is not null)
            {
                if (!await WindowInputStillMatchesAsync(
                        window,
                        current,
                        route,
                        cancellationToken).ConfigureAwait(false))
                {
                    return await FailAsync(
                            current,
                            AnalysisJobErrorCode.ProviderRejected,
                            AnalysisFailureDisposition.Terminal,
                            _options.RetryDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            current = await TransitionAsync(
                    current,
                    AnalysisJobState.Claimed,
                    AnalysisJobState.Extracting,
                    cancellationToken)
                .ConfigureAwait(false);
            var evidenceBatches = new List<AnalysisEvidenceBatch>(window.Members.Count);
            try
            {
                foreach (var member in window.Members)
                {
                    current = await EnsureLeaseCoversAsync(
                            current,
                            _options.ExtractionTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var evidence = await ExtractWithTimeoutAsync(
                            member.Chunk,
                            member.SourceFingerprint,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (evidence is null
                        || evidence.SourceFingerprint != member.SourceFingerprint)
                    {
                        throw new InvalidDataException(
                            "Extracted evidence did not match its persisted window member.");
                    }

                    evidenceBatches.Add(evidence);
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
            catch (AnalysisLeaseLostException)
            {
                throw;
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
                var aggregate = BuildAggregateEvidence(window, evidenceBatches);
                request = new AiAnalysisRequest(
                    Guid.NewGuid(),
                    current.Id,
                    current.Attempt,
                    aggregate.References,
                    window.Range,
                    TimelinePromptVersion,
                    AiAnalysisContract.CurrentSchemaVersion,
                    _options.Locale,
                    aggregate.Images,
                    aggregate.Context,
                    window.ExistingEntries.Select(entry => new AiPriorTimelineEntry(
                        entry.Id,
                        entry.Range,
                        entry.Title,
                        entry.Summary,
                        entry.IsRewriteProtectedBy(window.Range))).ToArray());
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

            route = readiness.Route!;
            profile = route.Profile;
            current = await EnsureLeaseCoversAsync(
                    current,
                    profile.Profile.RequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            AiAnalysisResponse? response = null;
            ClaimedJobReadiness? sendBlockedReadiness = null;
            var sendBlockedByRule = false;
            Guid? invocationId = null;
            try
            {
                var provider = await _providerFactory
                    .CreateAsync(profile, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    Task<AiAnalysisResponse>? analysisTask = null;
                    using (await _sendGate
                               .EnterAsync(cancellationToken)
                               .ConfigureAwait(false))
                    {
                        var sendReadiness = await CheckClaimedJobReadinessAsync(
                                current,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (sendReadiness.Status == ClaimedJobReadinessStatus.Ready)
                        {
                            var sendRoute = sendReadiness.Route!;
                            EnsureProviderMatchesProfile(provider, sendRoute.Profile);
                            if (_stageBindingStore is not null
                                && !await WindowInputStillMatchesAsync(
                                    window,
                                    current,
                                    sendRoute,
                                    cancellationToken).ConfigureAwait(false))
                            {
                                sendBlockedReadiness = new ClaimedJobReadiness(
                                    ClaimedJobReadinessStatus.ProviderRevisionChanged,
                                    sendRoute);
                            }
                            else if (!await TimelineSendAllowedAsync(
                                    window,
                                    current,
                                    sendRoute,
                                    cancellationToken).ConfigureAwait(false))
                            {
                                sendBlockedByRule = true;
                            }
                            else
                            {
                                if (_invocationStore is not null)
                                {
                                    invocationId = Guid.NewGuid();
                                    await _invocationStore.StartAsync(
                                            new ProviderInvocationStart(
                                                invocationId.Value,
                                                AnalysisStage.TimelineAnalysis,
                                                sendRoute.Profile.Profile.Id,
                                                sendRoute.Profile.Revision,
                                                sendRoute.Binding.RouteRevision,
                                                sendRoute.Profile.Profile.BaseEndpoint
                                                    .GetLeftPart(UriPartial.Authority),
                                                current.InputFingerprint,
                                                request.Images.Count,
                                                request.Images.Sum(static image =>
                                                    (long)image.JpegBytes.Length),
                                                GetUtcNow(),
                                                request.CorrelationId),
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                analysisTask = provider.AnalyzeAsync(request, cancellationToken)
                                    ?? throw new AiProviderException(
                                        AiProviderErrorCode.InvalidResponse,
                                        "The AI provider returned no analysis task.",
                                        Guid.Empty,
                                        isRetryable: false);
                            }
                        }
                        else
                        {
                            sendBlockedReadiness = sendReadiness;
                        }
                    }

                    if (analysisTask is not null)
                    {
                        response = await analysisTask.ConfigureAwait(false)
                            ?? throw new AiProviderException(
                                AiProviderErrorCode.InvalidResponse,
                                "The AI provider returned no analysis response.",
                                Guid.Empty,
                                isRetryable: false);
                        await CompleteInvocationAsync(
                                invocationId,
                                ProviderInvocationOutcome.Succeeded,
                                response.TokenUsage is { } usage
                                    ? new ProviderInvocationUsage(
                                        usage.PromptTokens,
                                        usage.CompletionTokens)
                                    : null)
                            .ConfigureAwait(false);
                        invocationId = null;
                    }
                }
                finally
                {
                    (provider as IDisposable)?.Dispose();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CompleteInvocationAsync(
                        invocationId,
                        ProviderInvocationOutcome.Cancelled,
                        usage: null)
                    .ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await CompleteInvocationAsync(
                        invocationId,
                        ProviderInvocationOutcome.FailedRetryable,
                        usage: null)
                    .ConfigureAwait(false);
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
                await CompleteInvocationAsync(
                        invocationId,
                        mapped.Disposition == AnalysisFailureDisposition.Retryable
                            ? ProviderInvocationOutcome.FailedRetryable
                            : ProviderInvocationOutcome.FailedTerminal,
                        usage: null)
                    .ConfigureAwait(false);
                return await FailAsync(
                        current,
                        mapped.Code,
                        mapped.Disposition,
                        mapped.RetryDelay ?? _options.RetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (sendBlockedReadiness is not null)
            {
                return await FailForReadinessAsync(
                        current,
                        sendBlockedReadiness.Status,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (sendBlockedByRule)
            {
                return await FailAsync(
                        current,
                        AnalysisJobErrorCode.EvidenceSendBlocked,
                        AnalysisFailureDisposition.Retryable,
                        _options.RetryDelay,
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
                var activities = AiAnalysisResponseValidator.Validate(request, response!);
                entries = activities
                    .SelectMany(activity => SplitAroundLockedEntries(
                        activity,
                        window.Range,
                        window.ExistingEntries))
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
                commitStatus = _resultCommitter is IAnalysisStageAwareWindowResultCommitter stageAwareWindowCommitter
                    ? await stageAwareWindowCommitter
                        .TryCommitWindowAsync(
                            current.Lease!,
                            current.ProviderProfileId,
                            current.ProviderProfileRevision,
                            readiness.Route!.Binding.RouteRevision,
                            window,
                            entries,
                            GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false)
                    : _resultCommitter is IAnalysisStageAwareResultCommitter stageAwareCommitter
                        ? await stageAwareCommitter
                            .TryCommitAsync(
                                current.Lease!,
                                current.ProviderProfileId,
                                current.ProviderProfileRevision,
                                readiness.Route!.Binding.RouteRevision,
                                entries,
                                GetUtcNow(),
                                cancellationToken)
                            .ConfigureAwait(false)
                    : _resultCommitter is IAnalysisWindowResultCommitter windowCommitter
                    ? await windowCommitter
                        .TryCommitWindowAsync(
                            current.Lease!,
                            current.ProviderProfileId,
                            current.ProviderProfileRevision,
                            window,
                            entries,
                            GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await _resultCommitter
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
                AnalysisResultCommitStatus.WindowChanged => await FailAsync(
                        current,
                        AnalysisJobErrorCode.PersistenceFailure,
                        AnalysisFailureDisposition.Retryable,
                        TimeSpan.Zero,
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

    private static AggregateEvidence BuildAggregateEvidence(
        AnalysisWindowSnapshot window,
        List<AnalysisEvidenceBatch> batches)
    {
        if (batches.Count != window.Members.Count)
        {
            throw new ArgumentException(
                "Every analysis window member requires one extracted evidence batch.",
                nameof(batches));
        }

        var references = new List<EvidenceReference>(batches.Count);
        var images = new List<AiEvidenceImage>();
        var context = new List<AiAnalysisContextSlice>();
        for (var memberIndex = 0; memberIndex < window.Members.Count; memberIndex++)
        {
            var member = window.Members[memberIndex];
            var batch = batches[memberIndex];
            references.Add(new EvidenceReference(
                member.Chunk.Id,
                batch.ArtifactPath,
                member.ContributionRange));
            foreach (var image in batch.Images)
            {
                if (member.ContributionRange.Contains(image.CapturedAt))
                {
                    images.Add(new AiEvidenceImage(
                        window.Members.Count == 1
                            ? image.FrameId
                            : $"m{memberIndex:D3}-{image.FrameId}",
                        image.CapturedAt,
                        image.JpegBytes));
                }
            }

            foreach (var slice in batch.Context)
            {
                var start = slice.Range.Start > member.ContributionRange.Start
                    ? slice.Range.Start
                    : member.ContributionRange.Start;
                var end = slice.Range.End < member.ContributionRange.End
                    ? slice.Range.End
                    : member.ContributionRange.End;
                if (end > start)
                {
                    context.Add(new AiAnalysisContextSlice(
                        new TimeRange(start, end),
                        slice.ApplicationId,
                        slice.ApplicationDisplayName));
                }
            }
        }

        var selectedImages = SelectImagesWithinBudget(images);
        var normalizedContext = NormalizeContext(context);
        return new AggregateEvidence(references, selectedImages, normalizedContext);
    }

    private async Task<bool> WindowInputStillMatchesAsync(
        AnalysisWindowSnapshot window,
        AnalysisJob current,
        RunnableTimelineRoute route,
        CancellationToken cancellationToken)
    {
        CaptureChunkFingerprint evidenceFingerprint;
        if (_privacyScreeningService is null || _fingerprintProvider is null)
        {
            evidenceFingerprint = CaptureAnalysisIngestionService
                .ComputeWindowFingerprint(window.Members);
        }
        else
        {
            var selections = new Dictionary<string, PrivacyEvidenceSelection>(
                window.Members.Count,
                StringComparer.Ordinal);
            foreach (var member in window.Members)
            {
                var original = await _fingerprintProvider
                    .ComputeAsync(member.Chunk, cancellationToken)
                    .ConfigureAwait(false);
                var selection = await _privacyScreeningService.PrepareAsync(
                        member.Chunk,
                        original,
                        CaptureAnalysisIngestionService.CreatePrivacyOperationId(
                            member.Chunk,
                            original),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!selection.IsReady
                    || selection.Fingerprint != member.SourceFingerprint)
                {
                    return false;
                }
                selections.Add(member.Chunk.Id, selection);
            }
            evidenceFingerprint = CaptureAnalysisIngestionService
                .ComputeWindowFingerprint(window.Members, selections);
        }

        var expectedInput = CaptureAnalysisIngestionService.BindRouteFingerprint(
            evidenceFingerprint,
            route.Profile,
            route.Binding);
        return string.Equals(
            expectedInput.Value,
            current.InputFingerprint,
            StringComparison.Ordinal);
    }

    private async Task<bool> TimelineSendAllowedAsync(
        AnalysisWindowSnapshot window,
        AnalysisJob current,
        RunnableTimelineRoute route,
        CancellationToken cancellationToken)
    {
        if (_sendPolicy is null)
        {
            return true;
        }

        foreach (var member in window.Members)
        {
            var decision = await _sendPolicy.EvaluateAsync(
                    member.Chunk,
                    AnalysisStage.TimelineAnalysis,
                    route.Profile,
                    route.Binding,
                    member.SourceFingerprint,
                    current.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return false;
            }
        }
        return true;
    }

    private static List<AiEvidenceImage> SelectImagesWithinBudget(
        List<AiEvidenceImage> images)
    {
        if (images.Count == 0)
        {
            return [];
        }

        var ordered = images
            .OrderBy(static image => image.CapturedAt)
            .ThenBy(static image => image.FrameId, StringComparer.Ordinal)
            .ToArray();
        var deduplicated = new List<AiEvidenceImage>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var image = ordered[index];
            var isFinal = index == ordered.Length - 1;
            if (!isFinal
                && deduplicated.Count != 0
                && image.JpegBytes.Span.SequenceEqual(
                    deduplicated[^1].JpegBytes.Span))
            {
                continue;
            }

            deduplicated.Add(image);
        }

        var targetCount = Math.Min(deduplicated.Count, AiAnalysisContract.MaximumImages);
        var candidateIndices = new List<int>(targetCount);
        for (var ordinal = 0; ordinal < targetCount; ordinal++)
        {
            candidateIndices.Add(targetCount == 1
                ? 0
                : checked((int)((long)ordinal * (deduplicated.Count - 1) / (targetCount - 1))));
        }

        var selected = new List<AiEvidenceImage>(targetCount);
        var totalBytes = 0L;
        foreach (var index in candidateIndices.Distinct().Order())
        {
            var image = deduplicated[index];
            if (totalBytes + image.JpegBytes.Length
                > AiAnalysisContract.MaximumRequestImageBytes)
            {
                continue;
            }

            selected.Add(image);
            totalBytes += image.JpegBytes.Length;
        }

        if (selected.Count == 0)
        {
            selected.Add(deduplicated.MinBy(static image => image.JpegBytes.Length)!);
        }

        return selected;
    }

    private static AiAnalysisContextSlice[] NormalizeContext(
        IReadOnlyList<AiAnalysisContextSlice> context)
    {
        var normalized = new List<AiAnalysisContextSlice>();
        foreach (var group in context
                     .GroupBy(static slice => slice.ApplicationId, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            DateTimeOffset? previousEnd = null;
            foreach (var slice in group.OrderBy(static slice => slice.Range.Start))
            {
                var start = previousEnd > slice.Range.Start
                    ? previousEnd.Value
                    : slice.Range.Start;
                if (slice.Range.End <= start)
                {
                    continue;
                }

                normalized.Add(new AiAnalysisContextSlice(
                    new TimeRange(start, slice.Range.End),
                    slice.ApplicationId,
                    slice.ApplicationDisplayName));
                previousEnd = slice.Range.End;
            }
        }

        return normalized
            .OrderBy(static slice => slice.Range.Start)
            .ThenBy(static slice => slice.ApplicationId, StringComparer.Ordinal)
            .Take(AiAnalysisContract.MaximumContextSlices)
            .ToArray();
    }

    private static Activity[] SplitAroundLockedEntries(
        Activity activity,
        TimeRange window,
        IReadOnlyList<AnalysisWindowExistingEntry> existingEntries)
    {
        var segments = new List<TimeRange> { activity.Range };
        foreach (var locked in existingEntries
                     .Where(entry => entry.IsRewriteProtectedBy(window))
                     .OrderBy(static entry => entry.Range.Start))
        {
            var next = new List<TimeRange>();
            foreach (var segment in segments)
            {
                if (locked.Range.End <= segment.Start || locked.Range.Start >= segment.End)
                {
                    next.Add(segment);
                    continue;
                }

                if (locked.Range.Start > segment.Start)
                {
                    next.Add(new TimeRange(
                        segment.Start,
                        locked.Range.Start < segment.End ? locked.Range.Start : segment.End));
                }

                if (locked.Range.End < segment.End)
                {
                    next.Add(new TimeRange(
                        locked.Range.End > segment.Start ? locked.Range.End : segment.Start,
                        segment.End));
                }
            }

            segments = next;
        }

        return segments.Select(segment =>
        {
            var evidence = activity.EvidenceReferences
                .Where(reference => reference.ContributionRange is not { } contribution
                    || contribution.Start < segment.End && contribution.End > segment.Start)
                .ToArray();
            if (evidence.Length == 0)
            {
                evidence = activity.EvidenceReferences.ToArray();
            }

            return new Activity(
                segment,
                activity.Title,
                activity.Summary,
                activity.Category,
                activity.Productivity,
                activity.Apps,
                activity.Tags,
                activity.Confidence,
                evidence);
        }).ToArray();
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

    private sealed record AggregateEvidence(
        IReadOnlyList<EvidenceReference> References,
        IReadOnlyList<AiEvidenceImage> Images,
        IReadOnlyList<AiAnalysisContextSlice> Context);

    private async Task<RunnableTimelineRoute?> GetRunnableRouteBeforeClaimAsync(
        CancellationToken cancellationToken)
    {
        if (_stageBindingStore is null)
        {
            return null;
        }

        var binding = await _stageBindingStore
            .GetAsync(AnalysisStage.TimelineAnalysis, cancellationToken)
            .ConfigureAwait(false);
        if (!binding.Enabled || !binding.ProviderProfileId.HasValue)
        {
            return null;
        }
        var profile = await _profileStore
            .GetAsync(binding.ProviderProfileId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (profile is not { IsComplete: true })
        {
            return null;
        }
        var validation = await _stageBindingStore.GetValidationAsync(
                profile.Profile.Id,
                profile.Revision,
                AnalysisStage.TimelineAnalysis,
                cancellationToken)
            .ConfigureAwait(false);
        return validation is null ? null : new RunnableTimelineRoute(profile, binding);
    }

    private async Task<ClaimedJobReadiness> CheckClaimedJobReadinessAsync(
        AnalysisJob job,
        CancellationToken cancellationToken)
    {
        var route = await GetRunnableRouteBeforeClaimAsync(cancellationToken)
            .ConfigureAwait(false);
        if (route is null
            || route.Profile.Profile.Id != job.ProviderProfileId
            || route.Profile.Revision != job.ProviderProfileRevision)
        {
            return new ClaimedJobReadiness(
                ClaimedJobReadinessStatus.ProviderRevisionChanged,
                route);
        }

        return new ClaimedJobReadiness(ClaimedJobReadinessStatus.Ready, route);
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
        CaptureChunkFingerprint expectedSourceFingerprint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ExtractionTimeout);
        return await _evidenceExtractor
            .ExtractAsync(chunk, expectedSourceFingerprint, timeout.Token)
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
            AnalysisEvidenceExtractionException extractionFailure =>
                MapExtractionFailure(extractionFailure.FailureKind),
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

    private static (AnalysisJobErrorCode Code, AnalysisFailureDisposition Disposition)
        MapExtractionFailure(AnalysisEvidenceExtractionFailureKind failureKind)
    {
        return failureKind switch
        {
            AnalysisEvidenceExtractionFailureKind.EvidenceNotFound => (
                AnalysisJobErrorCode.EvidenceMissing,
                AnalysisFailureDisposition.Terminal),
            AnalysisEvidenceExtractionFailureKind.IoFailure
                or AnalysisEvidenceExtractionFailureKind.CryptoFailure => (
                    AnalysisJobErrorCode.ExtractionFailed,
                    AnalysisFailureDisposition.Retryable),
            AnalysisEvidenceExtractionFailureKind.UnsafeEvidence
                or AnalysisEvidenceExtractionFailureKind.EvidenceTooLarge
                or AnalysisEvidenceExtractionFailureKind.EvidenceChanged
                or AnalysisEvidenceExtractionFailureKind.InvalidEvidence
                or AnalysisEvidenceExtractionFailureKind.DecoderFailure
                or AnalysisEvidenceExtractionFailureKind.EvidenceConflict
                or AnalysisEvidenceExtractionFailureKind.NativeContractFailure => (
                    AnalysisJobErrorCode.EvidenceInvalid,
                    AnalysisFailureDisposition.Terminal),
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

    private async Task CompleteInvocationAsync(
        Guid? invocationId,
        ProviderInvocationOutcome outcome,
        ProviderInvocationUsage? usage)
    {
        if (!invocationId.HasValue || _invocationStore is null)
        {
            return;
        }
        await _invocationStore.CompleteAsync(
                invocationId.Value,
                outcome,
                usage,
                GetUtcNow(),
                CancellationToken.None)
            .ConfigureAwait(false);
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
        RunnableTimelineRoute? Route);

    private sealed record RunnableTimelineRoute(
        AiProviderProfileSnapshot Profile,
        AnalysisStageBinding Binding);

    private sealed record ProviderFailure(
        AnalysisJobErrorCode Code,
        AnalysisFailureDisposition Disposition,
        TimeSpan? RetryDelay);

    private sealed class AnalysisLeaseLostException : Exception;
}
