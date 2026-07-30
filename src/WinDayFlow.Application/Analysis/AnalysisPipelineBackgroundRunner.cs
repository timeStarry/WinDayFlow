using System.Diagnostics;
using WinDayFlow.Application.Capture;

namespace WinDayFlow.Application.Analysis;

public sealed record AnalysisPipelineBackgroundRunnerOptions
{
    public AnalysisPipelineBackgroundRunnerOptions(
        TimeSpan reconciliationInterval)
    {
        if (reconciliationInterval <= TimeSpan.Zero
            || reconciliationInterval > MaximumReconciliationInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        }

        ReconciliationInterval = reconciliationInterval;
    }

    public static TimeSpan MaximumReconciliationInterval { get; } =
        TimeSpan.FromDays(1);

    public TimeSpan ReconciliationInterval { get; }

    public static AnalysisPipelineBackgroundRunnerOptions Default { get; } =
        new(TimeSpan.FromMinutes(1));
}

public sealed class AnalysisPipelineBackgroundRunner :
    IAnalysisPipelineScheduler,
    IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task<AnalysisPipelineRunSummary>>
        _runOnceAsync;
    private readonly ICaptureChunkCommitNotifier _chunkCommitNotifier;
    private readonly AnalysisPipelineBackgroundRunnerOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly AnalysisPipelineStatusSource _statusSource;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _completion;
    private int _stoppedStatusPublished;
    private RunnerState _state;

    public AnalysisPipelineBackgroundRunner(
        AnalysisPipelineSupervisor supervisor,
        ICaptureChunkCommitNotifier chunkCommitNotifier,
        AnalysisPipelineBackgroundRunnerOptions? options = null,
        TimeProvider? timeProvider = null,
        AnalysisPipelineStatusSource? statusSource = null)
    {
        _runOnceAsync = CreateRunOnceDelegate(supervisor);
        _chunkCommitNotifier = chunkCommitNotifier
            ?? throw new ArgumentNullException(nameof(chunkCommitNotifier));
        _options = options ?? AnalysisPipelineBackgroundRunnerOptions.Default;
        _delayAsync = CreateDelayDelegate(timeProvider);
        _statusSource = statusSource
            ?? new AnalysisPipelineStatusSource(timeProvider);
    }

    internal AnalysisPipelineBackgroundRunner(
        Func<CancellationToken, Task<AnalysisPipelineRunSummary>> runOnceAsync,
        ICaptureChunkCommitNotifier chunkCommitNotifier,
        AnalysisPipelineBackgroundRunnerOptions? options,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        AnalysisPipelineStatusSource statusSource)
    {
        _runOnceAsync = runOnceAsync
            ?? throw new ArgumentNullException(nameof(runOnceAsync));
        _chunkCommitNotifier = chunkCommitNotifier
            ?? throw new ArgumentNullException(nameof(chunkCommitNotifier));
        _options = options ?? AnalysisPipelineBackgroundRunnerOptions.Default;
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _statusSource = statusSource
            ?? throw new ArgumentNullException(nameof(statusSource));
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state == RunnerState.Disposed, this);
            if (_state != RunnerState.Created)
            {
                throw new InvalidOperationException(
                    "The analysis pipeline background runner can only be started once.");
            }

            _lifetimeCancellation = new CancellationTokenSource();
            _state = RunnerState.Running;
            try
            {
                SubscribeEvents();
                var lifetimeToken = _lifetimeCancellation.Token;
                _completion = CompleteLifecycleAsync(
                    RunLoopAsync(lifetimeToken),
                    RunPeriodicWakeAsync(lifetimeToken));
            }
            catch
            {
                UnsubscribeEvents();
                _state = RunnerState.Stopped;
                _lifetimeCancellation.Dispose();
                _lifetimeCancellation = null;
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? lifetimeCancellation = null;
        Task? completion;
        var stoppedWithoutStarting = false;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state == RunnerState.Disposed, this);
            if (_state == RunnerState.Created)
            {
                _state = RunnerState.Stopped;
                stoppedWithoutStarting = true;
            }
            else if (_state == RunnerState.Running)
            {
                _state = RunnerState.Stopping;
                UnsubscribeEvents();
                lifetimeCancellation = _lifetimeCancellation;
            }

            completion = _completion;
        }

        if (stoppedWithoutStarting)
        {
            PublishStoppedOnce();
            return;
        }

        lifetimeCancellation?.Cancel();
        if (completion is not null)
        {
            await completion
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetimeCancellation;
        Task? completion;

        lock (_sync)
        {
            if (_state == RunnerState.Disposed)
            {
                return;
            }

            if (_state == RunnerState.Running)
            {
                UnsubscribeEvents();
            }

            _state = RunnerState.Disposed;
            lifetimeCancellation = _lifetimeCancellation;
            completion = _completion;
        }

        lifetimeCancellation?.Cancel();
        try
        {
            if (completion is not null)
            {
                await completion.ConfigureAwait(false);
            }
        }
        finally
        {
            if (completion is null)
            {
                PublishStoppedOnce();
            }

            lifetimeCancellation?.Dispose();
            _wakeSignal.Dispose();
        }
    }

    public void RequestRun() => SignalWake();

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var runImmediately = true;
        var aggregateWithPrevious = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (runImmediately)
            {
                _ = _wakeSignal.Wait(0, CancellationToken.None);
            }
            else
            {
                try
                {
                    await _wakeSignal
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            try
            {
                _statusSource.PublishRunning();
                var summary = await _runOnceAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (summary is null)
                {
                    throw new InvalidOperationException(
                        "The analysis pipeline supervisor returned no run summary.");
                }

                _statusSource.PublishRunCompleted(
                    summary,
                    aggregateWithPrevious);
                runImmediately = summary.MoreWorkPossible;
                aggregateWithPrevious = runImmediately;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"The analysis pipeline background run failed and will be retried after the next wake: {exception.GetType().Name}");
                _statusSource.PublishFaulted(
                    AnalysisPipelineFaultCode.PipelineRunFailed);
                runImmediately = false;
                aggregateWithPrevious = false;
            }
        }
    }

    private async Task RunPeriodicWakeAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _delayAsync(
                        _options.ReconciliationInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"The analysis pipeline periodic wake failed and will be retried: {exception.GetType().Name}");
                _statusSource.PublishFaulted(
                    AnalysisPipelineFaultCode.SchedulerFailed);
                await Task.Yield();
                continue;
            }

            SignalWake();
        }
    }

    private async Task CompleteLifecycleAsync(Task loop, Task periodicWake)
    {
        try
        {
            await Task.WhenAll(loop, periodicWake).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (_state == RunnerState.Stopping)
                {
                    _state = RunnerState.Stopped;
                }
            }

            PublishStoppedOnce();
        }
    }

    private void SubscribeEvents()
    {
        _chunkCommitNotifier.ChunkCommitted += OnChunkCommitted;
    }

    private void UnsubscribeEvents()
    {
        _chunkCommitNotifier.ChunkCommitted -= OnChunkCommitted;
    }

    private void OnChunkCommitted(
        object? sender,
        CaptureChunkCommittedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        SignalWake();
    }

    private void SignalWake()
    {
        lock (_sync)
        {
            if (_state != RunnerState.Running)
            {
                return;
            }
        }

        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void PublishStoppedOnce()
    {
        if (Interlocked.Exchange(ref _stoppedStatusPublished, 1) == 0)
        {
            _statusSource.PublishStopped();
        }
    }

    private static Func<CancellationToken, Task<AnalysisPipelineRunSummary>>
        CreateRunOnceDelegate(AnalysisPipelineSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        return supervisor.RunOnceAsync;
    }

    private static Func<TimeSpan, CancellationToken, Task> CreateDelayDelegate(
        TimeProvider? timeProvider)
    {
        var provider = timeProvider ?? TimeProvider.System;
        return (delay, cancellationToken) =>
            Task.Delay(delay, provider, cancellationToken);
    }

    private enum RunnerState
    {
        Created = 0,
        Running = 1,
        Stopping = 2,
        Stopped = 3,
        Disposed = 4,
    }
}
