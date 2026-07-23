namespace WinDayFlow.Domain;

public sealed record AnalysisJobLease
{
    public const int MaximumOwnerLength = 128;

    public AnalysisJobLease(
        Guid jobId,
        string owner,
        string token,
        int attempt,
        DateTimeOffset expiresAtUtc)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("An analysis lease requires a job identifier.", nameof(jobId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > MaximumOwnerLength
            || !string.Equals(owner, owner.Trim(), StringComparison.Ordinal)
            || owner.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The analysis lease owner is invalid.", nameof(owner));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length != 32
            || token.Any(static character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "An analysis lease token must be a canonical 128-bit lowercase hexadecimal value.",
                nameof(token));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        JobId = jobId;
        Owner = owner;
        Token = token;
        Attempt = attempt;
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
    }

    public Guid JobId { get; }

    public string Owner { get; }

    public string Token { get; }

    public int Attempt { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}
