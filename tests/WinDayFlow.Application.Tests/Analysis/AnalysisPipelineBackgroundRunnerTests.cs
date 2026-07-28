using System.Threading.Channels;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using Xunit;

namespace WinDayFlow.Application.Tests.Analysis;

public sealed class AnalysisPipelineBackgroundRunnerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartRunsImmediatelyAndRelevantEventsWakeThePipeline()
    {
        var recorder = new RunRecorder();
        await using var harness = await CreateHarnessAsync(recorder.RunAsync);

        await harness.Runner.StartAsync();
        Assert.Equal(1, await recorder.ReadCallAsync());

        harness.ChunkNotifier.RaiseCommitted();
        Assert.Equal(2, await recorder.ReadCallAsync());

        await harness.ProviderConfiguration.SaveAsync(
            "Local test provider",
            "http://localhost:11434/v1/",
            "vision-test",
            requestTimeoutSeconds: 30,
            replacementApiKey: null);
        Assert.Equal(3, await recorder.ReadCallAsync());

        await harness.Settings.SetCloudAnalysisEnabledAsync(enabled: true);
        Assert.Equal(4, await recorder.ReadCallAsync());

        await harness.Settings.SetThemeAsync(AppThemePreference.Dark);
        Assert.Equal(4, recorder.CallCount);
    }

    [Fact]
    public async Task PeriodicFallbackWakesThePipeline()
    {
        var recorder = new RunRecorder();
        var periodicDelay = new ControlledPeriodicDelay();
        var options = new AnalysisPipelineBackgroundRunnerOptions(
            TimeSpan.FromMinutes(7));
        await using var harness = await CreateHarnessAsync(
            recorder.RunAsync,
            options,
            periodicDelay.WaitAsync);

        await harness.Runner.StartAsync();
        Assert.Equal(1, await recorder.ReadCallAsync());

        periodicDelay.Pulse();

        Assert.Equal(2, await recorder.ReadCallAsync());
        Assert.Equal(options.ReconciliationInterval, periodicDelay.LastDelay);
    }

    [Fact]
    public async Task MoreWorkPossibleContinuesWithoutAnotherWake()
    {
        var recorder = new RunRecorder(
            call => Task.FromResult(CreateSummary(moreWorkPossible: call == 1)));
        await using var harness = await CreateHarnessAsync(recorder.RunAsync);

        await harness.Runner.StartAsync();

        Assert.Equal(1, await recorder.ReadCallAsync());
        Assert.Equal(2, await recorder.ReadCallAsync());
        Assert.Equal(2, recorder.CallCount);
    }

    [Fact]
    public async Task RequestRunBurstsAreCoalescedAndRunsNeverOverlap()
    {
        var releaseFirstRun = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new RunRecorder(async call =>
        {
            if (call == 1)
            {
                await releaseFirstRun.Task.ConfigureAwait(false);
            }

            return CreateSummary(moreWorkPossible: false);
        });
        await using var harness = await CreateHarnessAsync(recorder.RunAsync);

        await harness.Runner.StartAsync();
        Assert.Equal(1, await recorder.ReadCallAsync());

        Assert.IsAssignableFrom<IAnalysisPipelineScheduler>(harness.Runner);
        Parallel.For(0, 20, _ => harness.Runner.RequestRun());

        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(1, recorder.MaximumConcurrency);

        releaseFirstRun.TrySetResult();

        Assert.Equal(2, await recorder.ReadCallAsync());
        Assert.Equal(1, recorder.MaximumConcurrency);
    }

    [Fact]
    public async Task SuccessfulRunPublishesSummaryAndAdvancesDataRevision()
    {
        var summary = new AnalysisPipelineRunSummary(
            RecoveredLeaseCount: 1,
            new CaptureAnalysisIngestionResult(
                ScannedChunkCount: 2,
                CreatedChunkCount: 1,
                CreatedJobCount: 1,
                AnalysisReady: true),
            ProcessedJobCount: 1,
            CompletedJobCount: 1,
            RetryableFailureCount: 0,
            TerminalFailureCount: 0,
            LeaseLostCount: 0,
            MoreWorkPossible: false);
        var recorder = new RunRecorder(_ => Task.FromResult(summary));
        await using var harness = await CreateHarnessAsync(recorder.RunAsync);
        using var statuses = new StatusRecorder(harness.StatusSource);

        await harness.Runner.StartAsync();

        var running = await statuses.ReadAsync();
        var idle = await statuses.ReadAsync();
        Assert.Equal(AnalysisPipelineActivityState.Running, running.State);
        Assert.Equal(0, running.DataRevision);
        Assert.Null(running.LastRunSummary);
        Assert.Null(running.FaultCode);
        Assert.Equal(AnalysisPipelineActivityState.Idle, idle.State);
        Assert.Equal(1, idle.DataRevision);
        Assert.Same(summary, idle.LastRunSummary);
        Assert.Null(idle.FaultCode);
        Assert.Equal(running.Sequence + 1, idle.Sequence);
    }

    [Fact]
    public async Task RunFailureWaitsForNextWakeAndDoesNotKillTheLoop()
    {
        var failure = new IOException("transient pipeline failure");
        var recorder = new RunRecorder(call =>
            call == 1
                ? Task.FromException<AnalysisPipelineRunSummary>(failure)
                : Task.FromResult(CreateSummary(moreWorkPossible: false)));
        await using var harness = await CreateHarnessAsync(recorder.RunAsync);

        await harness.Runner.StartAsync();
        Assert.Equal(1, await recorder.ReadCallAsync());

        harness.ChunkNotifier.RaiseCommitted();

        Assert.Equal(2, await recorder.ReadCallAsync());
        Assert.Equal(2, recorder.CallCount);
    }

    [Fact]
    public async Task RunFailurePublishesFaultAndTheNextRunRecovers()
    {
        var failure = new IOException("transient pipeline failure");
        var recoveredSummary = CreateSummary(moreWorkPossible: false);
        var recorder = new RunRecorder(call =>
            call == 1
                ? Task.FromException<AnalysisPipelineRunSummary>(failure)
                : Task.FromResult(recoveredSummary));
        await using var harness = await CreateHarnessAsync(recorder.RunAsync);
        using var statuses = new StatusRecorder(harness.StatusSource);

        await harness.Runner.StartAsync();

        Assert.Equal(
            AnalysisPipelineActivityState.Running,
            (await statuses.ReadAsync()).State);
        var faulted = await statuses.ReadAsync();
        Assert.Equal(AnalysisPipelineActivityState.Faulted, faulted.State);
        Assert.Equal(
            AnalysisPipelineFaultCode.PipelineRunFailed,
            faulted.FaultCode);
        Assert.Equal(1, faulted.DataRevision);

        harness.Runner.RequestRun();

        var running = await statuses.ReadAsync();
        var idle = await statuses.ReadAsync();
        Assert.Equal(AnalysisPipelineActivityState.Running, running.State);
        Assert.Null(running.FaultCode);
        Assert.Equal(AnalysisPipelineActivityState.Idle, idle.State);
        Assert.Null(idle.FaultCode);
        Assert.Same(recoveredSummary, idle.LastRunSummary);
        Assert.Equal(faulted.DataRevision, idle.DataRevision);
    }

    [Fact]
    public async Task PeriodicSchedulerFailurePublishesFaultAndThenRecovers()
    {
        var recorder = new RunRecorder();
        var periodicDelay = new ControlledFailingPeriodicDelay();
        await using var harness = await CreateHarnessAsync(
            recorder.RunAsync,
            delayAsync: periodicDelay.WaitAsync);
        using var statuses = new StatusRecorder(harness.StatusSource);

        await harness.Runner.StartAsync();
        Assert.Equal(
            AnalysisPipelineActivityState.Running,
            (await statuses.ReadAsync()).State);
        Assert.Equal(
            AnalysisPipelineActivityState.Idle,
            (await statuses.ReadAsync()).State);

        periodicDelay.FailFirstWait();

        var faulted = await statuses.ReadAsync();
        Assert.Equal(AnalysisPipelineActivityState.Faulted, faulted.State);
        Assert.Equal(
            AnalysisPipelineFaultCode.SchedulerFailed,
            faulted.FaultCode);
        await periodicDelay.WaitUntilRetryingAsync();
        periodicDelay.Pulse();

        var running = await statuses.ReadAsync();
        var idle = await statuses.ReadAsync();
        Assert.Equal(AnalysisPipelineActivityState.Running, running.State);
        Assert.Null(running.FaultCode);
        Assert.Equal(AnalysisPipelineActivityState.Idle, idle.State);
        Assert.Null(idle.FaultCode);
        Assert.Equal(2, recorder.CallCount);
    }

    [Fact]
    public async Task ThrowingStatusObserverDoesNotInterruptTheRunner()
    {
        var recorder = new RunRecorder();
        await using var harness = await CreateHarnessAsync(recorder.RunAsync);
        var notificationCount = 0;
        harness.StatusSource.StatusChanged += (_, _) =>
        {
            Interlocked.Increment(ref notificationCount);
            throw new InvalidOperationException("observer failure");
        };
        using var statuses = new StatusRecorder(harness.StatusSource);

        await harness.Runner.StartAsync();
        Assert.Equal(1, await recorder.ReadCallAsync());
        Assert.Equal(
            AnalysisPipelineActivityState.Running,
            (await statuses.ReadAsync()).State);
        Assert.Equal(
            AnalysisPipelineActivityState.Idle,
            (await statuses.ReadAsync()).State);
        harness.Runner.RequestRun();
        Assert.Equal(2, await recorder.ReadCallAsync());
        Assert.Equal(
            AnalysisPipelineActivityState.Running,
            (await statuses.ReadAsync()).State);
        Assert.Equal(
            AnalysisPipelineActivityState.Idle,
            (await statuses.ReadAsync()).State);

        Assert.Equal(4, Volatile.Read(ref notificationCount));
        Assert.Equal(
            AnalysisPipelineActivityState.Idle,
            harness.StatusSource.Current.State);
    }

    [Fact]
    public async Task StopCancelsInFlightRunAndUnsubscribesWakeEvents()
    {
        var runStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        async Task<AnalysisPipelineRunSummary> RunAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            runStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("The run should be cancelled.");
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    runCancelled.TrySetResult();
                }
            }
        }

        await using var harness = await CreateHarnessAsync(RunAsync);
        using var statuses = new StatusRecorder(harness.StatusSource);
        await harness.Runner.StartAsync();
        await runStarted.Task.WaitAsync(TestTimeout);
        Assert.Equal(
            AnalysisPipelineActivityState.Running,
            (await statuses.ReadAsync()).State);
        Assert.Equal(1, harness.ChunkNotifier.SubscriberCount);

        await harness.Runner.StopAsync().WaitAsync(TestTimeout);

        await runCancelled.Task.WaitAsync(TestTimeout);
        Assert.Equal(0, harness.ChunkNotifier.SubscriberCount);
        harness.ChunkNotifier.RaiseCommitted();
        await harness.Settings.SetCloudAnalysisEnabledAsync(enabled: true);
        await harness.ProviderConfiguration.SaveAsync(
            "Provider after stop",
            "http://localhost:11434/v1/",
            "vision-after-stop",
            requestTimeoutSeconds: 30,
            replacementApiKey: null);
        Assert.Equal(1, Volatile.Read(ref callCount));
        var stopped = await statuses.ReadAsync();
        Assert.Equal(AnalysisPipelineActivityState.Idle, stopped.State);
        Assert.Null(stopped.FaultCode);
        Assert.Null(stopped.LastRunSummary);
        Assert.Equal(0, stopped.DataRevision);
    }

    [Fact]
    public async Task DisposeBeforeStartReleasesResourcesWithoutSubscribing()
    {
        var recorder = new RunRecorder();
        var harness = await CreateHarnessAsync(recorder.RunAsync);

        await harness.Runner.DisposeAsync();

        Assert.Equal(0, harness.ChunkNotifier.SubscriberCount);
        Assert.Equal(AnalysisPipelineActivityState.Idle, harness.StatusSource.Current.State);
        Assert.Equal(1, harness.StatusSource.Current.Sequence);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => harness.Runner.StartAsync());
        await harness.DisposeDependenciesAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OptionsRejectNonPositiveIntervals(int ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AnalysisPipelineBackgroundRunnerOptions(
                TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void OptionsRejectIntervalsAboveTheSupportedMaximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AnalysisPipelineBackgroundRunnerOptions(
                AnalysisPipelineBackgroundRunnerOptions
                    .MaximumReconciliationInterval
                    .Add(TimeSpan.FromTicks(1))));
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        Func<CancellationToken, Task<AnalysisPipelineRunSummary>> runOnceAsync,
        AnalysisPipelineBackgroundRunnerOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        var settings = new AppSettingsService(new TestSettingsRepository());
        await settings.InitializeAsync();
        var providerStore = new TestProviderProfileStore();
        var providerConfiguration = new AiProviderConfigurationService(
            providerStore,
            new UnusedProviderFactory(),
            settings);
        await providerConfiguration.InitializeAsync();
        var chunkNotifier = new TestChunkCommitNotifier();
        var statusSource = new AnalysisPipelineStatusSource();
        var runner = new AnalysisPipelineBackgroundRunner(
            runOnceAsync,
            chunkNotifier,
            settings,
            providerConfiguration,
            options,
            delayAsync ?? WaitForeverAsync,
            statusSource);
        return new TestHarness(
            runner,
            chunkNotifier,
            settings,
            providerConfiguration,
            statusSource);
    }

    private static Task WaitForeverAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        _ = delay;
        return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static AnalysisPipelineRunSummary CreateSummary(
        bool moreWorkPossible) => new(
        RecoveredLeaseCount: 0,
        new CaptureAnalysisIngestionResult(0, 0, 0, AnalysisReady: false),
        ProcessedJobCount: 0,
        CompletedJobCount: 0,
        RetryableFailureCount: 0,
        TerminalFailureCount: 0,
        LeaseLostCount: 0,
        moreWorkPossible);

    private sealed class TestHarness(
        AnalysisPipelineBackgroundRunner runner,
        TestChunkCommitNotifier chunkNotifier,
        AppSettingsService settings,
        AiProviderConfigurationService providerConfiguration,
        AnalysisPipelineStatusSource statusSource)
        : IAsyncDisposable
    {
        private int _dependenciesDisposed;

        public AnalysisPipelineBackgroundRunner Runner { get; } = runner;

        public TestChunkCommitNotifier ChunkNotifier { get; } = chunkNotifier;

        public AppSettingsService Settings { get; } = settings;

        public AiProviderConfigurationService ProviderConfiguration { get; } =
            providerConfiguration;

        public AnalysisPipelineStatusSource StatusSource { get; } = statusSource;

        public async ValueTask DisposeAsync()
        {
            await Runner.DisposeAsync();
            await DisposeDependenciesAsync();
        }

        public Task DisposeDependenciesAsync()
        {
            if (Interlocked.Exchange(ref _dependenciesDisposed, 1) == 0)
            {
                ProviderConfiguration.Dispose();
                Settings.Dispose();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RunRecorder
    {
        private readonly Channel<int> _calls = Channel.CreateUnbounded<int>();
        private readonly Func<int, Task<AnalysisPipelineRunSummary>> _run;
        private int _activeCount;
        private int _callCount;
        private int _maximumConcurrency;

        public RunRecorder(
            Func<int, Task<AnalysisPipelineRunSummary>>? run = null)
        {
            _run = run ?? (_ => Task.FromResult(
                CreateSummary(moreWorkPossible: false)));
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<AnalysisPipelineRunSummary> RunAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCount);
            UpdateMaximumConcurrency(active);
            _calls.Writer.TryWrite(call);
            try
            {
                return await _run(call).WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
            }
        }

        public Task<int> ReadCallAsync() =>
            _calls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        private void UpdateMaximumConcurrency(int active)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (active > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    active,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class ControlledPeriodicDelay
    {
        private readonly Channel<bool> _pulses = Channel.CreateUnbounded<bool>();
        private long _lastDelayTicks;

        public TimeSpan LastDelay =>
            TimeSpan.FromTicks(Volatile.Read(ref _lastDelayTicks));

        public async Task WaitAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Volatile.Write(ref _lastDelayTicks, delay.Ticks);
            _ = await _pulses.Reader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public void Pulse() => _pulses.Writer.TryWrite(item: true);
    }

    private sealed class ControlledFailingPeriodicDelay
    {
        private readonly TaskCompletionSource _failFirstWait = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _retryStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<bool> _pulses = Channel.CreateUnbounded<bool>();
        private int _callCount;

        public async Task WaitAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                await _failFirstWait.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                throw new IOException("periodic scheduler failure");
            }

            _retryStarted.TrySetResult();
            _ = await _pulses.Reader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public void FailFirstWait() => _failFirstWait.TrySetResult();

        public Task WaitUntilRetryingAsync() =>
            _retryStarted.Task.WaitAsync(TestTimeout);

        public void Pulse() => _pulses.Writer.TryWrite(item: true);
    }

    private sealed class StatusRecorder : IDisposable
    {
        private readonly AnalysisPipelineStatusSource _source;
        private readonly Channel<AnalysisPipelineStatus> _statuses =
            Channel.CreateUnbounded<AnalysisPipelineStatus>();

        public StatusRecorder(AnalysisPipelineStatusSource source)
        {
            _source = source;
            _source.StatusChanged += OnStatusChanged;
        }

        public Task<AnalysisPipelineStatus> ReadAsync() =>
            _statuses.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public void Dispose()
        {
            _source.StatusChanged -= OnStatusChanged;
            _statuses.Writer.TryComplete();
        }

        private void OnStatusChanged(
            object? sender,
            AnalysisPipelineStatusChangedEventArgs eventArgs)
        {
            _ = sender;
            _statuses.Writer.TryWrite(eventArgs.Current);
        }
    }

    private sealed class TestChunkCommitNotifier : ICaptureChunkCommitNotifier
    {
        private EventHandler<CaptureChunkCommittedEventArgs>? _chunkCommitted;

        public int SubscriberCount =>
            _chunkCommitted?.GetInvocationList().Length ?? 0;

        public event EventHandler<CaptureChunkCommittedEventArgs>? ChunkCommitted
        {
            add => _chunkCommitted += value;
            remove => _chunkCommitted -= value;
        }

        public void RaiseCommitted()
        {
            _chunkCommitted?.Invoke(this, CaptureChunkCommittedEventArgs.WakeHint);
        }
    }

    private sealed class TestSettingsRepository : IAppSettingsRepository
    {
        private AppSettings _current = AppSettings.Default;

        public Task<AppSettings> GetAsync(
            CancellationToken cancellationToken = default)
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
            Assert.Equal(_current, expected);
            _current = proposed;
            return Task.CompletedTask;
        }
    }

    private sealed class TestProviderProfileStore : IAiProviderProfileStore
    {
        private AiProviderProfileSnapshot? _current;

        public Task<AiProviderProfileSnapshot?> GetActiveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_current);
        }

        public Task<AiProviderProfileSnapshot> SaveActiveAsync(
            AiProviderProfile profile,
            long? expectedRevision,
            AiProviderCredentialUpdate credentialUpdate,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_current?.Revision, expectedRevision);
            _ = changedAtUtc;
            var revision = checked((_current?.Revision ?? 0) + 1);
            var hasApiKey = credentialUpdate.Kind switch
            {
                AiProviderCredentialUpdateKind.Preserve =>
                    _current?.HasApiKey == true,
                AiProviderCredentialUpdateKind.Replace => true,
                AiProviderCredentialUpdateKind.Clear => false,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(credentialUpdate)),
            };
            _current = new AiProviderProfileSnapshot(
                profile,
                revision,
                hasApiKey,
                validatedRevision: null,
                validatedAtUtc: null);
            return Task.FromResult(_current);
        }

        public Task<AiProviderProfileSnapshot?> MarkValidatedAsync(
            Guid profileId,
            long expectedRevision,
            DateTimeOffset validatedAtUtc,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class UnusedProviderFactory : IAiAnalysisProviderFactory
    {
        public Task<IAiAnalysisProvider> CreateAsync(
            AiProviderProfileSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
