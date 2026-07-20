using WinDayFlow.Infrastructure.Timeline;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Timeline;

public sealed class DevelopmentTimelineRepositoryTests
{
    private readonly DevelopmentTimelineRepository _repository = new();

    [Fact]
    public async Task GetForDayAsyncReturnsFourOrderedClearlyLabeledSamples()
    {
        var entries = await _repository.GetForDayAsync(DevelopmentTimelineRepository.SampleDate);

        Assert.Equal(4, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.StartsWith("[样例]", entry.Title, StringComparison.Ordinal);
            Assert.Contains("sample", entry.Tags, StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(entry.Evidence);
            Assert.StartsWith(
                "sample-chunk-",
                entry.Evidence!.CaptureChunkId,
                StringComparison.Ordinal);
        });
        Assert.Equal(entries.OrderBy(entry => entry.Range.Start), entries);
    }

    [Fact]
    public async Task GetForDayAsyncReturnsNoEntriesForAnotherDay()
    {
        var entries = await _repository.GetForDayAsync(
            DevelopmentTimelineRepository.SampleDate.AddDays(1));

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetForDayAsyncHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _repository.GetForDayAsync(
                DevelopmentTimelineRepository.SampleDate,
                cancellation.Token));
    }
}
