namespace WinDayFlow.Application.Settings;

public sealed record RecordingConsent(
    int PolicyVersion,
    DateTimeOffset AcceptedAtUtc)
{
    public int PolicyVersion { get; } = ValidatePolicyVersion(PolicyVersion);

    public DateTimeOffset AcceptedAtUtc { get; } = ValidateAcceptedAtUtc(AcceptedAtUtc);

    private static int ValidatePolicyVersion(int policyVersion)
    {
        if (policyVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policyVersion),
                policyVersion,
                "The recording consent policy version must be positive.");
        }

        return policyVersion;
    }

    private static DateTimeOffset ValidateAcceptedAtUtc(DateTimeOffset acceptedAtUtc)
    {
        if (acceptedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The recording consent acceptance time must use the UTC offset.",
                nameof(acceptedAtUtc));
        }

        return acceptedAtUtc;
    }
}
