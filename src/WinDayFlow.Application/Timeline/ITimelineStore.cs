using WinDayFlow.Domain;

namespace WinDayFlow.Application.Timeline;

public interface ITimelineStore : ITimelineRepository
{
    Task<TimelineEntry?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        TimelineEntry entry,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        TimelineEntry entry,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
