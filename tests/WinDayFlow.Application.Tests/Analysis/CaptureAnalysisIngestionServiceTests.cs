using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Application.Tests.Analysis;

public sealed class CaptureAnalysisIngestionServiceTests
{
    [Fact]
    public void WindowMembersUseTheContinuousSuffixAndAggregateFingerprint()
    {
        var chunks = new[]
        {
            CreateChunk("chunk-before-gap", minuteOffset: 0),
            CreateChunk("chunk-contiguous-a", minuteOffset: 3),
            CreateChunk("chunk-contiguous-b", minuteOffset: 4),
        };
        var fingerprints = chunks.ToDictionary(
            static chunk => chunk.Id,
            CreateFingerprint,
            StringComparer.Ordinal);

        var members = CaptureAnalysisIngestionService.BuildWindowMembers(
            chunks,
            fingerprints,
            chunks[^1]);
        var fingerprint = CaptureAnalysisIngestionService.ComputeWindowFingerprint(members);
        var changedFingerprints = new Dictionary<string, CaptureChunkFingerprint>(
            fingerprints,
            StringComparer.Ordinal)
        {
            [chunks[1].Id] = new CaptureChunkFingerprint(new string('F', 64)),
        };
        var changedMembers = CaptureAnalysisIngestionService.BuildWindowMembers(
            chunks,
            changedFingerprints,
            chunks[^1]);

        Assert.Equal(["chunk-contiguous-a", "chunk-contiguous-b"],
            members.Select(static member => member.Chunk.Id));
        Assert.Equal(chunks[1].Range.Start, members[0].ContributionRange.Start);
        Assert.Equal(chunks[2].Range.End, members[^1].ContributionRange.End);
        Assert.NotEqual(
            fingerprint,
            CaptureAnalysisIngestionService.ComputeWindowFingerprint(changedMembers));
    }

    [Fact]
    public async Task ThreeContinuousFifteenMinuteChunksCreateAFortyFiveMinuteWindow()
    {
        var chunks = new[]
        {
            CreateChunk("chunk-a", minuteOffset: 0, durationMinutes: 15),
            CreateChunk("chunk-b", minuteOffset: 15, durationMinutes: 15),
            CreateChunk("chunk-c", minuteOffset: 30, durationMinutes: 15),
        };
        var scanner = new TestManifestScanner(chunks);
        var store = new TestCaptureAnalysisStore();
        using var settings = await CreateSettingsAsync(cloudEnabled: true);
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            new TestFingerprintProvider(),
            new TestProviderStore(CreateProfile(revision: 1)),
            settings,
            stageBindingStore: new TestStageBindingStore(enabled: true));

        var result = await service.ReconcileAsync();

