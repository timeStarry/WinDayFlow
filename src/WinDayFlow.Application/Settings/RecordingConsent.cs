namespace WinDayFlow.Application.Settings;

public sealed record RecordingConsent(
    int PolicyVersion,
    DateTimeOffset AcceptedAtUtc)
{
    public int PolicyVersion { get; } = PolicyVersion > 0
        ? PolicyVersion
        : throw new ArgumentOutOfRangeException(
            nameof(PolicyVersion),
            PolicyVersion,
            "The recording consent policy version must be positive.");

    public DateTimeOffset AcceptedAtUtc { get; } = AcceptedAtUtc.Offset == TimeSpan.Zero
        ? AcceptedAtUtc
        : throw new ArgumentException(
            "The recording consent acceptance time must use the UTC offset.",
            nameof(AcceptedAtUtc));
}
