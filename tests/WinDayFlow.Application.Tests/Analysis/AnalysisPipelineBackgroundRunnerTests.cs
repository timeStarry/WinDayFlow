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
    public async Task WakeBurstsAreCoalescedAndRunsNeverOverlap()
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

        for (var index = 0; index < 20; index++)
        {
            harness.ChunkNotifier.RaiseCommitted();
        }

        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(1, recorder.MaximumConcurrency);

        releaseFirstRun.TrySetResult();

        Assert.Equal(2, await recorder.ReadCallAsync());
        Assert.Equal(1, recorder.MaximumConcurrency);
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
        await harness.Runner.StartAsync();
        await runStarted.Task.WaitAsync(TestTimeout);
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
    }

    [Fact]
    public async Task DisposeBeforeStartReleasesResourcesWithoutSubscribing()
    {
        var recorder = new RunRecorder();
        var harness = await CreateHarnessAsync(recorder.RunAsync);

        await harness.Runner.DisposeAsync();

        Assert.Equal(0, harness.ChunkNotifier.SubscriberCount);
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
        var runner = new AnalysisPipelineBackgroundRunner(
            runOnceAsync,
            chunkNotifier,
            settings,
            providerConfiguration,
            options,
            delayAsync ?? WaitForeverAsync);
        return new TestHarness(
            runner,
            chunkNotifier,
            settings,
            providerConfiguration);
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
        AiProviderConfigurationService providerConfiguration)
        : IAsyncDisposable
    {
        private int _dependenciesDisposed;

        public AnalysisPipelineBackgroundRunner Runner { get; } = runner;

        public TestChunkCommitNotifier ChunkNotifier { get; } = chunkNotifier;

        public AppSettingsService Settings { get; } = settings;

        public AiProviderConfigurationService ProviderConfiguration { get; } =
            providerConfiguration;

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
