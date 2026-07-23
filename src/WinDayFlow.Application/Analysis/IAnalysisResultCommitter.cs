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

public enum AnalysisResultCommitStatus
{
    Committed = 0,
    LeaseLost = 1,
    CloudAnalysisDisabled = 2,
    ProviderRevisionChanged = 3,
    EntryConflict = 4,
}
