using WinDayFlow.Domain;

namespace WinDayFlow.Application.Timeline;

public sealed class TimelineQueryService
{
    private readonly ITimelineRepository _repository;

    public TimelineQueryService(ITimelineRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
        DateOnly day,
        CancellationToken cancellationToken = default)
    {
        var entries = await _repository
            .GetForDayAsync(day, cancellationToken)
            .ConfigureAwait(false);

        return entries
            .OrderBy(static entry => entry.Range.Start)
            .ThenBy(static entry => entry.Range.End)
            .ThenBy(static entry => entry.Id)
            .ToArray();
    }
}
