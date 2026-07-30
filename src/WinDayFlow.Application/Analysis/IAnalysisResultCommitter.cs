using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public interface IAnalysisResultCommitter
{
    Task<AnalysisResultCommitStatus> TryCommitAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisWindowResultCommitter : IAnalysisResultCommitter
{
    Task<AnalysisResultCommitStatus> TryCommitWindowAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        AnalysisWindowSnapshot window,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisStageAwareResultCommitter : IAnalysisResultCommitter
{
    Task<AnalysisResultCommitStatus> TryCommitAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisStageAwareWindowResultCommitter :
    IAnalysisStageAwareResultCommitter,
    IAnalysisWindowResultCommitter
{
    Task<AnalysisResultCommitStatus> TryCommitWindowAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        AnalysisWindowSnapshot window,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);
}

public enum AnalysisResultCommitStatus
{
    Committed = 0,
    LeaseLost = 1,
    CloudAnalysisDisabled = 2,
    ProviderRevisionChanged = 3,
    EntryConflict = 4,
    WindowChanged = 5,
}
