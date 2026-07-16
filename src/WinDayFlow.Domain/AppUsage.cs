namespace WinDayFlow.Domain;

public sealed record AppUsage
{
    public AppUsage(string applicationId, string displayName, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Application usage duration cannot be negative.");
        }

        ApplicationId = applicationId;
        DisplayName = displayName;
        Duration = duration;
    }

    public string ApplicationId { get; }

    public string DisplayName { get; }

    public TimeSpan Duration { get; }
}
