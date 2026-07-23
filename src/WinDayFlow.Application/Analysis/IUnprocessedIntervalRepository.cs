using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public interface IUnprocessedIntervalRepository
{
    Task<IReadOnlyList<UnprocessedInterval>> GetForUtcRangeAsync(
        TimeRange utcRange,
        CancellationToken cancellationToken = default);
}
