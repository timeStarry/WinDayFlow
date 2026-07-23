namespace WinDayFlow.Domain;

public enum AnalysisJobState
{
    Pending = 0,
    Claimed = 1,
    Extracting = 2,
    Observing = 3,
    Summarizing = 4,
    Committing = 5,
    Completed = 6,
    FailedRetryable = 7,
    FailedTerminal = 8,
    Cancelled = 9,
}
