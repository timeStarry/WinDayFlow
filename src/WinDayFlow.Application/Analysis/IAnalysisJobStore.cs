using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public interface IAnalysisJobStore
{
    Task<AnalysisJobEnqueueResult> EnqueueAsync(
        AnalysisJob pendingJob,
        CancellationToken cancellationToken = default);

    Task<AnalysisJob?> GetAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<bool> HasCompletedAnalysisAsync(
        string captureChunkId,
        string analysisVersion,
        string inputFingerprint,
        CancellationToken cancellationToken = default);

    Task<AnalysisJob?> TryClaimNextAsync(
        string leaseOwner,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<AnalysisJob?> TryTransitionAsync(
        AnalysisJobLease lease,
        AnalysisJobState expectedState,
        AnalysisJobState nextState,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default);

    Task<AnalysisJob?> TryRenewLeaseAsync(
        AnalysisJobLease lease,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset newExpiresAtUtc,
        CancellationToken cancellationToken = default);

    Task<AnalysisJob?> TryFailAsync(
        AnalysisJobLease lease,
        AnalysisJobFailure failure,
        AnalysisFailureDisposition disposition,
        DateTimeOffset failedAtUtc,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default);

    Task<AnalysisJob?> TryCancelAsync(
        Guid jobId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default);

    Task<int> RecoverExpiredLeasesAsync(
        DateTimeOffset recoveredAtUtc,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default);
}

public sealed record AnalysisJobEnqueueResult(AnalysisJob Job, bool Created);
