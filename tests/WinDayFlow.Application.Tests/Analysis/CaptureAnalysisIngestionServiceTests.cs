using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Application.Tests.Analysis;

public sealed class CaptureAnalysisIngestionServiceTests
{
    [Fact]
    public async Task CloudOffStillIngestsEveryCommittedChunkWithoutEnqueueing()
    {
        var later = CreateChunk("chunk-b", minuteOffset: 1);
        var earlier = CreateChunk("chunk-a", minuteOffset: 0);
        var scanner = new TestManifestScanner([later, earlier]);
        var store = new TestCaptureAnalysisStore();
        using var settings = await CreateSettingsAsync(cloudEnabled: false);
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            new TestFingerprintProvider(),
            new TestProviderStore(CreateProfile(revision: 1)),
            settings);

        var result = await service.ReconcileAsync();

        Assert.Equal(new CaptureAnalysisIngestionResult(2, 2, 0, false), result);
        Assert.Equal(["chunk-a", "chunk-b"], store.IngestOrder);
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task ValidatedEnabledProviderCreatesStableIdempotentJobs()
    {
        var chunks = new[]
        {
            CreateChunk("chunk-a", minuteOffset: 0),
            CreateChunk("chunk-b", minuteOffset: 1),
        };
        var scanner = new TestManifestScanner(chunks);
        var store = new TestCaptureAnalysisStore();
        var fingerprints = new TestFingerprintProvider();
        using var settings = await CreateSettingsAsync(cloudEnabled: true);
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            fingerprints,
            new TestProviderStore(CreateProfile(revision: 7)),
            settings,
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 23, 9, 30, 0, TimeSpan.Zero)));

        var first = await service.ReconcileAsync();
        var firstJobs = store.Jobs.Values.OrderBy(static job => job.CaptureChunkId).ToArray();
        var second = await service.ReconcileAsync();
        var secondJobs = store.Jobs.Values.OrderBy(static job => job.CaptureChunkId).ToArray();

        Assert.Equal(new CaptureAnalysisIngestionResult(2, 2, 2, true), first);
        Assert.Equal(new CaptureAnalysisIngestionResult(2, 0, 0, true), second);
        Assert.Equal(firstJobs, secondJobs);
        Assert.Equal(2, firstJobs.Length);
        Assert.All(firstJobs, job =>
        {
            Assert.Equal(7, job.ProviderProfileRevision);
            Assert.Equal(CaptureAnalysisIngestionOptions.DefaultAnalysisVersion, job.AnalysisVersion);
            Assert.Matches("^[0-9A-F]{64}$", job.InputFingerprint);
        });
        Assert.NotEqual(firstJobs[0].Id, firstJobs[1].Id);
        Assert.NotEqual(firstJobs[0].InputFingerprint, firstJobs[1].InputFingerprint);
        Assert.Equal(4, fingerprints.CallCount);
    }

    [Fact]
    public async Task ProviderRevisionChangeDuringReconcileDoesNotCreateStaleJobs()
    {
        var scanner = new TestManifestScanner([CreateChunk("chunk-a", minuteOffset: 0)]);
        var store = new TestCaptureAnalysisStore();
        using var settings = await CreateSettingsAsync(cloudEnabled: true);
        var providerStore = new TestProviderStore(
            CreateProfile(revision: 2),
            CreateProfile(revision: 3));
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            new TestFingerprintProvider(),
            providerStore,
            settings);

        var result = await service.ReconcileAsync();

        Assert.Equal(new CaptureAnalysisIngestionResult(1, 1, 0, false), result);
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task ProviderRevisionChangeWhileHashingStopsBeforeStaleEnqueue()
    {
        var scanner = new TestManifestScanner([CreateChunk("chunk-a", minuteOffset: 0)]);
        var store = new TestCaptureAnalysisStore();
        using var settings = await CreateSettingsAsync(cloudEnabled: true);
        var providerStore = new TestProviderStore(
            CreateProfile(revision: 2),
            CreateProfile(revision: 2),
            CreateProfile(revision: 3));
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            new TestFingerprintProvider(),
            providerStore,
            settings);

        var result = await service.ReconcileAsync();

        Assert.Equal(new CaptureAnalysisIngestionResult(1, 1, 0, false), result);
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task ManifestSemanticChangeAfterHashSkipsStaleJob()
    {
        var original = CreateChunk("chunk-a", minuteOffset: 0);
        var changed = CreateChunk("chunk-a", minuteOffset: 2);
        var scanner = new SequencedManifestScanner([original], [changed]);
        var store = new TestCaptureAnalysisStore();
        using var settings = await CreateSettingsAsync(cloudEnabled: true);
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            new TestFingerprintProvider(),
            new TestProviderStore(CreateProfile(revision: 2)),
            settings);

        var result = await service.ReconcileAsync();

        Assert.Equal(
            new CaptureAnalysisIngestionResult(1, 1, 0, true, 1),
            result);
        Assert.Empty(store.Jobs);
    }

    private static CaptureChunk CreateChunk(string id, int minuteOffset)
    {
        var start = new DateTimeOffset(2026, 7, 23, 8, minuteOffset, 0, TimeSpan.Zero);
        return new CaptureChunk(
            id,
            new EvidenceRelativePath($"chunks/{id}/capture.mp4"),
            new EvidenceRelativePath($"chunks/{id}/manifest.json"),
            new TimeRange(start, start.AddMinutes(1)),
            frameCount: 30,
            videoWidth: 1280,
            videoHeight: 720,
            frameRateNumerator: 1,
            frameRateDenominator: 1,
            videoByteCount: 1_024,
            persistenceGeneration: 11,
            targetEpoch: 12,
            committedAtUtc: start.AddMinutes(1),
            ingestedAtUtc: start.AddMinutes(1));
    }

    private static AiProviderProfileSnapshot CreateProfile(long revision)
    {
        var validatedAt = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        return new AiProviderProfileSnapshot(
            new AiProviderProfile(
                Guid.Parse("907e84ae-9d27-4fc0-a493-0a605cad94e7"),
                "Test provider",
                AiProviderKind.OpenAiCompatible,
                new Uri("https://example.test/v1/"),
                "vision-model",
                TimeSpan.FromSeconds(30)),
            revision,
            hasApiKey: true,
            validatedRevision: revision,
            validatedAt);
    }

    private static async Task<AppSettingsService> CreateSettingsAsync(bool cloudEnabled)
    {
        var initial = AppSettings.Default;
        var settings = new AppSettingsService(new TestSettingsRepository(new AppSettings(
            initial.Theme,
            initial.CaptureEnabled,
            cloudEnabled,
            initial.RecordingConsent,
            initial.CapturePrivacy)));
        await settings.InitializeAsync();
        return settings;
    }

    private sealed class TestManifestScanner(IReadOnlyList<CaptureChunk> chunks)
        : ICaptureManifestScanner
    {
        public Task<IReadOnlyList<CaptureChunk>> ScanCommittedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(chunks);
        }
    }

    private sealed class SequencedManifestScanner(
        params IReadOnlyList<CaptureChunk>[] scans) : ICaptureManifestScanner
    {
        private int _index;

        public Task<IReadOnlyList<CaptureChunk>> ScanCommittedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, scans.Length - 1);
            return Task.FromResult(scans[index]);
        }
    }

    private sealed class TestFingerprintProvider : ICaptureChunkFingerprintProvider
    {
        public int CallCount { get; private set; }

        public Task<CaptureChunkFingerprint> ComputeAsync(
            CaptureChunk chunk,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var source = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(chunk.Id));
            return Task.FromResult(
                new CaptureChunkFingerprint(Convert.ToHexString(source)));
        }
    }

    private sealed class TestCaptureAnalysisStore : ICaptureChunkStore, IAnalysisJobStore
    {
        private readonly Dictionary<string, CaptureChunk> _chunks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AnalysisJob> _jobs = new(StringComparer.Ordinal);

        public List<string> IngestOrder { get; } = [];

        public IReadOnlyDictionary<string, AnalysisJob> Jobs => _jobs;

        public Task<CaptureChunkIngestResult> IngestCommittedAsync(
            CaptureChunk chunk,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IngestOrder.Add(chunk.Id);
            if (_chunks.TryGetValue(chunk.Id, out var existing))
            {
                return Task.FromResult(new CaptureChunkIngestResult(existing, Created: false));
            }

            _chunks.Add(chunk.Id, chunk);
            return Task.FromResult(new CaptureChunkIngestResult(chunk, Created: true));
        }

        Task<CaptureChunk?> ICaptureChunkStore.GetAsync(
            string chunkId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _chunks.TryGetValue(chunkId, out var chunk);
            return Task.FromResult(chunk);
        }

        public Task<AnalysisJobEnqueueResult> EnqueueAsync(
            AnalysisJob pendingJob,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = string.Join(
                '|',
                pendingJob.CaptureChunkId,
                pendingJob.ProviderProfileId,
                pendingJob.ProviderProfileRevision,
                pendingJob.AnalysisVersion);
            if (_jobs.TryGetValue(key, out var existing))
            {
                Assert.Equal(existing.Id, pendingJob.Id);
                Assert.Equal(existing.InputFingerprint, pendingJob.InputFingerprint);
                return Task.FromResult(new AnalysisJobEnqueueResult(existing, Created: false));
            }

            _jobs.Add(key, pendingJob);
            return Task.FromResult(new AnalysisJobEnqueueResult(pendingJob, Created: true));
        }

        Task<AnalysisJob?> IAnalysisJobStore.GetAsync(
            Guid jobId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AnalysisJob?> TryClaimNextAsync(
            string leaseOwner,
            DateTimeOffset claimedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AnalysisJob?> TryTransitionAsync(
            AnalysisJobLease lease,
            AnalysisJobState expectedState,
            AnalysisJobState nextState,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AnalysisJob?> TryRenewLeaseAsync(
            AnalysisJobLease lease,
            DateTimeOffset renewedAtUtc,
            DateTimeOffset newExpiresAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AnalysisJob?> TryFailAsync(
            AnalysisJobLease lease,
            AnalysisJobFailure failure,
            AnalysisFailureDisposition disposition,
            DateTimeOffset failedAtUtc,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AnalysisJob?> TryCancelAsync(
            Guid jobId,
            DateTimeOffset cancelledAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> RecoverExpiredLeasesAsync(
            DateTimeOffset recoveredAtUtc,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestProviderStore(params AiProviderProfileSnapshot?[] snapshots)
        : IAiProviderProfileStore
    {
        private int _readIndex;

        public Task<AiProviderProfileSnapshot?> GetActiveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(_readIndex++, snapshots.Length - 1);
            return Task.FromResult(snapshots[index]);
        }

        public Task<AiProviderProfileSnapshot> SaveActiveAsync(
            AiProviderProfile profile,
            long? expectedRevision,
            AiProviderCredentialUpdate credentialUpdate,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiProviderProfileSnapshot?> MarkValidatedAsync(
            Guid profileId,
            long expectedRevision,
            DateTimeOffset validatedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestSettingsRepository(AppSettings settings)
        : IAppSettingsRepository
    {
        private AppSettings _settings = settings;

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(
            AppSettings settings,
            AppSettings expectedCurrent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
