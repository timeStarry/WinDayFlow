using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Application.Tests;

public sealed class TimelineQueryServiceTests
{
    private static readonly DateTimeOffset DayStart =
        new(2026, 7, 15, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task GetForDayAsyncRequestsDateAndReturnsDeterministicOrder()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var thirdId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var repository = new RecordingRepository(
        [
            CreateEntry(thirdId, DayStart.AddHours(10), TimeSpan.FromMinutes(45)),
            CreateEntry(secondId, DayStart.AddHours(9), TimeSpan.FromMinutes(60)),
            CreateEntry(firstId, DayStart.AddHours(9), TimeSpan.FromMinutes(60)),
        ]);
        var service = new TimelineQueryService(repository);
        var requestedDate = new DateOnly(2026, 7, 15);
        using var cancellation = new CancellationTokenSource();

        var entries = await service.GetForDayAsync(requestedDate, cancellation.Token);

        Assert.Equal([firstId, secondId, thirdId], entries.Select(static entry => entry.Id));
        Assert.Equal(requestedDate, repository.RequestedDate);
        Assert.Equal(cancellation.Token, repository.CancellationToken);
        Assert.Equal(1, repository.CallCount);
    }

    [Fact]
    public async Task GetForDayAsyncUsesEndThenIdentifierToBreakEqualStartTies()
    {
        var shortestId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var longerLowId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var longerHighId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var repository = new RecordingRepository(
        [
            CreateEntry(longerHighId, DayStart.AddHours(9), TimeSpan.FromMinutes(60)),
            CreateEntry(longerLowId, DayStart.AddHours(9), TimeSpan.FromMinutes(60)),
            CreateEntry(shortestId, DayStart.AddHours(9), TimeSpan.FromMinutes(30)),
        ]);
        var service = new TimelineQueryService(repository);

        var entries = await service.GetForDayAsync(new DateOnly(2026, 7, 15));

        Assert.Equal(
            [shortestId, longerLowId, longerHighId],
            entries.Select(static entry => entry.Id));
    }

    private static TimelineEntry CreateEntry(Guid id, DateTimeOffset start, TimeSpan duration)
    {
        return TimelineEntry.FromActivity(
            id,
            new Activity(
                new TimeRange(start, start.Add(duration)),
                $"Entry {id}",
                string.Empty,
                ActivityCategory.Unknown,
                ProductivityKind.Unknown,
                [],
                [],
                0.5,
                new EvidenceReference(id.ToString("N"), $"evidence/{id:N}.mp4")),
            "analysis-v1");
    }

    private sealed class RecordingRepository(IReadOnlyList<TimelineEntry> entries) : ITimelineRepository
    {
        public DateOnly? RequestedDate { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
            DateOnly day,
            CancellationToken cancellationToken = default)
        {
            RequestedDate = day;
            CancellationToken = cancellationToken;
            CallCount++;

            return Task.FromResult(entries);
        }
    }
}
