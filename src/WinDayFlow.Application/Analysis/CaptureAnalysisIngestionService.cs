using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public sealed record CaptureAnalysisIngestionOptions
{
    public const string DefaultAnalysisVersion = "timeline-v5";
    public const string DefaultEvidencePolicyVersion = "frames-v1";

    public CaptureAnalysisIngestionOptions(
        string analysisVersion,
        string evidencePolicyVersion,
        int maxAttempts)
    {
        ValidateVersion(
            analysisVersion,
            nameof(analysisVersion),
            AnalysisJob.MaximumAnalysisVersionLength);
        ValidateVersion(evidencePolicyVersion, nameof(evidencePolicyVersion), 128);
        if (maxAttempts is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        AnalysisVersion = analysisVersion;
        EvidencePolicyVersion = evidencePolicyVersion;
        MaxAttempts = maxAttempts;
    }

    public string AnalysisVersion { get; }

    public string EvidencePolicyVersion { get; }

    public int MaxAttempts { get; }

    public static CaptureAnalysisIngestionOptions Default { get; } = new(
        DefaultAnalysisVersion,
        DefaultEvidencePolicyVersion,
        maxAttempts: 5);

    private static void ValidateVersion(
        string value,
        string parameterName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A capture-analysis version must be trimmed and contain no control characters.",
                parameterName);
        }
    }
}

public sealed record CaptureAnalysisIngestionResult(
    int ScannedChunkCount,
    int CreatedChunkCount,
    int CreatedJobCount,
    bool AnalysisReady,
    int UnstableChunkCount = 0);

