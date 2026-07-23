using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public sealed record CaptureAnalysisIngestionOptions
{
    public const string DefaultAnalysisVersion = "timeline-v1";
    public const string DefaultEvidencePolicyVersion = "evidence-v1";

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
        TimeProvider? timeProvider = null)
    {
        _manifestScanner = manifestScanner
            ?? throw new ArgumentNullException(nameof(manifestScanner));
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _fingerprintProvider = fingerprintProvider
            ?? throw new ArgumentNullException(nameof(fingerprintProvider));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
                }
            }

            var profile = await GetRunnableProfileAsync(cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                return new CaptureAnalysisIngestionResult(
                    chunks.Length,
                    createdChunkCount,
                    CreatedJobCount: 0,
                    AnalysisReady: false);
            }

            var createdJobCount = 0;
            var unstableChunkCount = 0;
            foreach (var chunk in persistedChunks)
            {
                if (!await ProfileStillRunnableAsync(profile, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return new CaptureAnalysisIngestionResult(
                        chunks.Length,
                        createdChunkCount,
                        createdJobCount,
                        AnalysisReady: false);
                }

                var fingerprint = await _fingerprintProvider
                    .ComputeAsync(chunk, cancellationToken)
                    .ConfigureAwait(false);
                if (!await EvidenceStillMatchesAsync(chunk, cancellationToken)
                    .ConfigureAwait(false))
                {
                    unstableChunkCount++;
                    continue;
                }

                if (!await ProfileStillRunnableAsync(profile, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return new CaptureAnalysisIngestionResult(
                        chunks.Length,
                        createdChunkCount,
                        createdJobCount,
                        AnalysisReady: false,
                        unstableChunkCount);
                }

                var pendingJob = CreatePendingJob(chunk, profile, fingerprint);
                var result = await _jobStore
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
                AnalysisReady: true,
                unstableChunkCount);
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

    private async Task<AiProviderProfileSnapshot?> GetRunnableProfileAsync(
        CancellationToken cancellationToken)
    {
        if (!_settings.Current.CloudAnalysisEnabled)
        {
            return null;
        }

        var profile = await _profileStore
            .GetActiveAsync(cancellationToken)
            .ConfigureAwait(false);
        return profile is { IsComplete: true, IsValidated: true }
            ? profile
            : null;
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

    private async Task<bool> ProfileStillRunnableAsync(
        AiProviderProfileSnapshot expected,
        CancellationToken cancellationToken)
    {
        var current = await GetRunnableProfileAsync(cancellationToken)
            .ConfigureAwait(false);
        return current is not null
            && current.Profile.Id == expected.Profile.Id
            && current.Revision == expected.Revision;
    }

    private async Task<bool> EvidenceStillMatchesAsync(
        CaptureChunk expected,
        CancellationToken cancellationToken)
    {
        var rescanned = ValidateAndOrder(await _manifestScanner
            .ScanCommittedAsync(cancellationToken)
            .ConfigureAwait(false));
        var current = Array.Find(
            rescanned,
            chunk => string.Equals(chunk.Id, expected.Id, StringComparison.Ordinal));
        return current is not null && HasSameCommittedEvidence(expected, current);
    }

    private static bool HasSameCommittedEvidence(
        CaptureChunk expected,
        CaptureChunk current) =>
        string.Equals(expected.Id, current.Id, StringComparison.Ordinal)
        && expected.VideoPath == current.VideoPath
        && expected.ManifestPath == current.ManifestPath
        && expected.Range == current.Range
        && expected.FrameCount == current.FrameCount
        && expected.VideoWidth == current.VideoWidth
        && expected.VideoHeight == current.VideoHeight
        && expected.FrameRateNumerator == current.FrameRateNumerator
        && expected.FrameRateDenominator == current.FrameRateDenominator
        && expected.VideoByteCount == current.VideoByteCount
        && expected.PersistenceGeneration == current.PersistenceGeneration
        && expected.TargetEpoch == current.TargetEpoch
        && current.Availability == CaptureChunkAvailability.Available;

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