        Assert.Equal(new CaptureAnalysisIngestionResult(3, 3, 3, true), result);
        var finalWindow = Assert.Single(
            store.EnqueuedWindows,
            static members => members.Count == 3);
        Assert.Equal(chunks.Select(static chunk => chunk.Id),
            finalWindow.Select(static member => member.Chunk.Id));
        Assert.Equal(TimeSpan.FromMinutes(45),
            finalWindow[^1].ContributionRange.End - finalWindow[0].ContributionRange.Start);
        Assert.Equal(1, scanner.ScanCount);
    }

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
            settings,
            stageBindingStore: new TestStageBindingStore(enabled: false));

        var result = await service.ReconcileAsync();

        Assert.Equal(new CaptureAnalysisIngestionResult(2, 2, 0, false), result);
        Assert.Equal(["chunk-a", "chunk-b"], store.IngestOrder);
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task NewChunkAttachesSafeRuleObservationMetadataBeforePersistence()
    {
        var chunk = CreateChunk("chunk-context", minuteOffset: 0);
        var sample = new CaptureContextSample(
            chunk.Id,
            ordinal: 0,
            chunk.Range.Start.AddSeconds(10),
            application: null);
        var scanner = new ContextManifestScanner(chunk, sample);
        var store = new TestCaptureAnalysisStore();
        var contextStore = new RecordingContextStore();
        var match = new CaptureContextRuleMatch(
            Guid.Parse("62c4141b-4cfa-43c0-b222-292f90adf86c"),
            RuleRevision: 3);
        using var settings = await CreateSettingsAsync(cloudEnabled: false);
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            new TestFingerprintProvider(),
            new TestProviderStore(CreateProfile(revision: 1)),
            settings,
            stageBindingStore: new TestStageBindingStore(enabled: false),
            contextStore: contextStore,
            ruleObservations: new StaticRuleObservationSource(
                new CaptureContextRuleEvaluation(
                    ruleSetRevision: 7,
                    applicationContextAvailable: true,
                    windowContextAvailable: true,
                    [match])));

        await service.ReconcileAsync();

        var persisted = Assert.Single(contextStore.Samples);
        Assert.Equal(7, persisted.EvaluatedRuleSetRevision);
        Assert.True(persisted.ApplicationContextAvailable);
        Assert.True(persisted.WindowContextAvailable);
        Assert.Equal(match, Assert.Single(persisted.RuleMatches));
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
                new DateTimeOffset(2026, 7, 23, 9, 30, 0, TimeSpan.Zero)),
            stageBindingStore: new TestStageBindingStore(enabled: true));

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
    public async Task CompletedAnalysisAtExactRouteInputSkipsEnqueue()
    {
        var chunk = CreateChunk("chunk-a", minuteOffset: 0);
        var scanner = new TestManifestScanner([chunk]);
        var store = new TestCaptureAnalysisStore();
        store.MarkCompletedAnalysis(
            chunk.Id,
            CaptureAnalysisIngestionOptions.DefaultAnalysisVersion,
            CreateTimelineInputFingerprint(chunk, revision: 2).Value);
        var fingerprints = new TestFingerprintProvider();
        using var settings = await CreateSettingsAsync(cloudEnabled: true);
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            fingerprints,
            new TestProviderStore(CreateProfile(revision: 2)),
            settings,
            stageBindingStore: new TestStageBindingStore(enabled: true));

        var result = await service.ReconcileAsync();

        Assert.Equal(
            new CaptureAnalysisIngestionResult(1, 1, 0, AnalysisReady: true),
            result);
        Assert.Equal(1, fingerprints.CallCount);
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task CompletedAnalysisWithChangedInputDoesNotBlockNewJob()
    {
        var chunk = CreateChunk("chunk-a", minuteOffset: 0);
        var scanner = new TestManifestScanner([chunk]);
        var store = new TestCaptureAnalysisStore();
        var currentFingerprint = CreateFingerprint(chunk);
        var changedFingerprint =
            (currentFingerprint.Value[0] == 'A' ? "B" : "A")
            + currentFingerprint.Value[1..];
        store.MarkCompletedAnalysis(
            chunk.Id,
            CaptureAnalysisIngestionOptions.DefaultAnalysisVersion,
            changedFingerprint);
        var fingerprints = new TestFingerprintProvider();
        using var settings = await CreateSettingsAsync(cloudEnabled: true);
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            fingerprints,
            new TestProviderStore(CreateProfile(revision: 2)),
            settings,
            stageBindingStore: new TestStageBindingStore(enabled: true));

        var result = await service.ReconcileAsync();

        Assert.Equal(new CaptureAnalysisIngestionResult(1, 1, 1, true), result);
        Assert.Equal(1, fingerprints.CallCount);
        Assert.Single(store.Jobs);
    }

    [Fact]
    public async Task CompletedAnalysisAtDifferentVersionDoesNotBlockNewJob()
    {
        var chunk = CreateChunk("chunk-a", minuteOffset: 0);
        var scanner = new TestManifestScanner([chunk]);
        var store = new TestCaptureAnalysisStore();
        store.MarkCompletedAnalysis(
            chunk.Id,
            "timeline-v1",
            CreateFingerprint(chunk).Value);
        var fingerprints = new TestFingerprintProvider();
        using var settings = await CreateSettingsAsync(cloudEnabled: true);
        using var service = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            fingerprints,
            new TestProviderStore(CreateProfile(revision: 2)),
            settings,
            new CaptureAnalysisIngestionOptions(
                "timeline-v2",
                CaptureAnalysisIngestionOptions.DefaultEvidencePolicyVersion,
                maxAttempts: 5),
            stageBindingStore: new TestStageBindingStore(enabled: true));

        var result = await service.ReconcileAsync();

        Assert.Equal(
            new CaptureAnalysisIngestionResult(1, 1, 1, AnalysisReady: true),
            result);
        Assert.Equal(1, fingerprints.CallCount);
        var job = Assert.Single(store.Jobs.Values);
        Assert.Equal("timeline-v2", job.AnalysisVersion);
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
            settings,
            stageBindingStore: new TestStageBindingStore(enabled: true));

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
            settings,
            stageBindingStore: new TestStageBindingStore(enabled: true));

        var result = await service.ReconcileAsync();

        Assert.Equal(new CaptureAnalysisIngestionResult(1, 1, 0, false), result);
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task ReconcileUsesOneValidatedArchiveSnapshot()
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
            settings,
            stageBindingStore: new TestStageBindingStore(enabled: true));

        var result = await service.ReconcileAsync();

        Assert.Equal(new CaptureAnalysisIngestionResult(1, 1, 1, true), result);
        Assert.Single(store.Jobs);
        Assert.Equal(1, scanner.ScanCount);
    }

    private static CaptureChunk CreateChunk(
        string id,
        int minuteOffset,
        int durationMinutes = 1)
    {
        var start = new DateTimeOffset(2026, 7, 23, 8, minuteOffset, 0, TimeSpan.Zero);
        return new CaptureChunk(
            id,
            new EvidenceRelativePath($"chunks/{id}/manifest.json"),
            new TimeRange(start, start.AddMinutes(durationMinutes)),
            capturedFrameCount: 40,
            frameCount: 30,
            frameWidth: 1280,
            frameHeight: 720,
            frameByteCount: 1_024,
            persistenceGeneration: 11,
            targetEpoch: 12,
            committedAtUtc: start.AddMinutes(durationMinutes),
            ingestedAtUtc: start.AddMinutes(durationMinutes));
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

    private static CaptureChunkFingerprint CreateFingerprint(CaptureChunk chunk)
    {
        var source = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(chunk.Id));
        return new CaptureChunkFingerprint(Convert.ToHexString(source));
    }

    private static CaptureChunkFingerprint CreateTimelineInputFingerprint(
        CaptureChunk chunk,
        long revision)
    {
        var sourceFingerprint = CreateFingerprint(chunk);
        var members = CaptureAnalysisIngestionService.BuildWindowMembers(
            [chunk],
            new Dictionary<string, CaptureChunkFingerprint>(StringComparer.Ordinal)
            {
                [chunk.Id] = sourceFingerprint,
            },
            chunk);
        var selections = new Dictionary<string, PrivacyEvidenceSelection>(StringComparer.Ordinal)
        {
            [chunk.Id] = new(
                PrivacyEvidenceStatus.ReadyOriginal,
                sourceFingerprint,
                chunk.ManifestPath,
                ScreeningId: null,
                ScreeningRevision: null),
        };
        var evidenceFingerprint = CaptureAnalysisIngestionService.ComputeWindowFingerprint(
            members,
            selections);
        return CaptureAnalysisIngestionService.BindRouteFingerprint(
            evidenceFingerprint,
            CreateProfile(revision),
            new TestStageBindingStore(enabled: true).Binding);
    }

    private static async Task<AppSettingsService> CreateSettingsAsync(bool cloudEnabled)
    {
        _ = cloudEnabled;
        var settings = new AppSettingsService(
            new TestSettingsRepository(AppSettings.Default));
        await settings.InitializeAsync();
        return settings;
    }

    private sealed class TestManifestScanner(IReadOnlyList<CaptureChunk> chunks)
        : ICaptureManifestScanner
    {
        public int ScanCount { get; private set; }

        public Task<IReadOnlyList<CaptureChunk>> ScanCommittedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanCount++;
            return Task.FromResult(chunks);
        }
    }

    private sealed class ContextManifestScanner(
        CaptureChunk chunk,
        CaptureContextSample sample)
        : ICaptureManifestScanner,
          ICaptureManifestContextSource
    {
        public Task<IReadOnlyList<CaptureChunk>> ScanCommittedAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureChunk>>([chunk]);

        public Task<IReadOnlyList<CaptureContextSample>> ReadContextAsync(
            CaptureChunk requestedChunk,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(chunk.Id, requestedChunk.Id);
            return Task.FromResult<IReadOnlyList<CaptureContextSample>>([sample]);
        }
    }

    private sealed class RecordingContextStore : ICaptureContextStore
    {
        public IReadOnlyList<CaptureContextSample> Samples { get; private set; } = [];

        public Task ReplaceAsync(
            CaptureChunk chunk,
            IReadOnlyList<CaptureContextSample> samples,
            CaptureExclusionRuleSet rules,
            CancellationToken cancellationToken = default)
        {
            Samples = samples.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CaptureContextSample>> ListAsync(
            string captureChunkId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Samples);
    }

    private sealed class StaticRuleObservationSource(
        CaptureContextRuleEvaluation evaluation)
        : ICaptureRuleObservationSource
    {
        public CaptureContextRuleEvaluation? FindAt(DateTimeOffset sampledAt) =>
            evaluation;
    }

    private sealed class SequencedManifestScanner(
        params IReadOnlyList<CaptureChunk>[] scans) : ICaptureManifestScanner
    {
        private int _index;

        public int ScanCount => Volatile.Read(ref _index);

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
            return Task.FromResult(CreateFingerprint(chunk));
        }
    }

    private sealed class TestCaptureAnalysisStore :
        ICaptureChunkStore,
        IAnalysisJobStore,
        IAnalysisWindowStore
    {
        private readonly Dictionary<string, CaptureChunk> _chunks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AnalysisJob> _jobs = new(StringComparer.Ordinal);
        private readonly HashSet<(
            string CaptureChunkId,
            string AnalysisVersion,
            string InputFingerprint)>
            _completedAnalyses = [];

        public List<string> IngestOrder { get; } = [];

        public List<IReadOnlyList<AnalysisWindowMember>> EnqueuedWindows { get; } = [];

        public IReadOnlyDictionary<string, AnalysisJob> Jobs => _jobs;

        public void MarkCompletedAnalysis(
            string captureChunkId,
            string analysisVersion,
            string inputFingerprint) =>
            _completedAnalyses.Add((captureChunkId, analysisVersion, inputFingerprint));

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
                pendingJob.AnalysisVersion,
                pendingJob.InputFingerprint);
            if (_jobs.TryGetValue(key, out var existing))
            {
                Assert.Equal(existing.Id, pendingJob.Id);
                Assert.Equal(existing.InputFingerprint, pendingJob.InputFingerprint);
                return Task.FromResult(new AnalysisJobEnqueueResult(existing, Created: false));
            }

            _jobs.Add(key, pendingJob);
            return Task.FromResult(new AnalysisJobEnqueueResult(pendingJob, Created: true));
        }

        public Task<AnalysisJobEnqueueResult> EnqueueWindowAsync(
            AnalysisJob pendingJob,
            IReadOnlyList<AnalysisWindowMember> members,
            CancellationToken cancellationToken = default)
        {
            Assert.NotEmpty(members);
            EnqueuedWindows.Add(members.ToArray());
            return EnqueueAsync(pendingJob, cancellationToken);
        }

        public Task<AnalysisWindowSnapshot?> GetWindowAsync(
            Guid jobId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        Task<AnalysisJob?> IAnalysisJobStore.GetAsync(
            Guid jobId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasCompletedAnalysisAsync(
            string captureChunkId,
            string analysisVersion,
            string inputFingerprint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_completedAnalyses.Contains((
                captureChunkId,
                analysisVersion,
                inputFingerprint)));
        }

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

        public Task<AnalysisJobRetryResult> TryRetryAsync(
            Guid jobId,
            DateTimeOffset requestedAtUtc,
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

        public Task<IReadOnlyList<AiProviderProfileSnapshot>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AiProviderProfileSnapshot>>(
                snapshots.OfType<AiProviderProfileSnapshot>().TakeLast(1).ToArray());
        }

        public Task<AiProviderProfileSnapshot?> GetAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(_readIndex++, snapshots.Length - 1);
            var snapshot = snapshots[index];
            return Task.FromResult(snapshot?.Profile.Id == profileId ? snapshot : null);
        }

        public Task<AiProviderProfileSnapshot> CreateAsync(
            AiProviderProfile profile,
            AiProviderCredentialUpdate credentialUpdate,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiProviderProfileSnapshot> UpdateAsync(
            AiProviderProfile profile,
            long expectedRevision,
            AiProviderCredentialUpdate credentialUpdate,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(
            Guid profileId,
            long expectedRevision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestStageBindingStore(bool enabled) : IAnalysisStageBindingStore
    {
        private static readonly Guid ProviderId = CreateProfile(revision: 1).Profile.Id;

        public AnalysisStageBinding Binding { get; } = new(
            AnalysisStage.TimelineAnalysis,
            enabled,
            enabled ? ProviderId : null,
            routeRevision: 1);

        public Task<IReadOnlyList<AnalysisStageBinding>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AnalysisStageBinding>>([Binding]);

        public Task<AnalysisStageBinding> GetAsync(
            AnalysisStage stage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(stage == AnalysisStage.TimelineAnalysis
                ? Binding
                : new AnalysisStageBinding(
                    AnalysisStage.PrivacyInspection,
                    enabled: false,
                    providerProfileId: null,
                    routeRevision: 1));

        public Task<AnalysisStageBinding> SaveAsync(
            AnalysisStage stage,
            bool enabled,
            Guid? providerProfileId,
            long expectedRouteRevision,
            PrivacyStageOptions? privacyOptions,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderStageValidation?> GetValidationAsync(
            Guid profileId,
            long profileRevision,
            AnalysisStage stage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderStageValidation?>(
                enabled
                    && profileId == ProviderId
                    && stage == AnalysisStage.TimelineAnalysis
                        ? new ProviderStageValidation(
                            profileId,
                            profileRevision,
                            stage,
                            new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero))
                        : null);

        public Task<ProviderStageValidation> MarkValidatedAsync(
            Guid profileId,
            long profileRevision,
            AnalysisStage stage,
            DateTimeOffset validatedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
