using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Privacy;

public sealed class PrivacyScreeningService : IPrivacyScreeningService, IDisposable
{
    private const int ProviderFailureCode = 1;
    private const int EvidenceFailureCode = 2;
    private const int RedactionFailureCode = 3;

    private readonly IAnalysisStageBindingStore _bindingStore;
    private readonly IAiProviderProfileStore _profileStore;
    private readonly IAiAnalysisProviderFactory _providerFactory;
    private readonly IAnalysisEvidenceExtractor _evidenceExtractor;
    private readonly IEvidenceSendPolicy _sendPolicy;
    private readonly IPrivacyScreeningStore _screeningStore;
    private readonly IPrivacyEvidenceRedactor _redactor;
    private readonly IProviderInvocationStore _invocationStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public PrivacyScreeningService(
        IAnalysisStageBindingStore bindingStore,
        IAiProviderProfileStore profileStore,
        IAiAnalysisProviderFactory providerFactory,
        IAnalysisEvidenceExtractor evidenceExtractor,
        IEvidenceSendPolicy sendPolicy,
        IPrivacyScreeningStore screeningStore,
        IPrivacyEvidenceRedactor redactor,
        IProviderInvocationStore invocationStore,
        TimeProvider? timeProvider = null)
    {
        _bindingStore = bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _evidenceExtractor = evidenceExtractor ?? throw new ArgumentNullException(nameof(evidenceExtractor));
        _sendPolicy = sendPolicy ?? throw new ArgumentNullException(nameof(sendPolicy));
        _screeningStore = screeningStore ?? throw new ArgumentNullException(nameof(screeningStore));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _invocationStore = invocationStore ?? throw new ArgumentNullException(nameof(invocationStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PrivacyEvidenceSelection> PrepareAsync(
        CaptureChunk chunk,
        CaptureChunkFingerprint originalFingerprint,
        Guid logicalOperationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(originalFingerprint);
        if (logicalOperationId == Guid.Empty)
        {
            throw new ArgumentException("A privacy operation identifier is required.", nameof(logicalOperationId));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var route = await _bindingStore
                .GetAsync(AnalysisStage.PrivacyInspection, cancellationToken)
                .ConfigureAwait(false);
            if (!route.Enabled)
            {
                return Original(originalFingerprint, chunk.ManifestPath, route);
            }

            if (!route.ProviderProfileId.HasValue)
            {
                return NotReady(route);
            }

            var profile = await _profileStore
                .GetAsync(route.ProviderProfileId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (profile is not { IsComplete: true })
            {
                return NotReady(route);
            }

            var validation = await _bindingStore.GetValidationAsync(
                    profile.Profile.Id,
                    profile.Revision,
                    AnalysisStage.PrivacyInspection,
                    cancellationToken)
                .ConfigureAwait(false);
            if (validation is null)
            {
                return NotReady(route, profile);
            }

            if (chunk.FrameCount == 0)
            {
                return Original(originalFingerprint, chunk.ManifestPath, route, profile);
            }

            var cached = await _screeningStore.GetAsync(
                    chunk.Id,
                    profile.Profile.Id,
                    profile.Revision,
                    route.RouteRevision,
                    originalFingerprint.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null && IsTerminal(cached.State))
            {
                return Select(cached, chunk.ManifestPath);
            }

            AnalysisEvidenceBatch evidence;
            try
            {
                evidence = await _evidenceExtractor
                    .ExtractAsync(chunk, originalFingerprint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return await SaveFailureAsync(
                        cached,
                        chunk,
                        originalFingerprint,
                        profile,
                        route,
                        EvidenceFailureCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (evidence.Images.Count == 0)
            {
                return Original(originalFingerprint, chunk.ManifestPath, route, profile);
            }

            var sendDecision = await _sendPolicy.EvaluateAsync(
                    chunk,
                    AnalysisStage.PrivacyInspection,
                    profile,
                    route,
                    originalFingerprint,
                    logicalOperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!sendDecision.IsAllowed)
            {
                return new PrivacyEvidenceSelection(
                    PrivacyEvidenceStatus.BlockedByRule,
                    Fingerprint: null,
                    ManifestPath: null,
                    ScreeningId: null,
                    ScreeningRevision: null,
                    profile.Profile.Id,
                    profile.Revision,
                    route.RouteRevision);
            }

            var now = UtcNow();
            var inspecting = cached is null
                ? new PrivacyScreeningSnapshot(
                    Guid.NewGuid(),
                    chunk.Id,
                    profile.Profile.Id,
                    profile.Revision,
                    route.RouteRevision,
                    originalFingerprint.Value,
                    PrivacyScreeningState.Inspecting,
                    Verdict: null,
                    Result: null,
                    DerivativeManifestPath: null,
                    OutputFingerprint: null,
                    Attempt: 1,
                    ErrorCode: null,
                    Revision: 1,
                    now,
                    now)
                : cached with
                {
                    State = PrivacyScreeningState.Inspecting,
                    Attempt = cached.Attempt + 1,
                    ErrorCode = null,
                    Revision = cached.Revision + 1,
                    UpdatedAtUtc = now,
                };
            inspecting = await _screeningStore.SaveAsync(inspecting, cancellationToken)
                .ConfigureAwait(false);

            PrivacyInspectionResponse response;
            Guid? invocationId = null;
            try
            {
                var provider = await _providerFactory.CreateAsync(profile, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    if (provider is not IPrivacyInspectionProvider privacyProvider)
                    {
                        throw new InvalidOperationException(
                            "The selected provider does not support privacy inspection.");
                    }

                    var request = new PrivacyInspectionRequest(
                        Guid.NewGuid(),
                        chunk.Id,
                        originalFingerprint.Value,
                        evidence.Images);
                    invocationId = Guid.NewGuid();
                    await _invocationStore.StartAsync(
                            new ProviderInvocationStart(
                                invocationId.Value,
                                AnalysisStage.PrivacyInspection,
                                profile.Profile.Id,
                                profile.Revision,
                                route.RouteRevision,
                                profile.Profile.BaseEndpoint.GetLeftPart(UriPartial.Authority),
                                originalFingerprint.Value,
                                evidence.Images.Count,
                                evidence.Images.Sum(static image => (long)image.JpegBytes.Length),
                                UtcNow(),
                                request.CorrelationId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    response = await privacyProvider.InspectPrivacyAsync(request, cancellationToken)
                        .ConfigureAwait(false);
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
            catch (Exception exception)
            {
                await CompleteInvocationAsync(
                        invocationId,
                        IsRetryable(exception)
                            ? ProviderInvocationOutcome.FailedRetryable
                            : ProviderInvocationOutcome.FailedTerminal,
                        usage: null)
                    .ConfigureAwait(false);
                return await SaveFailureAsync(
                        inspecting,
                        chunk,
                        originalFingerprint,
                        profile,
                        route,
                        ProviderFailureCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await ApplyResultAsync(
                    inspecting,
                    chunk,
                    originalFingerprint,
                    route,
                    response.Result,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    private async Task<PrivacyEvidenceSelection> ApplyResultAsync(
        PrivacyScreeningSnapshot current,
        CaptureChunk chunk,
        CaptureChunkFingerprint originalFingerprint,
        AnalysisStageBinding route,
        PrivacyScreeningResult result,
        CancellationToken cancellationToken)
    {
        if (result.Verdict == PrivacyScreeningVerdict.Clear)
        {
            return await SaveFinalAsync(
                    current,
                    PrivacyScreeningState.Clear,
                    result,
                    chunk.ManifestPath,
                    originalFingerprint,
                    errorCode: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (result.Verdict == PrivacyScreeningVerdict.Inconclusive)
        {
            return await SavePolicyResultAsync(
                    current,
                    chunk,
                    originalFingerprint,
                    result,
                    route.PrivacyOptions!.OnError,
                    ProviderFailureCode,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return route.PrivacyOptions!.OnMatch switch
        {
            PrivacyMatchAction.AuditOnly => await SaveFinalAsync(
                current,
                PrivacyScreeningState.Clear,
                result,
                chunk.ManifestPath,
                originalFingerprint,
                errorCode: null,
                cancellationToken).ConfigureAwait(false),
            PrivacyMatchAction.Hold => await SaveFinalAsync(
                current,
                PrivacyScreeningState.Held,
                result,
                manifestPath: null,
                fingerprint: null,
                errorCode: null,
                cancellationToken).ConfigureAwait(false),
            PrivacyMatchAction.RequireReview => await SaveFinalAsync(
                current,
                PrivacyScreeningState.NeedsReview,
                result,
                manifestPath: null,
                fingerprint: null,
                errorCode: null,
                cancellationToken).ConfigureAwait(false),
            PrivacyMatchAction.RedactAndContinue => await RedactAsync(
                current,
                chunk,
                originalFingerprint,
                result,
                route,
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The privacy match action is unsupported."),
        };
    }

    private async Task<PrivacyEvidenceSelection> RedactAsync(
        PrivacyScreeningSnapshot current,
        CaptureChunk chunk,
        CaptureChunkFingerprint originalFingerprint,
        PrivacyScreeningResult result,
        AnalysisStageBinding route,
        CancellationToken cancellationToken)
    {
        if (result.Findings.Count == 0)
        {
            return await SavePolicyResultAsync(
                    current,
                    chunk,
                    originalFingerprint,
                    result,
                    route.PrivacyOptions!.OnError == PrivacyFailureAction.RequireReview
                        ? PrivacyFailureAction.RequireReview
                        : PrivacyFailureAction.Hold,
                    RedactionFailureCode,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var derivative = await _redactor.RedactAsync(
                    current.Id,
                    chunk,
                    originalFingerprint,
                    result.Findings,
                    cancellationToken)
                .ConfigureAwait(false);
            return await SaveFinalAsync(
                    current,
                    PrivacyScreeningState.Redacted,
                    result,
                    derivative.ManifestPath,
                    derivative.Fingerprint,
                    errorCode: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await SavePolicyResultAsync(
                    current,
                    chunk,
                    originalFingerprint,
                    result,
                    route.PrivacyOptions!.OnError,
                    RedactionFailureCode,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task<PrivacyEvidenceSelection> SaveFailureAsync(
        PrivacyScreeningSnapshot? current,
        CaptureChunk chunk,
        CaptureChunkFingerprint originalFingerprint,
        AiProviderProfileSnapshot profile,
        AnalysisStageBinding route,
        int errorCode,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var snapshot = current ?? new PrivacyScreeningSnapshot(
            Guid.NewGuid(),
            chunk.Id,
            profile.Profile.Id,
            profile.Revision,
            route.RouteRevision,
            originalFingerprint.Value,
            PrivacyScreeningState.Pending,
            Verdict: null,
            Result: null,
            DerivativeManifestPath: null,
            OutputFingerprint: null,
            Attempt: 0,
            ErrorCode: null,
            Revision: 1,
            now,
            now);
        return SavePolicyResultAsync(
            snapshot,
            chunk,
            originalFingerprint,
            result: null,
            route.PrivacyOptions!.OnError,
            errorCode,
            cancellationToken,
            isNew: current is null);
    }

    private async Task<PrivacyEvidenceSelection> SavePolicyResultAsync(
        PrivacyScreeningSnapshot current,
        CaptureChunk chunk,
        CaptureChunkFingerprint originalFingerprint,
        PrivacyScreeningResult? result,
        PrivacyFailureAction action,
        int errorCode,
        CancellationToken cancellationToken,
        bool isNew = false)
    {
        return action switch
        {
            PrivacyFailureAction.PassThrough => await SaveFinalAsync(
                current,
                PrivacyScreeningState.Clear,
                result,
                chunk.ManifestPath,
                originalFingerprint,
                errorCode,
                cancellationToken,
                isNew).ConfigureAwait(false),
            PrivacyFailureAction.RequireReview => await SaveFinalAsync(
                current,
                PrivacyScreeningState.NeedsReview,
                result,
                manifestPath: null,
                fingerprint: null,
                errorCode,
                cancellationToken,
                isNew).ConfigureAwait(false),
            PrivacyFailureAction.Hold => await SaveFinalAsync(
                current,
                PrivacyScreeningState.Held,
                result,
                manifestPath: null,
                fingerprint: null,
                errorCode,
                cancellationToken,
                isNew).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The privacy failure action is unsupported."),
        };
    }

    private async Task<PrivacyEvidenceSelection> SaveFinalAsync(
        PrivacyScreeningSnapshot current,
        PrivacyScreeningState state,
        PrivacyScreeningResult? result,
        EvidenceRelativePath? manifestPath,
        CaptureChunkFingerprint? fingerprint,
        int? errorCode,
        CancellationToken cancellationToken,
        bool isNew = false)
    {
        var updated = current with
        {
            State = state,
            Verdict = result?.Verdict,
            Result = result,
            DerivativeManifestPath = state == PrivacyScreeningState.Redacted
                ? manifestPath
                : null,
            OutputFingerprint = fingerprint?.Value,
            ErrorCode = errorCode,
            Revision = isNew ? current.Revision : current.Revision + 1,
            UpdatedAtUtc = UtcNow(),
        };
        updated = await _screeningStore.SaveAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        return Select(updated, manifestPath);
    }

    private async Task CompleteInvocationAsync(
        Guid? invocationId,
        ProviderInvocationOutcome outcome,
        ProviderInvocationUsage? usage)
    {
        if (!invocationId.HasValue)
        {
            return;
        }

        try
        {
            await _invocationStore.CompleteAsync(
                    invocationId.Value,
                    outcome,
                    usage,
                    UtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private static PrivacyEvidenceSelection Original(
        CaptureChunkFingerprint fingerprint,
        EvidenceRelativePath manifestPath,
        AnalysisStageBinding route,
        AiProviderProfileSnapshot? profile = null) => new(
            PrivacyEvidenceStatus.ReadyOriginal,
            fingerprint,
            manifestPath,
            ScreeningId: null,
            ScreeningRevision: null,
            profile?.Profile.Id,
            profile?.Revision,
            route.RouteRevision);

    private static PrivacyEvidenceSelection NotReady(
        AnalysisStageBinding route,
        AiProviderProfileSnapshot? profile = null) => new(
            PrivacyEvidenceStatus.NotReady,
            Fingerprint: null,
            ManifestPath: null,
            ScreeningId: null,
            ScreeningRevision: null,
            profile?.Profile.Id,
            profile?.Revision,
            route.RouteRevision);

    private static PrivacyEvidenceSelection Select(
        PrivacyScreeningSnapshot value,
        EvidenceRelativePath? originalManifestPath) => value.State switch
        {
            PrivacyScreeningState.Clear => new PrivacyEvidenceSelection(
                PrivacyEvidenceStatus.ReadyOriginal,
                new CaptureChunkFingerprint(value.OutputFingerprint!),
                originalManifestPath,
                value.Id,
                value.Revision,
                value.ProviderProfileId,
                value.ProviderProfileRevision,
                value.RouteRevision),
            PrivacyScreeningState.Redacted => new PrivacyEvidenceSelection(
                PrivacyEvidenceStatus.ReadyRedacted,
                new CaptureChunkFingerprint(value.OutputFingerprint!),
                value.DerivativeManifestPath,
                value.Id,
                value.Revision,
                value.ProviderProfileId,
                value.ProviderProfileRevision,
                value.RouteRevision),
            PrivacyScreeningState.Held => Blocked(
                PrivacyEvidenceStatus.Held,
                value),
            PrivacyScreeningState.NeedsReview => Blocked(
                PrivacyEvidenceStatus.NeedsReview,
                value),
            _ => Blocked(PrivacyEvidenceStatus.NotReady, value),
        };

    private static PrivacyEvidenceSelection Blocked(
        PrivacyEvidenceStatus status,
        PrivacyScreeningSnapshot value) => new(
            status,
            Fingerprint: null,
            ManifestPath: null,
            value.Id,
            value.Revision,
            value.ProviderProfileId,
            value.ProviderProfileRevision,
            value.RouteRevision);

    private static bool IsTerminal(PrivacyScreeningState state) => state is
        PrivacyScreeningState.Clear
        or PrivacyScreeningState.Redacted
        or PrivacyScreeningState.Held
        or PrivacyScreeningState.NeedsReview
        or PrivacyScreeningState.FailedTerminal;

    private static bool IsRetryable(Exception exception) =>
        exception is AiProviderException { IsRetryable: true }
            or HttpRequestException
            or TimeoutException;

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
}