public sealed class CaptureAnalysisIngestionService : IDisposable
{
    private readonly ICaptureManifestScanner _manifestScanner;
    private readonly ICaptureChunkStore _chunkStore;
    private readonly IAnalysisJobStore _jobStore;
    private readonly ICaptureChunkFingerprintProvider _fingerprintProvider;
    private readonly IAiProviderProfileStore _profileStore;
    private readonly AppSettingsService _settings;
    private readonly IAnalysisStageBindingStore? _stageBindingStore;
    private readonly ICaptureManifestContextSource? _contextSource;
    private readonly ICaptureContextStore? _contextStore;
    private readonly IPrivacyScreeningService? _privacyScreeningService;
    private readonly ICaptureRuleObservationSource? _ruleObservations;
    private readonly CaptureAnalysisIngestionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public CaptureAnalysisIngestionService(
        ICaptureManifestScanner manifestScanner,
        ICaptureChunkStore chunkStore,
        IAnalysisJobStore jobStore,
        ICaptureChunkFingerprintProvider fingerprintProvider,
        IAiProviderProfileStore profileStore,
        AppSettingsService settings,
        CaptureAnalysisIngestionOptions? options = null,
        TimeProvider? timeProvider = null,
        IAnalysisStageBindingStore? stageBindingStore = null,
        ICaptureContextStore? contextStore = null,
        IPrivacyScreeningService? privacyScreeningService = null,
        ICaptureRuleObservationSource? ruleObservations = null)
    {
        _manifestScanner = manifestScanner
            ?? throw new ArgumentNullException(nameof(manifestScanner));
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _fingerprintProvider = fingerprintProvider
            ?? throw new ArgumentNullException(nameof(fingerprintProvider));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _stageBindingStore = stageBindingStore;
        _contextSource = manifestScanner as ICaptureManifestContextSource;
        _contextStore = contextStore;
        _privacyScreeningService = privacyScreeningService;
        _ruleObservations = ruleObservations;
        _options = options ?? CaptureAnalysisIngestionOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CaptureAnalysisIngestionResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var scanned = await _manifestScanner
                .ScanCommittedAsync(cancellationToken)
                .ConfigureAwait(false);
            var chunks = ValidateAndOrder(scanned);
            var persistedChunks = new List<CaptureChunk>(chunks.Length);
            var createdChunkCount = 0;

            foreach (var chunk in chunks)
            {
                var result = await _chunkStore
                    .IngestCommittedAsync(chunk, cancellationToken)
                    .ConfigureAwait(false);
                persistedChunks.Add(result.Chunk);
                if (result.Created)
                {
                    createdChunkCount++;
                    if (_contextSource is not null && _contextStore is not null)
                    {
                        var context = await _contextSource
                            .ReadContextAsync(result.Chunk, cancellationToken)
                            .ConfigureAwait(false);
                        context = AttachRuleEvaluations(context);
                        await _contextStore.ReplaceAsync(
                                result.Chunk,
                                context,
                                _settings.Current.Evidence.SendRules,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            var route = await GetRunnableRouteAsync(cancellationToken)
                .ConfigureAwait(false);
            if (route is null)
            {
                return new CaptureAnalysisIngestionResult(
                    chunks.Length,
                    createdChunkCount,
                    CreatedJobCount: 0,
                    AnalysisReady: false);
            }

            var createdJobCount = 0;
            var fingerprints = new Dictionary<string, CaptureChunkFingerprint>(
                persistedChunks.Count,
                StringComparer.Ordinal);
            foreach (var chunk in persistedChunks)
            {
                fingerprints.Add(
                    chunk.Id,
                    await _fingerprintProvider
                        .ComputeAsync(chunk, cancellationToken)
                        .ConfigureAwait(false));
            }

            var privacySelections = new Dictionary<string, PrivacyEvidenceSelection>(
                persistedChunks.Count,
                StringComparer.Ordinal);
            foreach (var chunk in persistedChunks)
            {
                var originalFingerprint = fingerprints[chunk.Id];
                var selection = _privacyScreeningService is null
                    ? new PrivacyEvidenceSelection(
                        PrivacyEvidenceStatus.ReadyOriginal,
                        originalFingerprint,
                        chunk.ManifestPath,
                        ScreeningId: null,
                        ScreeningRevision: null)
                    : await _privacyScreeningService.PrepareAsync(
                            chunk,
                            originalFingerprint,
                            CreatePrivacyOperationId(chunk, originalFingerprint),
                            cancellationToken)
                        .ConfigureAwait(false);
                privacySelections.Add(chunk.Id, selection);
            }

            foreach (var chunk in persistedChunks)
            {
                if (!await RouteStillRunnableAsync(route, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return new CaptureAnalysisIngestionResult(
                        chunks.Length,
                        createdChunkCount,
                        createdJobCount,
                        AnalysisReady: false);
                }

                var originalWindowMembers = BuildWindowMembers(
                    persistedChunks,
                    fingerprints,
                    chunk);
                if (originalWindowMembers.Any(member =>
                        !privacySelections[member.Chunk.Id].IsReady))
                {
                    continue;
                }

                var windowMembers = originalWindowMembers
                    .Select(member => new AnalysisWindowMember(
                        member.Chunk,
                        privacySelections[member.Chunk.Id].Fingerprint!,
                        member.ContributionRange))
                    .ToArray();
                var evidenceFingerprint = _jobStore is IAnalysisWindowStore
                    ? ComputeWindowFingerprint(windowMembers, privacySelections)
                    : privacySelections[chunk.Id].Fingerprint!;
                var fingerprint = _stageBindingStore is null
                    ? evidenceFingerprint
                    : BindRouteFingerprint(
                        evidenceFingerprint,
                        route.Profile,
                        route.Binding);
                if (await _jobStore
                    .HasCompletedAnalysisAsync(
                        chunk.Id,
                        _options.AnalysisVersion,
                        fingerprint.Value,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                if (!await RouteStillRunnableAsync(route, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return new CaptureAnalysisIngestionResult(
                        chunks.Length,
                        createdChunkCount,
                        createdJobCount,
                        AnalysisReady: false);
                }

                var pendingJob = CreatePendingJob(chunk, route.Profile, fingerprint);
                var result = _jobStore is IAnalysisWindowStore windowStore
                    ? await windowStore
                        .EnqueueWindowAsync(pendingJob, windowMembers, cancellationToken)
                        .ConfigureAwait(false)
                    : await _jobStore
                        .EnqueueAsync(pendingJob, cancellationToken)
                        .ConfigureAwait(false);
                if (result.Created)
                {
                    createdJobCount++;
                }
            }

            return new CaptureAnalysisIngestionResult(
                chunks.Length,
                createdChunkCount,
                createdJobCount,
                AnalysisReady: true);
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

    private IReadOnlyList<CaptureContextSample> AttachRuleEvaluations(
        IReadOnlyList<CaptureContextSample> samples)
    {
        if (_ruleObservations is null || samples.Count == 0)
        {
            return samples;
        }

        return samples.Select(sample =>
        {
            var evaluation = _ruleObservations.FindAt(sample.SampledAt);
            if (evaluation is null)
            {
                return sample;
            }

            var matches = sample.RuleMatches
                .Concat(evaluation.RuleMatches)
                .GroupBy(static match => match.RuleId)
                .Select(static group => group.Last())
                .OrderBy(static match => match.RuleId)
                .ToArray();
            return new CaptureContextSample(
                sample.CaptureChunkId,
                sample.Ordinal,
                sample.SampledAt,
                sample.Application,
                matches,
                evaluation.RuleSetRevision,
                evaluation.ApplicationContextAvailable,
                evaluation.WindowContextAvailable);
        }).ToArray();
    }

    private async Task<RunnableTimelineRoute?> GetRunnableRouteAsync(
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

    private AnalysisJob CreatePendingJob(
        CaptureChunk chunk,
        AiProviderProfileSnapshot profile,
        CaptureChunkFingerprint fingerprint)
    {
        var createdAt = _timeProvider.GetUtcNow().ToUniversalTime();
        return AnalysisJob.CreatePending(
            CreateDeterministicJobId(chunk, profile, fingerprint),
            chunk.Id,
            profile.Profile.Id,
            profile.Revision,
            _options.AnalysisVersion,
            fingerprint.Value,
            _options.MaxAttempts,
            createdAt);
    }

    private Guid CreateDeterministicJobId(
        CaptureChunk chunk,
        AiProviderProfileSnapshot profile,
        CaptureChunkFingerprint fingerprint)
    {
        var canonical = string.Join(
            '\n',
            "capture-analysis-job-v1",
            chunk.Id,
            fingerprint.Value,
            profile.Profile.Id.ToString("N", CultureInfo.InvariantCulture),
            profile.Revision.ToString(CultureInfo.InvariantCulture),
            _options.AnalysisVersion,
            _options.EvidencePolicyVersion);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    internal static IReadOnlyList<AnalysisWindowMember> BuildWindowMembers(
        IReadOnlyList<CaptureChunk> chunks,
        IReadOnlyDictionary<string, CaptureChunkFingerprint> fingerprints,
        CaptureChunk anchor)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(fingerprints);
        ArgumentNullException.ThrowIfNull(anchor);
        var windowEnd = anchor.Range.End;
        var cutoff = windowEnd - TimeSpan.FromMinutes(45);
        var anchorLocalDate = DateOnly.FromDateTime(windowEnd.AddTicks(-1).DateTime);
        var localDayStart = new DateTimeOffset(
            anchorLocalDate.ToDateTime(TimeOnly.MinValue),
            windowEnd.Offset);
        var windowStart = cutoff > localDayStart ? cutoff : localDayStart;

        var candidates = chunks
            .Where(chunk => chunk.Range.Start < windowEnd && chunk.Range.End > windowStart)
            .OrderBy(static value => value.Range.Start)
            .ThenBy(static value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var anchorIndex = Array.FindIndex(candidates, chunk => string.Equals(
            chunk.Id,
            anchor.Id,
            StringComparison.Ordinal));
        if (anchorIndex < 0)
        {
            throw new InvalidDataException(
                "The analysis window does not contain its anchor capture chunk.");
        }

        var continuous = new List<CaptureChunk> { candidates[anchorIndex] };
        var nextStart = candidates[anchorIndex].Range.Start;
        for (var index = anchorIndex - 1; index >= 0; index--)
        {
            var candidate = candidates[index];
            if (nextStart - candidate.Range.End > TimeSpan.FromSeconds(1))
            {
                break;
            }

            continuous.Add(candidate);
            nextStart = candidate.Range.Start < nextStart
                ? candidate.Range.Start
                : nextStart;
        }

        continuous.Reverse();
        var members = new List<AnalysisWindowMember>(continuous.Count);
        foreach (var chunk in continuous)
        {

            if (!fingerprints.TryGetValue(chunk.Id, out var fingerprint))
            {
                throw new InvalidDataException(
                    $"Capture chunk '{chunk.Id}' has no source fingerprint.");
            }

            var contributionStart = chunk.Range.Start > windowStart
                ? chunk.Range.Start
                : windowStart;
            var contributionEnd = chunk.Range.End < windowEnd
                ? chunk.Range.End
                : windowEnd;
            members.Add(new AnalysisWindowMember(
                chunk,
                fingerprint,
                new TimeRange(contributionStart, contributionEnd)));
        }

        return members;
    }

    internal static CaptureChunkFingerprint ComputeWindowFingerprint(
        IReadOnlyList<AnalysisWindowMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            throw new ArgumentException(
                "An aggregate input fingerprint requires window members.",
                nameof(members));
        }

        var canonical = new StringBuilder("capture-analysis-window-v1\n");
        foreach (var member in members)
        {
            canonical.Append(member.Chunk.Id).Append('\n')
                .Append(member.SourceFingerprint.Value).Append('\n')
                .Append(member.ContributionRange.Start.UtcDateTime.Ticks)
                .Append('\n')
                .Append(member.ContributionRange.End.UtcDateTime.Ticks)
                .Append('\n');
        }

        return new CaptureChunkFingerprint(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }

    internal static CaptureChunkFingerprint ComputeWindowFingerprint(
        IReadOnlyList<AnalysisWindowMember> members,
        IReadOnlyDictionary<string, PrivacyEvidenceSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(selections);
        if (members.Count == 0)
        {
            throw new ArgumentException(
                "An aggregate input fingerprint requires window members.",
                nameof(members));
        }

        var canonical = new StringBuilder("capture-analysis-private-window-v1\n");
        foreach (var member in members)
        {
            if (!selections.TryGetValue(member.Chunk.Id, out var selection)
                || !selection.IsReady
                || selection.Fingerprint != member.SourceFingerprint)
            {
                throw new InvalidDataException(
                    $"Capture chunk '{member.Chunk.Id}' has no matching privacy selection.");
            }

            canonical.Append(member.Chunk.Id).Append('\n')
                .Append(member.SourceFingerprint.Value).Append('\n')
                .Append(member.ContributionRange.Start.UtcDateTime.Ticks).Append('\n')
                .Append(member.ContributionRange.End.UtcDateTime.Ticks).Append('\n')
                .Append((int)selection.Status).Append('\n')
                .Append(selection.ScreeningId?.ToString("N", CultureInfo.InvariantCulture) ?? "-")
                .Append('\n')
                .Append(selection.ScreeningRevision?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append('\n')
                .Append(selection.ProviderProfileId?.ToString("N", CultureInfo.InvariantCulture) ?? "-")
                .Append('\n')
                .Append(selection.ProviderProfileRevision?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append('\n')
                .Append(selection.PrivacyRouteRevision.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return new CaptureChunkFingerprint(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }

    private async Task<bool> RouteStillRunnableAsync(
        RunnableTimelineRoute expected,
        CancellationToken cancellationToken)
    {
        var current = await GetRunnableRouteAsync(cancellationToken)
            .ConfigureAwait(false);
        return current is not null
            && current.Profile.Profile.Id == expected.Profile.Profile.Id
            && current.Profile.Revision == expected.Profile.Revision
            && current.Binding.RouteRevision == expected.Binding.RouteRevision;
    }

    internal static CaptureChunkFingerprint BindRouteFingerprint(
        CaptureChunkFingerprint evidenceFingerprint,
        AiProviderProfileSnapshot profile,
        AnalysisStageBinding binding)
    {
        var canonical = string.Join(
            '\n',
            "timeline-route-v1",
            evidenceFingerprint.Value,
            profile.Profile.Id.ToString("N", CultureInfo.InvariantCulture),
            profile.Revision.ToString(CultureInfo.InvariantCulture),
            binding.RouteRevision.ToString(CultureInfo.InvariantCulture));
        return new CaptureChunkFingerprint(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    internal sealed record RunnableTimelineRoute(
        AiProviderProfileSnapshot Profile,
        AnalysisStageBinding Binding);

    internal static Guid CreatePrivacyOperationId(
        CaptureChunk chunk,
        CaptureChunkFingerprint fingerprint)
    {
        var canonical = string.Join(
            '\n',
            "privacy-screening-operation-v1",
            chunk.Id,
            fingerprint.Value);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    private static CaptureChunk[] ValidateAndOrder(
        IReadOnlyList<CaptureChunk> scanned)
    {
        ArgumentNullException.ThrowIfNull(scanned);
        if (scanned.Any(static chunk => chunk is null))
        {
            throw new InvalidDataException("The manifest scanner returned a null chunk.");
        }

        var ordered = scanned
            .OrderBy(static chunk => chunk.Range.Start)
            .ThenBy(static chunk => chunk.Id, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Select(static chunk => chunk.Id)
            .Distinct(StringComparer.Ordinal)
            .Count() != ordered.Length)
        {
            throw new InvalidDataException(
                "The manifest scanner returned duplicate chunk identifiers.");
        }

        return ordered;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}
