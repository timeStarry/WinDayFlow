using System.Security.Cryptography;
using System.Text;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Application.Tests.Analysis;

public sealed class AnalysisPipelineSupervisorTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        23,
        16,
        0,
        0,
        TimeSpan.FromHours(8));

    [Fact]
    public async Task RunOnceRecoversExpiredLeasesBeforeIngestionAndDrain()
    {
        var options = new AnalysisPipelineSupervisorOptions(
            TimeSpan.FromMinutes(3),
            maximumJobsPerRun: 4);
        using var harness = await CreateHarnessAsync(
            cloudEnabled: true,
            options: options);
        harness.Store.RecoveredLeaseCount = 2;

        var summary = await harness.Supervisor.RunOnceAsync();

        Assert.Equal(2, summary.RecoveredLeaseCount);
        Assert.Equal(
            new CaptureAnalysisIngestionResult(0, 0, 0, true),
            summary.Ingestion);
        Assert.Equal(0, summary.ProcessedJobCount);
        Assert.False(summary.MoreWorkPossible);
        Assert.Equal(Now.ToUniversalTime(), harness.Store.RecoveredAtUtc);
        Assert.Equal(options.RecoveryRetryDelay, harness.Store.RecoveryRetryDelay);
        Assert.Equal(["recover", "scan", "claim"], harness.Calls);
    }

    [Fact]
    public async Task CloudOffStillIngestsCommittedChunksWithoutClaiming()
    {
        var chunk = CreateChunk("cloud-off", minuteOffset: 0);
        using var harness = await CreateHarnessAsync(
            cloudEnabled: false,
            scannedChunks: [chunk]);

        var summary = await harness.Supervisor.RunOnceAsync();

        Assert.Equal(
            new CaptureAnalysisIngestionResult(1, 1, 0, false),
            summary.Ingestion);
        Assert.Equal(0, summary.ProcessedJobCount);
        Assert.False(summary.MoreWorkPossible);
        Assert.Equal(0, harness.Store.ClaimCount);
        Assert.Equal(chunk, await harness.Store.GetChunkAsync(chunk.Id));
        Assert.Equal(["recover", "scan", $"ingest:{chunk.Id}"], harness.Calls);
    }

    [Fact]
    public async Task DrainCountsMixedOutcomesAndContinuesUntilNoWork()
    {
        var scripts = new[]
        {
            new JobScript("completed", AnalysisResultCommitStatus.Committed),
            new JobScript("retryable", AnalysisResultCommitStatus.CloudAnalysisDisabled),
            new JobScript("terminal", AnalysisResultCommitStatus.EntryConflict),
            new JobScript("lease-lost", AnalysisResultCommitStatus.LeaseLost),
        };
        using var harness = await CreateHarnessAsync(
            cloudEnabled: true,
            jobScripts: scripts);

        var summary = await harness.Supervisor.RunOnceAsync();

        Assert.Equal(4, summary.ProcessedJobCount);
        Assert.Equal(1, summary.CompletedJobCount);
        Assert.Equal(1, summary.RetryableFailureCount);
        Assert.Equal(1, summary.TerminalFailureCount);
        Assert.Equal(1, summary.LeaseLostCount);
        Assert.False(summary.MoreWorkPossible);
        Assert.Equal(5, harness.Store.ClaimCount);
        Assert.Equal(4, harness.Committer.CallCount);
    }

    [Fact]
    public async Task DrainStopsAtConfiguredLimitAndReportsPossibleWork()
    {
        var scripts = Enumerable.Range(0, 3)
            .Select(index => new JobScript(
                $"limited-{index}",
                AnalysisResultCommitStatus.Committed))
            .ToArray();
        using var harness = await CreateHarnessAsync(
            cloudEnabled: true,
            jobScripts: scripts,
            options: new AnalysisPipelineSupervisorOptions(
                TimeSpan.Zero,
                maximumJobsPerRun: 2));

        var summary = await harness.Supervisor.RunOnceAsync();

        Assert.Equal(2, summary.ProcessedJobCount);
        Assert.Equal(2, summary.CompletedJobCount);
        Assert.True(summary.MoreWorkPossible);
        Assert.Equal(2, harness.Store.ClaimCount);
        Assert.Equal(1, harness.Store.PendingJobCount);
    }

    [Fact]
    public async Task CallerCancellationDuringDrainPropagatesAndStopsClaiming()
    {
        var scripts = new[]
        {
            new JobScript("cancelled", AnalysisResultCommitStatus.Committed),
            new JobScript("not-claimed", AnalysisResultCommitStatus.Committed),
        };
        using var harness = await CreateHarnessAsync(
            cloudEnabled: true,
            jobScripts: scripts);
        using var cancellation = new CancellationTokenSource();
        harness.Extractor.OnExtract = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<AnalysisEvidenceBatch>(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Supervisor.RunOnceAsync(cancellation.Token));

        Assert.Equal(1, harness.Store.ClaimCount);
        Assert.Equal(1, harness.Store.PendingJobCount);
        Assert.Equal(0, harness.Committer.CallCount);
    }

    [Fact]
    public async Task UnexpectedRecoveryFailureIsNotSwallowed()
    {
        using var harness = await CreateHarnessAsync(cloudEnabled: true);
        var failure = new IOException("recovery failed");
        harness.Store.RecoveryFailure = failure;

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => harness.Supervisor.RunOnceAsync());

        Assert.Same(failure, thrown);
        Assert.Equal(["recover"], harness.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(AnalysisPipelineSupervisorOptions.MaximumJobsPerRunLimit + 1)]
    public void OptionsRejectInvalidMaximumJobsPerRun(int maximumJobsPerRun)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AnalysisPipelineSupervisorOptions(
                TimeSpan.Zero,
                maximumJobsPerRun));
    }

    [Fact]
    public void OptionsRejectRecoveryDelayOutsideSupportedRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AnalysisPipelineSupervisorOptions(
                TimeSpan.FromTicks(-1),
                maximumJobsPerRun: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AnalysisPipelineSupervisorOptions(
                AnalysisPipelineSupervisorOptions.MaximumRecoveryRetryDelay
                    .Add(TimeSpan.FromTicks(1)),
                maximumJobsPerRun: 1));
    }

    [Fact]
    public async Task ParallelDrainHonorsProviderMaximumConcurrency()
    {
        const int jobCount = 6;
        const int maximumConcurrency = 3;
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialBatchStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var activeCount = 0;
        var observedMaximum = 0;

        async Task<AnalysisJobProcessResult> ProcessNextAsync(
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref callCount);
            if (call > jobCount)
            {
                return new AnalysisJobProcessResult(AnalysisJobProcessStatus.NoWork);
            }

            var active = Interlocked.Increment(ref activeCount);
            UpdateMaximum(ref observedMaximum, active);
            if (call == maximumConcurrency)
            {
                initialBatchStarted.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return new AnalysisJobProcessResult(AnalysisJobProcessStatus.Completed);
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        }

        var drain = AnalysisPipelineSupervisor.DrainAsync(
            ProcessNextAsync,
            maximumJobs: 32,
            maximumConcurrency,
            CancellationToken.None);
        await initialBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(maximumConcurrency, Volatile.Read(ref observedMaximum));
        release.TrySetResult();

        var result = await drain;
        Assert.Equal(jobCount, result.ProcessedJobCount);
        Assert.Equal(jobCount, result.CompletedJobCount);
        Assert.False(result.MoreWorkPossible);
        Assert.InRange(Volatile.Read(ref observedMaximum), 1, maximumConcurrency);
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var current = Volatile.Read(ref maximum);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        bool cloudEnabled,
        IReadOnlyList<CaptureChunk>? scannedChunks = null,
        IReadOnlyList<JobScript>? jobScripts = null,
        AnalysisPipelineSupervisorOptions? options = null)
    {
        var calls = new List<string>();
        var scripts = jobScripts ?? [];
        var chunks = scripts
            .Select((script, index) => CreateChunk(script.ChunkId, index))
            .ToArray();
        var profile = CreateProfileSnapshot();
        var bindingStore = new TestStageBindingStore(cloudEnabled, ProfileId);
        var jobs = chunks
            .Select((chunk, index) =>
            {
                var sourceFingerprint = CreateFingerprint(chunk);
                var windowFingerprint = CaptureAnalysisIngestionService
                    .ComputeWindowFingerprint(
                        [new AnalysisWindowMember(chunk, sourceFingerprint, chunk.Range)]);
                var inputFingerprint = CaptureAnalysisIngestionService.BindRouteFingerprint(
                    windowFingerprint,
                    profile,
                    bindingStore.Binding);
                return AnalysisJob.CreatePending(
                    CreateGuid(index + 1),
                    chunk.Id,
                    ProfileId,
                    providerProfileRevision: 1,
                    AnalysisJobProcessor.TimelinePromptVersion,
                    inputFingerprint.Value,
                    maxAttempts: 3,
                    Now.AddMinutes(-5));
            })
            .ToArray();
        var statuses = jobs
            .Select((job, index) => KeyValuePair.Create(job.Id, scripts[index].CommitStatus))
            .ToDictionary();
        var store = new TestPipelineStore(chunks, jobs, calls);
        var scanner = new TestManifestScanner(scannedChunks ?? [], calls);
        var profileStore = new TestProfileStore(profile);
        var settings = new AppSettingsService(new TestSettingsRepository(cloudEnabled));
        await settings.InitializeAsync();
        var ingestion = new CaptureAnalysisIngestionService(
            scanner,
            store,
            store,
            new TestFingerprintProvider(),
            profileStore,
            settings,
            timeProvider: new FixedTimeProvider(Now),
            stageBindingStore: bindingStore);
        var extractor = new TestEvidenceExtractor();
        var committer = new TestResultCommitter(statuses);
        var processor = new AnalysisJobProcessor(
            store,
            store,
            profileStore,
            new TestProviderFactory(new TestProvider(profile.Profile)),
            extractor,
            committer,
            settings,
            new AnalysisJobProcessorOptions(
                "pipeline-supervisor-tests",
                TimeSpan.FromHours(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(1),
                "en-US"),
            new FixedTimeProvider(Now),
            windowStore: store,
            stageBindingStore: bindingStore);
        var supervisor = new AnalysisPipelineSupervisor(
            store,
            ingestion,
            processor,
            options,
            new FixedTimeProvider(Now));
        return new TestHarness(
            supervisor,
            ingestion,
            settings,
            store,
            extractor,
            committer,
            calls);
    }

    private static Guid ProfileId { get; } =
        Guid.Parse("907e84ae-9d27-4fc0-a493-0a605cad94e7");

    private static Guid CreateGuid(int value) => new(
        value,
        0,
        0,
        new byte[8]);

    private static CaptureChunk CreateChunk(string id, int minuteOffset)
    {
        var start = Now.AddMinutes(minuteOffset);
        return new CaptureChunk(
            id,
            new EvidenceRelativePath($"chunks/{id}/manifest.json"),
            new TimeRange(start, start.AddMinutes(1)),
            capturedFrameCount: 10,
            frameCount: 6,
            frameWidth: 1600,
            frameHeight: 900,
            frameByteCount: 4_096,
            persistenceGeneration: 1,
            targetEpoch: 2,
            committedAtUtc: start.AddMinutes(1),
            ingestedAtUtc: start.AddMinutes(2));
    }

    private static AiProviderProfileSnapshot CreateProfileSnapshot()
    {
        var profile = new AiProviderProfile(
            ProfileId,
            "Analysis provider",
            AiProviderKind.OpenAiCompatible,
            new Uri("https://api.example.com/v1/"),
            "vision-v1",
            TimeSpan.FromSeconds(30));
        return new AiProviderProfileSnapshot(
            profile,
            revision: 1,
            hasApiKey: true,
            validatedRevision: 1,
            validatedAtUtc: Now.AddMinutes(-1));
    }

    private static CaptureChunkFingerprint CreateFingerprint(CaptureChunk chunk)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Id));
        return new CaptureChunkFingerprint(Convert.ToHexString(hash));
    }

    private sealed record JobScript(
        string ChunkId,
        AnalysisResultCommitStatus CommitStatus);

    private sealed class TestHarness(
        AnalysisPipelineSupervisor supervisor,
        CaptureAnalysisIngestionService ingestion,
        AppSettingsService settings,
        TestPipelineStore store,
        TestEvidenceExtractor extractor,
        TestResultCommitter committer,
        List<string> calls) : IDisposable
    {
        public AnalysisPipelineSupervisor Supervisor { get; } = supervisor;

        public TestPipelineStore Store { get; } = store;

        public TestEvidenceExtractor Extractor { get; } = extractor;

        public TestResultCommitter Committer { get; } = committer;

        public List<string> Calls { get; } = calls;

        public void Dispose()
        {
            ingestion.Dispose();
            settings.Dispose();
        }
    }

    private sealed class TestPipelineStore :
        ICaptureChunkStore,
        IAnalysisJobStore,
        IAnalysisWindowStore
    {
        private readonly Dictionary<string, CaptureChunk> _chunks;
        private readonly Dictionary<Guid, AnalysisJob> _jobs;
        private readonly Queue<Guid> _pendingJobIds;
        private readonly List<string> _calls;

        public TestPipelineStore(
            IEnumerable<CaptureChunk> chunks,
            IEnumerable<AnalysisJob> jobs,
            List<string> calls)
        {
            _chunks = chunks.ToDictionary(static chunk => chunk.Id, StringComparer.Ordinal);
            _jobs = jobs.ToDictionary(static job => job.Id);
            _pendingJobIds = new Queue<Guid>(_jobs.Keys);
            _calls = calls;
        }

        public int RecoveredLeaseCount { get; set; }

        public Exception? RecoveryFailure { get; set; }

        public DateTimeOffset? RecoveredAtUtc { get; private set; }

        public TimeSpan? RecoveryRetryDelay { get; private set; }

        public int ClaimCount { get; private set; }

        public int PendingJobCount => _pendingJobIds.Count;

        public Task<CaptureChunk?> GetChunkAsync(string chunkId)
        {
            _chunks.TryGetValue(chunkId, out var chunk);
            return Task.FromResult(chunk);
        }

        public Task<CaptureChunkIngestResult> IngestCommittedAsync(
            CaptureChunk chunk,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add($"ingest:{chunk.Id}");
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
            if (_jobs.TryGetValue(pendingJob.Id, out var existing))
            {
                return Task.FromResult(new AnalysisJobEnqueueResult(existing, Created: false));
            }

            _jobs.Add(pendingJob.Id, pendingJob);
            _pendingJobIds.Enqueue(pendingJob.Id);
            return Task.FromResult(new AnalysisJobEnqueueResult(pendingJob, Created: true));
        }

        public Task<AnalysisJobEnqueueResult> EnqueueWindowAsync(
            AnalysisJob pendingJob,
            IReadOnlyList<AnalysisWindowMember> members,
            CancellationToken cancellationToken = default) =>
            EnqueueAsync(pendingJob, cancellationToken);

        public Task<AnalysisWindowSnapshot?> GetWindowAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_jobs.TryGetValue(jobId, out var job)
                || !_chunks.TryGetValue(job.CaptureChunkId, out var chunk))
            {
                return Task.FromResult<AnalysisWindowSnapshot?>(null);
            }

            return Task.FromResult<AnalysisWindowSnapshot?>(new AnalysisWindowSnapshot(
                chunk.Range,
                [new AnalysisWindowMember(chunk, CreateFingerprint(chunk), chunk.Range)],
                []));
        }

        public Task<AnalysisJob?> GetAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _jobs.TryGetValue(jobId, out var job);
            return Task.FromResult(job);
        }

        public Task<bool> HasCompletedAnalysisAsync(
            string captureChunkId,
            string analysisVersion,
            string inputFingerprint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_jobs.Values.Any(job =>
                string.Equals(job.CaptureChunkId, captureChunkId, StringComparison.Ordinal)
                && string.Equals(job.AnalysisVersion, analysisVersion, StringComparison.Ordinal)
                && string.Equals(job.InputFingerprint, inputFingerprint, StringComparison.Ordinal)
                && job.State == AnalysisJobState.Completed));
        }

        public Task<AnalysisJob?> TryClaimNextAsync(
            string leaseOwner,
            DateTimeOffset claimedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClaimCount++;
            _calls.Add("claim");
            if (!_pendingJobIds.TryDequeue(out var jobId))
            {
                return Task.FromResult<AnalysisJob?>(null);
            }

            var current = _jobs[jobId];
            var attempt = current.Attempt + 1;
            var lease = new AnalysisJobLease(
                current.Id,
                leaseOwner,
                $"{attempt:D32}",
                attempt,
                claimedAtUtc.Add(leaseDuration));
            var claimed = Copy(
                current,
                AnalysisJobState.Claimed,
                attempt,
                lease,
                notBeforeUtc: null,
                failure: null,
                claimedAtUtc,
                completedAtUtc: null);
            _jobs[jobId] = claimed;
            return Task.FromResult<AnalysisJob?>(claimed);
        }

        public Task<AnalysisJob?> TryTransitionAsync(
            AnalysisJobLease lease,
            AnalysisJobState expectedState,
            AnalysisJobState nextState,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetLeased(lease, out var current) || current.State != expectedState)
            {
                return Task.FromResult<AnalysisJob?>(null);
            }

            var transitioned = Copy(
                current,
                nextState,
                current.Attempt,
                current.Lease,
                notBeforeUtc: null,
                failure: null,
                changedAtUtc,
                completedAtUtc: null);
            _jobs[current.Id] = transitioned;
            return Task.FromResult<AnalysisJob?>(transitioned);
        }

        public Task<AnalysisJob?> TryRenewLeaseAsync(
            AnalysisJobLease lease,
            DateTimeOffset renewedAtUtc,
            DateTimeOffset newExpiresAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetLeased(lease, out var current))
            {
                return Task.FromResult<AnalysisJob?>(null);
            }

            var renewedLease = new AnalysisJobLease(
                lease.JobId,
                lease.Owner,
                lease.Token,
                lease.Attempt,
                newExpiresAtUtc);
            var renewed = Copy(
                current,
                current.State,
                current.Attempt,
                renewedLease,
                notBeforeUtc: null,
                failure: null,
                renewedAtUtc,
                completedAtUtc: null);
            _jobs[current.Id] = renewed;
            return Task.FromResult<AnalysisJob?>(renewed);
        }

        public Task<AnalysisJob?> TryFailAsync(
            AnalysisJobLease lease,
            AnalysisJobFailure failure,
            AnalysisFailureDisposition disposition,
            DateTimeOffset failedAtUtc,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetLeased(lease, out var current))
            {
                return Task.FromResult<AnalysisJob?>(null);
            }

            var retryable = disposition == AnalysisFailureDisposition.Retryable
                && current.Attempt < current.MaxAttempts;
            var failed = Copy(
                current,
                retryable
                    ? AnalysisJobState.FailedRetryable
                    : AnalysisJobState.FailedTerminal,
                current.Attempt,
                lease: null,
                retryable ? failedAtUtc.Add(retryDelay) : null,
                failure,
                failedAtUtc,
                retryable ? null : failedAtUtc);
            _jobs[current.Id] = failed;
            return Task.FromResult<AnalysisJob?>(failed);
        }

        public Task<AnalysisJob?> TryCancelAsync(
            Guid jobId,
            DateTimeOffset cancelledAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AnalysisJob?>(null);
        }

        public Task<AnalysisJobRetryResult> TryRetryAsync(
            Guid jobId,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AnalysisJobRetryResult(
                AnalysisJobRetryOutcome.StateNotRetryable,
                Job: null));
        }

        public Task<int> RecoverExpiredLeasesAsync(
            DateTimeOffset recoveredAtUtc,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add("recover");
            RecoveredAtUtc = recoveredAtUtc;
            RecoveryRetryDelay = retryDelay;
            return RecoveryFailure is null
                ? Task.FromResult(RecoveredLeaseCount)
                : Task.FromException<int>(RecoveryFailure);
        }

        private bool TryGetLeased(AnalysisJobLease lease, out AnalysisJob current)
        {
            if (_jobs.TryGetValue(lease.JobId, out current!)
                && current.Lease is { } currentLease
                && currentLease.Owner == lease.Owner
                && currentLease.Token == lease.Token
                && currentLease.Attempt == lease.Attempt)
            {
                return true;
            }

            current = null!;
            return false;
        }

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

    private sealed class TestManifestScanner(
        IReadOnlyList<CaptureChunk> chunks,
        List<string> calls) : ICaptureManifestScanner
    {
        public Task<IReadOnlyList<CaptureChunk>> ScanCommittedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("scan");
            return Task.FromResult(chunks);
        }
    }

    private sealed class TestFingerprintProvider : ICaptureChunkFingerprintProvider
    {
        public Task<CaptureChunkFingerprint> ComputeAsync(
            CaptureChunk chunk,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateFingerprint(chunk));
        }
    }

    private sealed class TestProfileStore(AiProviderProfileSnapshot profile)
        : IAiProviderProfileStore
    {
        public Task<IReadOnlyList<AiProviderProfileSnapshot>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiProviderProfileSnapshot>>([profile]);

        public Task<AiProviderProfileSnapshot?> GetAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AiProviderProfileSnapshot?>(
                profile.Profile.Id == profileId ? profile : null);

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

    private sealed class TestStageBindingStore(bool enabled, Guid profileId)
        : IAnalysisStageBindingStore
    {
        public AnalysisStageBinding Binding { get; } = new(
            AnalysisStage.TimelineAnalysis,
            enabled,
            enabled ? profileId : null,
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
            Guid requestedProfileId,
            long profileRevision,
            AnalysisStage stage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderStageValidation?>(
                enabled
                    && requestedProfileId == profileId
                    && stage == AnalysisStage.TimelineAnalysis
                        ? new ProviderStageValidation(
                            requestedProfileId,
                            profileRevision,
                            stage,
                            Now.ToUniversalTime())
                        : null);

        public Task<ProviderStageValidation> MarkValidatedAsync(
            Guid requestedProfileId,
            long profileRevision,
            AnalysisStage stage,
            DateTimeOffset validatedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestEvidenceExtractor : IAnalysisEvidenceExtractor
    {
        public Func<CaptureChunk, CancellationToken, Task<AnalysisEvidenceBatch>>? OnExtract
        {
            get;
            set;
        }

        public Task<AnalysisEvidenceBatch> ExtractAsync(
            CaptureChunk chunk,
            CaptureChunkFingerprint expectedSourceFingerprint,
            CancellationToken cancellationToken = default)
        {
            if (OnExtract is not null)
            {
                return OnExtract(chunk, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AnalysisEvidenceBatch(
                chunk.ManifestPath.Value,
                expectedSourceFingerprint,
                [new AiEvidenceImage(
                    "frame-1",
                    chunk.Range.Start.AddSeconds(10),
                    new byte[] { 0xff, 0xd8, 0xff, 0xd9 })],
                []));
        }
    }

    private sealed class TestProvider(AiProviderProfile profile) : IAiAnalysisProvider
    {
        public AiProviderProfile Profile { get; } = profile;

        public AiProviderCapabilities Capabilities =>
            AiProviderCapabilities.VisionAnalysis
            | AiProviderCapabilities.StructuredOutput;

        public Task<AiAnalysisResponse> AnalyzeAsync(
            AiAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AiAnalysisResponse(
                "request-1",
                Profile.Model,
                AiAnalysisContract.CurrentSchemaVersion,
                activities:
                [
                    new AiActivityCandidate(
                        StartOffsetMilliseconds: 0,
                        EndOffsetMilliseconds: checked(
                            (long)request.Range.Duration.TotalMilliseconds),
                        "Pipeline activity",
                        "Exercise the analysis pipeline.",
                        "focused_work",
                        "focused",
                        [],
                        ["integration"],
                        Confidence: 0.9,
                        ["frame-1"]),
                ]));
        }
    }

    private sealed class TestProviderFactory(IAiAnalysisProvider provider)
        : IAiAnalysisProviderFactory
    {
        public Task<IAiAnalysisProvider> CreateAsync(
            AiProviderProfileSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(provider);
        }
    }

    private sealed class TestResultCommitter(
        IReadOnlyDictionary<Guid, AnalysisResultCommitStatus> statuses)
        : IAnalysisResultCommitter
    {
        public int CallCount { get; private set; }

        public Task<AnalysisResultCommitStatus> TryCommitAsync(
            AnalysisJobLease lease,
            Guid providerProfileId,
            long providerProfileRevision,
            IReadOnlyList<TimelineEntry> entries,
            DateTimeOffset committedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(statuses[lease.JobId]);
        }
    }

    private sealed class TestSettingsRepository(bool cloudEnabled) : IAppSettingsRepository
    {
        private AppSettings _current = AppSettings.Default;

        private readonly bool _unusedCloudEnabled = cloudEnabled;

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_current);
        }

        public Task SaveAsync(
            AppSettings expected,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current = proposed;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
