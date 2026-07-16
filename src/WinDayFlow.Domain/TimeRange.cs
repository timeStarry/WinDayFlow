namespace WinDayFlow.Domain;

public sealed record TimeRange
{
    public TimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                end,
                "The end of a time range must be later than its start.");
        }

        Start = start;
        End = end;
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public TimeSpan Duration => End - Start;

    public bool Contains(DateTimeOffset instant)
    {
        return instant >= Start && instant < End;
    }

    public bool Overlaps(TimeRange other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Start < other.End && other.Start < End;
    }
}
