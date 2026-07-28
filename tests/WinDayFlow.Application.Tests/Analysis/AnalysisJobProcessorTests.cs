using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Application.Tests.Analysis;

public sealed class AnalysisJobProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProfileId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid JobId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private const string ChunkId = "chunk-processor-0001";

    [Fact]
    public async Task HappyPathAdvancesEveryStageAndCommitsValidatedTimeline()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.Completed, result.Status);
        Assert.Equal(JobId, result.JobId);
        Assert.Equal(1, harness.ProviderFactory.CreateCount);
        Assert.Equal(1, harness.Provider.CallCount);
        Assert.Equal(1, harness.Extractor.CallCount);
        Assert.Equal(new string('A', 64), harness.Extractor.ExpectedFingerprint?.Value);
        Assert.Equal(
            [
                AnalysisJobState.Extracting,
                AnalysisJobState.Observing,
                AnalysisJobState.Summarizing,
                AnalysisJobState.Committing,
            ],
            harness.JobStore.Transitions);
        Assert.True(harness.JobStore.RenewCount >= 1);

        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal(AnalysisJobProcessor.TimelinePromptVersion, request.PromptVersion);
        Assert.Equal(JobId, request.JobId);
        Assert.Equal(ChunkId, request.CaptureChunkId);
        var entry = Assert.Single(harness.Committer.Entries);
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal("Focused implementation", entry.Title);
        Assert.Equal(ChunkId, entry.Evidence?.CaptureChunkId);
        Assert.Equal("timeline-v1", entry.AnalysisVersion);
    }

    [Fact]
    public async Task CloudDisabledDoesNotClaimOrContactProvider()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: false);

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.NotReady, result.Status);
        Assert.Equal(0, harness.JobStore.ClaimCount);
        Assert.Equal(0, harness.ProviderFactory.CreateCount);
        Assert.Equal(0, harness.Extractor.CallCount);
    }

    [Fact]
    public async Task StaleProviderRevisionTerminatesWithoutExtractingOrNetworking()
    {
        using var harness = await CreateHarnessAsync(
            cloudEnabled: true,
            activeRevision: 2,
            jobRevision: 1);

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.FailedTerminal, result.Status);
        Assert.Equal(AnalysisJobErrorCode.ProviderRejected, result.FailureCode);
        Assert.Equal(0, harness.ProviderFactory.CreateCount);
        Assert.Equal(0, harness.Extractor.CallCount);
        Assert.Equal(AnalysisJobState.FailedTerminal, harness.JobStore.Current.State);
    }

    [Fact]
    public async Task ChangedEvidenceFingerprintTerminatesBeforeNetworking()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.Extractor.Evidence = CreateEvidence(
            CreateChunk(),
            new CaptureChunkFingerprint(new string('B', 64)));

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.FailedTerminal, result.Status);
        Assert.Equal(AnalysisJobErrorCode.EvidenceInvalid, result.FailureCode);
        Assert.Equal(0, harness.ProviderFactory.CreateCount);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Theory]
    [InlineData(
        AnalysisEvidenceExtractionFailureKind.EvidenceNotFound,
        AnalysisJobErrorCode.EvidenceMissing,
        AnalysisJobProcessStatus.FailedTerminal)]
    [InlineData(
        AnalysisEvidenceExtractionFailureKind.EvidenceConflict,
        AnalysisJobErrorCode.EvidenceInvalid,
        AnalysisJobProcessStatus.FailedTerminal)]
    [InlineData(
        AnalysisEvidenceExtractionFailureKind.DecoderFailure,
        AnalysisJobErrorCode.EvidenceInvalid,
        AnalysisJobProcessStatus.FailedTerminal)]
    [InlineData(
        AnalysisEvidenceExtractionFailureKind.IoFailure,
        AnalysisJobErrorCode.ExtractionFailed,
        AnalysisJobProcessStatus.FailedRetryable)]
    [InlineData(
        AnalysisEvidenceExtractionFailureKind.CryptoFailure,
        AnalysisJobErrorCode.ExtractionFailed,
        AnalysisJobProcessStatus.FailedRetryable)]
    public async Task NativeExtractionFailuresHaveStableJobMappings(
        AnalysisEvidenceExtractionFailureKind failureKind,
        AnalysisJobErrorCode expectedCode,
        AnalysisJobProcessStatus expectedStatus)
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.Extractor.Failure = new AnalysisEvidenceExtractionException(
            failureKind,
            resultCode: -1);

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.Equal(expectedCode, harness.JobStore.Current.Failure?.Code);
        Assert.Equal(0, harness.ProviderFactory.CreateCount);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Theory]
    [InlineData(AiProviderErrorCode.RateLimited, true, AnalysisJobErrorCode.ProviderRateLimited, AnalysisJobProcessStatus.FailedRetryable)]
    [InlineData(AiProviderErrorCode.AuthenticationFailed, false, AnalysisJobErrorCode.ProviderRejected, AnalysisJobProcessStatus.FailedTerminal)]
    [InlineData(AiProviderErrorCode.InvalidResponse, false, AnalysisJobErrorCode.ProviderResponseInvalid, AnalysisJobProcessStatus.FailedTerminal)]
    public async Task ProviderFailuresHaveStableJobMappings(
        AiProviderErrorCode providerCode,
        bool retryable,
        AnalysisJobErrorCode expectedCode,
        AnalysisJobProcessStatus expectedStatus)
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.Provider.Failure = new AiProviderException(
            providerCode,
            "provider detail that must not be persisted",
            Guid.NewGuid(),
            retryable,
            retryAfter: retryable ? TimeSpan.FromMinutes(3) : null);

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.Equal(expectedCode, harness.JobStore.Current.Failure?.Code);
        Assert.Null(harness.JobStore.Current.Failure?.Detail);
        Assert.Equal(0, harness.Committer.CallCount);
        if (retryable)
        {
            Assert.Equal(Now.AddMinutes(3), harness.JobStore.Current.NotBeforeUtc);
        }
    }

    [Fact]
    public async Task ProviderRevisionChangedAfterRequestTerminatesBeforeCommit()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.Provider.AfterAnalyzeAsync = () =>
        {
            harness.ProfileStore.Current = CreateSnapshot(revision: 2);
            return Task.CompletedTask;
        };

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.FailedTerminal, result.Status);
        Assert.Equal(AnalysisJobErrorCode.ProviderRejected, result.FailureCode);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Fact]
    public async Task CloudDisabledAfterRequestRetriesWithoutCommit()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.Provider.AfterAnalyzeAsync = () =>
            harness.Configuration.SetCloudAnalysisEnabledAsync(false);

        var result = await harness.Processor
            .ProcessNextAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AnalysisJobProcessStatus.FailedRetryable, result.Status);
        Assert.Equal(AnalysisJobErrorCode.ProviderUnavailable, result.FailureCode);
        Assert.Equal(1, harness.Provider.CallCount);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Fact]
    public async Task CloudDisableWhileFactoryIsBlockedPreventsProviderSend()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.ProviderFactory.BlockNextCreate();
        var processing = harness.Processor.ProcessNextAsync();
        await harness.ProviderFactory.WaitUntilCreateStartedAsync();

        try
        {
            await harness.Configuration
                .SetCloudAnalysisEnabledAsync(false)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(harness.Configuration.IsCloudAnalysisEnabled);
        }
        finally
        {
            harness.ProviderFactory.ReleaseCreate();
        }

        var result = await processing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AnalysisJobProcessStatus.FailedRetryable, result.Status);
        Assert.Equal(AnalysisJobErrorCode.ProviderUnavailable, result.FailureCode);
        Assert.Equal(1, harness.ProviderFactory.CreateCount);
        Assert.Equal(0, harness.Provider.CallCount);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Fact]
    public async Task ProviderRevisionChangeWhileFactoryIsBlockedPreventsProviderSend()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.ProviderFactory.BlockNextCreate();
        var processing = harness.Processor.ProcessNextAsync();
        await harness.ProviderFactory.WaitUntilCreateStartedAsync();

        harness.ProfileStore.Current = CreateSnapshot(revision: 2);
        harness.ProviderFactory.ReleaseCreate();
        var result = await processing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AnalysisJobProcessStatus.FailedTerminal, result.Status);
        Assert.Equal(AnalysisJobErrorCode.ProviderRejected, result.FailureCode);
        Assert.Equal(1, harness.ProviderFactory.CreateCount);
        Assert.Equal(0, harness.Provider.CallCount);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Fact]
    public async Task LeaseLossDuringRequiredRenewalStopsBeforeExtraction()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.JobStore.LoseLeaseOnRenew = true;

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.LeaseLost, result.Status);
        Assert.Equal(1, harness.JobStore.RenewCount);
        Assert.Equal(0, harness.Extractor.CallCount);
        Assert.Equal(0, harness.ProviderFactory.CreateCount);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Fact]
    public async Task CommitPersistenceFailureBecomesRetryableJobFailure()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.Committer.Failure = new IOException("database unavailable");

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.FailedRetryable, result.Status);
        Assert.Equal(AnalysisJobErrorCode.PersistenceFailure, result.FailureCode);
        Assert.Equal(AnalysisJobState.FailedRetryable, harness.JobStore.Current.State);
    }

    [Fact]
    public async Task CrossLocalMidnightActivityFailsValidationWithoutCommit()
    {
        var localStart = new DateTimeOffset(
            2026,
            7,
            23,
            23,
            59,
            50,
            TimeSpan.FromHours(8));
        using var harness = await CreateHarnessAsync(
            cloudEnabled: true,
            chunk: CreateChunk(localStart));

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.FailedTerminal, result.Status);
        Assert.Equal(AnalysisJobErrorCode.ProviderResponseInvalid, result.FailureCode);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Fact]
    public async Task CallerCancellationLeavesActiveLeaseForRecovery()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        using var cancellation = new CancellationTokenSource();
        harness.Provider.AfterAnalyzeAsync = () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Processor.ProcessNextAsync(cancellation.Token));

        Assert.Equal(AnalysisJobState.Observing, harness.JobStore.Current.State);
        Assert.NotNull(harness.JobStore.Current.Lease);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Fact]
    public async Task EmptyValidatedResponseFailsWithoutCommittingEntries()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        harness.Provider.Response = new AiAnalysisResponse(
            "request-empty",
            "vision-v1",
            AiAnalysisContract.CurrentSchemaVersion,
            activities: []);

        var result = await harness.Processor.ProcessNextAsync();

        Assert.Equal(AnalysisJobProcessStatus.FailedTerminal, result.Status);
        Assert.Equal(AnalysisJobErrorCode.ProviderResponseInvalid, result.FailureCode);
        Assert.Equal(AnalysisJobState.FailedTerminal, harness.JobStore.Current.State);
        Assert.Empty(harness.Committer.Entries);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        bool cloudEnabled,
        long activeRevision = 1,
        long jobRevision = 1,
        CaptureChunk? chunk = null)
    {
        var settingsRepository = new TestSettingsRepository(cloudEnabled);
        var settings = new AppSettingsService(settingsRepository);
        await settings.InitializeAsync();
        var profileStore = new TestProfileStore(CreateSnapshot(activeRevision));
        chunk ??= CreateChunk();
        var job = AnalysisJob.CreatePending(
            JobId,
            chunk.Id,
            ProfileId,
            jobRevision,
            "timeline-v1",
            new string('A', 64),
            maxAttempts: 3,
            Now);
        var jobStore = new TestJobStore(job);
        var chunkStore = new TestChunkStore(chunk);
        var extractor = new TestEvidenceExtractor(CreateEvidence(chunk));
        var provider = new TestProvider(CreateProfile());
        var providerFactory = new TestProviderFactory(provider);
        var configuration = new AiProviderConfigurationService(
            profileStore,
            providerFactory,
            settings,
            new FixedTimeProvider(Now));
        await configuration.InitializeAsync();
        var committer = new TestResultCommitter();
        var processor = new AnalysisJobProcessor(
            jobStore,
            chunkStore,
            profileStore,
            providerFactory,
            extractor,
            committer,
            settings,
            new AnalysisJobProcessorOptions(
                "processor-tests",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(1),
                "en-US"),
            new FixedTimeProvider(Now));
        return new TestHarness(
            processor,
            configuration,
            settings,
            profileStore,
            jobStore,
            extractor,
            provider,
            providerFactory,
            committer);
    }

    private static AiProviderProfile CreateProfile() => new(
        ProfileId,
        "Analysis provider",
        AiProviderKind.OpenAiCompatible,
        new Uri("https://api.example.com/v1/"),
        "vision-v1",
        TimeSpan.FromSeconds(30));

    private static AiProviderProfileSnapshot CreateSnapshot(long revision) => new(
        CreateProfile(),
        revision,
        hasApiKey: true,
        validatedRevision: revision,
        validatedAtUtc: Now.AddMinutes(-1));

    private static CaptureChunk CreateChunk(DateTimeOffset? start = null)
    {
        var rangeStart = start ?? Now;
        var range = new TimeRange(rangeStart, rangeStart.AddMinutes(1));
        return new CaptureChunk(
            ChunkId,
            new EvidenceRelativePath($"chunks/{ChunkId}/capture.mp4"),
            new EvidenceRelativePath($"chunks/{ChunkId}/manifest.json"),
            range,
            frameCount: 6,
            videoWidth: 1920,
            videoHeight: 1080,
            frameRateNumerator: 1,
            frameRateDenominator: 10,
            videoByteCount: 4_096,
            persistenceGeneration: 1,
            targetEpoch: 2,
            committedAtUtc: Now.AddMinutes(1),
            ingestedAtUtc: Now.AddMinutes(2));
    }

    private static AnalysisEvidenceBatch CreateEvidence(
        CaptureChunk chunk,
        CaptureChunkFingerprint? sourceFingerprint = null) => new(
        chunk.VideoPath.Value,
        sourceFingerprint ?? new CaptureChunkFingerprint(new string('A', 64)),
        [new AiEvidenceImage(
            "frame-1",
            chunk.Range.Start.AddSeconds(10),
            new byte[] { 0xff, 0xd8, 0xff, 0xd9 })],
        [new AiAnalysisContextSlice(chunk.Range, "editor.exe", "Editor")]);

    private static AiAnalysisResponse CreateResponse() => new(
        "request-1",
        "vision-v1",
        AiAnalysisContract.CurrentSchemaVersion,
        [
            new AiActivityCandidate(
                StartOffsetMilliseconds: 0,
                EndOffsetMilliseconds: 60_000,
                "Focused implementation",
                "Implemented the analysis worker.",
                "focused_work",
                "focused",
                ["editor.exe"],
                ["coding"],
                Confidence: 0.9,
                ["frame-1"]),
        ]);

    private sealed class TestHarness(
        AnalysisJobProcessor processor,
        AiProviderConfigurationService configuration,
        AppSettingsService settings,
        TestProfileStore profileStore,
        TestJobStore jobStore,
        TestEvidenceExtractor extractor,
        TestProvider provider,
        TestProviderFactory providerFactory,
        TestResultCommitter committer) : IDisposable
    {
        public AnalysisJobProcessor Processor { get; } = processor;

        public AiProviderConfigurationService Configuration { get; } = configuration;

        public AppSettingsService Settings { get; } = settings;

        public TestProfileStore ProfileStore { get; } = profileStore;

        public TestJobStore JobStore { get; } = jobStore;

        public TestEvidenceExtractor Extractor { get; } = extractor;

        public TestProvider Provider { get; } = provider;

        public TestProviderFactory ProviderFactory { get; } = providerFactory;

        public TestResultCommitter Committer { get; } = committer;

        public void Dispose()
        {
            Configuration.Dispose();
            Settings.Dispose();
        }
    }

    private sealed class TestJobStore(AnalysisJob current) : IAnalysisJobStore
    {
        public AnalysisJob Current { get; private set; } = current;

        public int ClaimCount { get; private set; }

        public int RenewCount { get; private set; }

        public bool LoseLeaseOnRenew { get; set; }

        public List<AnalysisJobState> Transitions { get; } = [];

        public Task<AnalysisJobEnqueueResult> EnqueueAsync(
            AnalysisJob pendingJob,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnalysisJob?> GetAsync(
            Guid jobId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AnalysisJob?>(Current.Id == jobId ? Current : null);

        public Task<bool> HasCompletedAnalysisAsync(
            string captureChunkId,
            string analysisVersion,
            string inputFingerprint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchesIdentity = string.Equals(
                    Current.CaptureChunkId,
                    captureChunkId,
                    StringComparison.Ordinal)
                && string.Equals(
                    Current.AnalysisVersion,
                    analysisVersion,
                    StringComparison.Ordinal);
            if (matchesIdentity && !string.Equals(
                    Current.InputFingerprint,
                    inputFingerprint,
                    StringComparison.Ordinal))
            {
                throw new CaptureChunkConflictException(captureChunkId);
            }

            return Task.FromResult(
                matchesIdentity && Current.State == AnalysisJobState.Completed);
        }

        public Task<AnalysisJob?> TryClaimNextAsync(
            string leaseOwner,
            DateTimeOffset claimedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            ClaimCount++;
            if (Current.State is not AnalysisJobState.Pending and not AnalysisJobState.FailedRetryable)
            {
                return Task.FromResult<AnalysisJob?>(null);
            }

            var attempt = Current.Attempt + 1;
            var lease = new AnalysisJobLease(
                Current.Id,
                leaseOwner,
                "0123456789abcdef0123456789abcdef",
                attempt,
                claimedAtUtc.Add(leaseDuration));
            Current = Copy(
                Current,
                AnalysisJobState.Claimed,
                attempt,
                lease,
                notBeforeUtc: null,
                failure: null,
                changedAtUtc: claimedAtUtc,
                completedAtUtc: null);
            return Task.FromResult<AnalysisJob?>(Current);
        }

        public Task<AnalysisJob?> TryTransitionAsync(
            AnalysisJobLease lease,
            AnalysisJobState expectedState,
            AnalysisJobState nextState,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (!HasLease(lease) || Current.State != expectedState)
            {
                return Task.FromResult<AnalysisJob?>(null);
            }

            Current = Copy(
                Current,
                nextState,
                Current.Attempt,
                nextState == AnalysisJobState.Completed ? null : Current.Lease,
                notBeforeUtc: null,
                failure: null,
                changedAtUtc,
                nextState == AnalysisJobState.Completed ? changedAtUtc : null);
            Transitions.Add(nextState);
            return Task.FromResult<AnalysisJob?>(Current);
        }

        public Task<AnalysisJob?> TryRenewLeaseAsync(
            AnalysisJobLease lease,
            DateTimeOffset renewedAtUtc,
            DateTimeOffset newExpiresAtUtc,
            CancellationToken cancellationToken = default)
        {
            RenewCount++;
            if (LoseLeaseOnRenew || !HasLease(lease))
            {
                return Task.FromResult<AnalysisJob?>(null);
            }

            var renewed = new AnalysisJobLease(
                lease.JobId,
                lease.Owner,
                lease.Token,
                lease.Attempt,
                newExpiresAtUtc);
            Current = Copy(
                Current,
                Current.State,
                Current.Attempt,
                renewed,
                notBeforeUtc: null,
                failure: null,
                renewedAtUtc,
                completedAtUtc: null);
            return Task.FromResult<AnalysisJob?>(Current);
        }

        public Task<AnalysisJob?> TryFailAsync(
            AnalysisJobLease lease,
            AnalysisJobFailure failure,
            AnalysisFailureDisposition disposition,
            DateTimeOffset failedAtUtc,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default)
        {
            if (!HasLease(lease))
            {
                return Task.FromResult<AnalysisJob?>(null);
            }

            var retryable = disposition == AnalysisFailureDisposition.Retryable
                && Current.Attempt < Current.MaxAttempts;
            Current = Copy(
                Current,
                retryable
                    ? AnalysisJobState.FailedRetryable
                    : AnalysisJobState.FailedTerminal,
                Current.Attempt,
                lease: null,
                retryable ? failedAtUtc.Add(retryDelay) : null,
                failure,
                failedAtUtc,
                retryable ? null : failedAtUtc);
            return Task.FromResult<AnalysisJob?>(Current);
        }

        public Task<AnalysisJob?> TryCancelAsync(
            Guid jobId,
            DateTimeOffset cancelledAtUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AnalysisJob?>(null);

        public Task<AnalysisJobRetryResult> TryRetryAsync(
            Guid jobId,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AnalysisJobRetryResult(
                AnalysisJobRetryOutcome.StateNotRetryable,
                Job: null));

        public Task<int> RecoverExpiredLeasesAsync(
            DateTimeOffset recoveredAtUtc,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private bool HasLease(AnalysisJobLease lease) =>
            Current.Lease is { } currentLease
            && currentLease.JobId == lease.JobId
            && currentLease.Attempt == lease.Attempt
            && currentLease.Owner == lease.Owner
            && currentLease.Token == lease.Token;

        private static AnalysisJob Copy(
            AnalysisJob source,
            AnalysisJobState state,
            int attempt,
            AnalysisJobLease? lease,
            DateTimeOffset? notBeforeUtc,
            AnalysisJobFailure? failure,
            DateTimeOffset changedAtUtc,
            DateTimeOffset? completedAtUtc) => new(
                source.Id,
                source.CaptureChunkId,
                source.ProviderProfileId,
                source.ProviderProfileRevision,
                source.AnalysisVersion,
                source.InputFingerprint,
                state,
                attempt,
                source.MaxAttempts,
                notBeforeUtc,
                lease,
                failure,
                source.CreatedAtUtc,
                changedAtUtc,
                completedAtUtc);
    }

    private sealed class TestChunkStore(CaptureChunk chunk) : ICaptureChunkStore
    {
        public Task<CaptureChunkIngestResult> IngestCommittedAsync(
            CaptureChunk chunk,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CaptureChunk?> GetAsync(
            string chunkId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureChunk?>(chunk.Id == chunkId ? chunk : null);
    }

    private sealed class TestProfileStore(AiProviderProfileSnapshot current)
        : IAiProviderProfileStore
    {
        public AiProviderProfileSnapshot? Current { get; set; } = current;

        public Task<AiProviderProfileSnapshot?> GetActiveAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<AiProviderProfileSnapshot> SaveActiveAsync(
            AiProviderProfile profile,
            long? expectedRevision,
            AiProviderCredentialUpdate credentialUpdate,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiProviderProfileSnapshot?> MarkValidatedAsync(
            Guid profileId,
            long expectedRevision,
            DateTimeOffset validatedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestEvidenceExtractor(AnalysisEvidenceBatch evidence)
        : IAnalysisEvidenceExtractor
    {
        public AnalysisEvidenceBatch Evidence { get; set; } = evidence;

        public int CallCount { get; private set; }

        public CaptureChunkFingerprint? ExpectedFingerprint { get; private set; }

        public Exception? Failure { get; set; }

        public Task<AnalysisEvidenceBatch> ExtractAsync(
            CaptureChunk chunk,
            CaptureChunkFingerprint expectedSourceFingerprint,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ExpectedFingerprint = expectedSourceFingerprint;
            return Failure is null
                ? Task.FromResult(Evidence)
                : Task.FromException<AnalysisEvidenceBatch>(Failure);
        }
    }

    private sealed class TestProvider(AiProviderProfile profile)
        : IAiAnalysisProvider, IDisposable
    {
        public AiProviderProfile Profile { get; } = profile;

        public AiProviderCapabilities Capabilities =>
            AiProviderCapabilities.VisionAnalysis
            | AiProviderCapabilities.StructuredOutput;

        public Exception? Failure { get; set; }

        public AiAnalysisResponse Response { get; set; } = CreateResponse();

        public Func<Task>? AfterAnalyzeAsync { get; set; }

        public int CallCount { get; private set; }

        public List<AiAnalysisRequest> Requests { get; } = [];

        public async Task<AiAnalysisResponse> AnalyzeAsync(
            AiAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            if (Failure is not null)
            {
                throw Failure;
            }

            if (AfterAnalyzeAsync is not null)
            {
                await AfterAnalyzeAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Response;
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestProviderFactory(TestProvider provider)
        : IAiAnalysisProviderFactory
    {
        private TaskCompletionSource? _createStarted;
        private TaskCompletionSource? _releaseCreate;

        public int CreateCount { get; private set; }

        public void BlockNextCreate()
        {
            if (_releaseCreate is not null)
            {
                throw new InvalidOperationException("A provider create is already blocked.");
            }

            _createStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseCreate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task WaitUntilCreateStartedAsync()
        {
            var started = _createStarted
                ?? throw new InvalidOperationException("Provider create is not blocked.");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void ReleaseCreate()
        {
            _releaseCreate?.TrySetResult();
        }

        public async Task<IAiAnalysisProvider> CreateAsync(
            AiProviderProfileSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            var release = _releaseCreate;
            _createStarted?.TrySetResult();
            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            return provider;
        }
    }

    private sealed class TestResultCommitter : IAnalysisResultCommitter
    {
        public int CallCount { get; private set; }

        public Exception? Failure { get; set; }

        public IReadOnlyList<TimelineEntry> Entries { get; private set; } = [];

        public Task<AnalysisResultCommitStatus> TryCommitAsync(
            AnalysisJobLease lease,
            Guid providerProfileId,
            long providerProfileRevision,
            IReadOnlyList<TimelineEntry> entries,
            DateTimeOffset committedAtUtc,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Failure is not null)
            {
                return Task.FromException<AnalysisResultCommitStatus>(Failure);
            }

            Entries = entries;
            return Task.FromResult(AnalysisResultCommitStatus.Committed);
        }
    }

    private sealed class TestSettingsRepository(bool cloudEnabled) : IAppSettingsRepository
    {
        private AppSettings _current = new(
            AppThemePreference.System,
            CaptureEnabled: false,
            CloudAnalysisEnabled: cloudEnabled,
            RecordingConsent: null);

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_current);

        public Task SaveAsync(
            AppSettings expected,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            if (_current != expected)
            {
                throw new AppSettingsConcurrencyException();
            }

            _current = proposed;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
