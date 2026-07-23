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
        var completedJobCount = 0;
        var retryableFailureCount = 0;
        var terminalFailureCount = 0;
        var leaseLostCount = 0;

        for (var processedJobCount = 0;
             processedJobCount < _options.MaximumJobsPerRun;
             processedJobCount++)
        {
            var processResult = await _jobProcessor
                .ProcessNextAsync(cancellationToken)
                .ConfigureAwait(false);
            switch (processResult.Status)
            {
                case AnalysisJobProcessStatus.NotReady:
                case AnalysisJobProcessStatus.NoWork:
                    return CreateSummary(
                        recoveredLeaseCount,
                        ingestion,
                        processedJobCount,
                        completedJobCount,
                        retryableFailureCount,
                        terminalFailureCount,
                        leaseLostCount,
                        moreWorkPossible: false);
                case AnalysisJobProcessStatus.Completed:
                    completedJobCount++;
                    break;
                case AnalysisJobProcessStatus.FailedRetryable:
                    retryableFailureCount++;
                    break;
                case AnalysisJobProcessStatus.FailedTerminal:
                    terminalFailureCount++;
                    break;
                case AnalysisJobProcessStatus.LeaseLost:
                    leaseLostCount++;
                    break;
                default:
                    throw new InvalidOperationException(
                        "The analysis job processor returned an unsupported status.");
            }
        }

        return CreateSummary(
            recoveredLeaseCount,
            ingestion,
            _options.MaximumJobsPerRun,
            completedJobCount,
            retryableFailureCount,
            terminalFailureCount,
            leaseLostCount,
            moreWorkPossible: true);
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
