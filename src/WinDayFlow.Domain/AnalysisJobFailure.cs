namespace WinDayFlow.Domain;

public sealed record AnalysisJobFailure
{
    public const int MaximumDetailLength = 1000;

    public AnalysisJobFailure(AnalysisJobErrorCode code, string? detail = null)
    {
        if (!Enum.IsDefined(code) || code == AnalysisJobErrorCode.None)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        if (detail is not null
            && (detail.Length > MaximumDetailLength
                || detail.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Analysis failure detail must contain at most {MaximumDetailLength} safe characters.",
                nameof(detail));
        }

        Code = code;
        Detail = detail;
    }

    public AnalysisJobErrorCode Code { get; }

    public string? Detail { get; }
}
