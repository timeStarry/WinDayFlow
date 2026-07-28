using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public enum AnalysisJobRetryOutcome
{
    Scheduled = 0,
    AlreadyScheduled = 1,
    NotFound = 2,
    StateNotRetryable = 3,
    StaleJob = 4,
    EvidenceUnavailable = 5,
    AnalysisAlreadyCompleted = 6,
    AttemptLimitReached = 7,
}

public sealed record AnalysisJobRetryResult(
    AnalysisJobRetryOutcome Outcome,
    AnalysisJob? Job)
{
    public bool Accepted => Outcome is
        AnalysisJobRetryOutcome.Scheduled or
        AnalysisJobRetryOutcome.AlreadyScheduled;
}

public sealed class AnalysisJobRetryService
{
    private readonly IAnalysisJobStore _jobStore;
    private readonly IAnalysisPipelineScheduler _scheduler;
    private readonly TimeProvider _timeProvider;

    public AnalysisJobRetryService(
        IAnalysisJobStore jobStore,
        IAnalysisPipelineScheduler scheduler,
        TimeProvider? timeProvider = null)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AnalysisJobRetryResult> RetryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException(
                "An analysis job identifier cannot be empty.",
                nameof(jobId));
        }

        var result = await _jobStore
            .TryRetryAsync(
                jobId,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Accepted)
        {
            _scheduler.RequestRun();
        }

        return result;
    }
}
