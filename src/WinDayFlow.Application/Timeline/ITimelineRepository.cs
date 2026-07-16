using WinDayFlow.Domain;

namespace WinDayFlow.Application.Timeline;

public interface ITimelineRepository
{
    Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
        DateOnly day,
        CancellationToken cancellationToken = default);
}
