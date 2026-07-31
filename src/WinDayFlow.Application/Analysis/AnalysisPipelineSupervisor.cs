using WinDayFlow.Application.Ai;

namespace WinDayFlow.Application.Analysis;

public sealed record AnalysisPipelineSupervisorOptions
{
    public const int MaximumJobsPerRunLimit = 1_000;

    public AnalysisPipelineSupervisorOptions(
        TimeSpan recoveryRetryDelay,
        int maximumJobsPerRun)
    {
        if (recoveryRetryDelay < TimeSpan.Zero
            || recoveryRetryDelay > MaximumRecoveryRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryRetryDelay));
        }

        if (maximumJobsPerRun is <= 0 or > MaximumJobsPerRunLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumJobsPerRun));
        }

        RecoveryRetryDelay = recoveryRetryDelay;
        MaximumJobsPerRun = maximumJobsPerRun;
    }

    public static TimeSpan MaximumRecoveryRetryDelay { get; } = TimeSpan.FromDays(1);

    public TimeSpan RecoveryRetryDelay { get; }

    public int MaximumJobsPerRun { get; }

    public static AnalysisPipelineSupervisorOptions Default { get; } = new(
        recoveryRetryDelay: TimeSpan.FromMinutes(1),
        maximumJobsPerRun: 32);
}

public sealed record AnalysisPipelineRunSummary(
    int RecoveredLeaseCount,
    CaptureAnalysisIngestionResult Ingestion,
    int ProcessedJobCount,
    int CompletedJobCount,
    int RetryableFailureCount,
    int TerminalFailureCount,
    int LeaseLostCount,
    bool MoreWorkPossible);

internal sealed record AnalysisPipelineDrainSummary(
    int ProcessedJobCount,
    int CompletedJobCount,
    int RetryableFailureCount,
    int TerminalFailureCount,
    int LeaseLostCount,
    bool MoreWorkPossible);

public sealed class AnalysisPipelineSupervisor
{
    private readonly IAnalysisJobStore _jobStore;
    private readonly CaptureAnalysisIngestionService _ingestionService;
    private readonly AnalysisJobProcessor _jobProcessor;
    private readonly AnalysisPipelineSupervisorOptions _options;
    private readonly TimeProvider _timeProvider;

    public AnalysisPipelineSupervisor(
        IAnalysisJobStore jobStore,
        CaptureAnalysisIngestionService ingestionService,
        AnalysisJobProcessor jobProcessor,
        AnalysisPipelineSupervisorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _ingestionService = ingestionService
            ?? throw new ArgumentNullException(nameof(ingestionService));
        _jobProcessor = jobProcessor ?? throw new ArgumentNullException(nameof(jobProcessor));
        _options = options ?? AnalysisPipelineSupervisorOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AnalysisPipelineRunSummary> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recoveredLeaseCount = await _jobStore
            .RecoverExpiredLeasesAsync(
                _timeProvider.GetUtcNow().ToUniversalTime(),
                _options.RecoveryRetryDelay,
                cancellationToken)
            .ConfigureAwait(false);
        if (recoveredLeaseCount < 0)
        {
            throw new InvalidOperationException(
                "The analysis job store returned a negative recovered lease count.");
        }

        var ingestion = await _ingestionService
            .ReconcileAsync(cancellationToken)
            .ConfigureAwait(false);
        var maximumConcurrency = await _jobProcessor
            .GetMaximumConcurrencyAsync(cancellationToken)
            .ConfigureAwait(false);
        var drain = await DrainAsync(
                _jobProcessor.ProcessNextAsync,
                _options.MaximumJobsPerRun,
                maximumConcurrency,
                cancellationToken)
            .ConfigureAwait(false);

        return CreateSummary(
            recoveredLeaseCount,
            ingestion,
            drain.ProcessedJobCount,
            drain.CompletedJobCount,
            drain.RetryableFailureCount,
            drain.TerminalFailureCount,
            drain.LeaseLostCount,
            drain.MoreWorkPossible);
    }

    internal static async Task<AnalysisPipelineDrainSummary> DrainAsync(
        Func<CancellationToken, Task<AnalysisJobProcessResult>> processNextAsync,
        int maximumJobs,
        int maximumConcurrency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processNextAsync);
        if (maximumJobs is <= 0 or > AnalysisPipelineSupervisorOptions.MaximumJobsPerRunLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumJobs));
        }
        if (maximumConcurrency is < 1 or > AiProviderProfile.MaximumConcurrencyLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        var reservedSlots = 0;
        var stopRequested = 0;
        var processedJobCount = 0;
        var completedJobCount = 0;
        var retryableFailureCount = 0;
        var terminalFailureCount = 0;
        var leaseLostCount = 0;

        async Task RunWorkerAsync()
        {
            while (Volatile.Read(ref stopRequested) == 0)
            {
                var slot = Interlocked.Increment(ref reservedSlots);
                if (slot > maximumJobs)
                {
                    return;
                }

                var result = await processNextAsync(cancellationToken)
                    .ConfigureAwait(false);
                switch (result.Status)
                {
                    case AnalysisJobProcessStatus.NotReady:
                    case AnalysisJobProcessStatus.NoWork:
                        Volatile.Write(ref stopRequested, 1);
                        return;
                    case AnalysisJobProcessStatus.Completed:
                        Interlocked.Increment(ref completedJobCount);
                        break;
                    case AnalysisJobProcessStatus.FailedRetryable:
                        Interlocked.Increment(ref retryableFailureCount);
                        break;
                    case AnalysisJobProcessStatus.FailedTerminal:
                        Interlocked.Increment(ref terminalFailureCount);
                        break;
                    case AnalysisJobProcessStatus.LeaseLost:
                        Interlocked.Increment(ref leaseLostCount);
                        break;
                    default:
                        throw new InvalidOperationException(
                            "The analysis job processor returned an unsupported status.");
                }

                Interlocked.Increment(ref processedJobCount);
            }
        }

        var workerCount = Math.Min(maximumJobs, maximumConcurrency);
        await Task.WhenAll(Enumerable.Range(0, workerCount)
                .Select(_ => RunWorkerAsync()))
            .ConfigureAwait(false);
        return new AnalysisPipelineDrainSummary(
            processedJobCount,
            completedJobCount,
            retryableFailureCount,
            terminalFailureCount,
            leaseLostCount,
            MoreWorkPossible: processedJobCount == maximumJobs
                && Volatile.Read(ref stopRequested) == 0);
    }

    private static AnalysisPipelineRunSummary CreateSummary(
        int recoveredLeaseCount,
        CaptureAnalysisIngestionResult ingestion,
        int processedJobCount,
        int completedJobCount,
        int retryableFailureCount,
        int terminalFailureCount,
        int leaseLostCount,
        bool moreWorkPossible) => new(
            recoveredLeaseCount,
            ingestion,
            processedJobCount,
            completedJobCount,
            retryableFailureCount,
            terminalFailureCount,
            leaseLostCount,
            moreWorkPossible);
}
