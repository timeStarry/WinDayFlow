using Xunit;

namespace WinDayFlow.Domain.Tests;

public sealed class TimeRangeTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 15, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void ConstructorRejectsEmptyOrReversedRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(Start, Start));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(Start, Start.AddTicks(-1)));
    }

    [Fact]
    public void RangeIsHalfOpenAndReportsElapsedDuration()
    {
        var range = new TimeRange(Start, Start.AddMinutes(45));

        Assert.Equal(TimeSpan.FromMinutes(45), range.Duration);
        Assert.True(range.Contains(Start));
        Assert.True(range.Contains(Start.AddMinutes(44)));
        Assert.False(range.Contains(range.End));
    }

    [Fact]
    public void OverlapsDoesNotTreatAdjacentRangesAsOverlapping()
    {
        var first = new TimeRange(Start, Start.AddMinutes(30));
        var adjacent = new TimeRange(first.End, first.End.AddMinutes(30));
        var overlapping = new TimeRange(first.End.AddMinutes(-1), first.End.AddMinutes(30));

        Assert.False(first.Overlaps(adjacent));
        Assert.True(first.Overlaps(overlapping));
        Assert.True(overlapping.Overlaps(first));
    }
}
