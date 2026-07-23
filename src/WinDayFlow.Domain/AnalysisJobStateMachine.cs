namespace WinDayFlow.Domain;

public static class AnalysisJobStateMachine
{
    public static bool IsActive(AnalysisJobState state) => state is
        AnalysisJobState.Claimed
        or AnalysisJobState.Extracting
        or AnalysisJobState.Observing
        or AnalysisJobState.Summarizing
        or AnalysisJobState.Committing;

    public static bool IsTerminal(AnalysisJobState state) => state is
        AnalysisJobState.Completed
        or AnalysisJobState.FailedTerminal
        or AnalysisJobState.Cancelled;

    public static bool CanTransition(AnalysisJobState current, AnalysisJobState next)
    {
        if (!Enum.IsDefined(current) || !Enum.IsDefined(next))
        {
            return false;
        }

        return (current, next) switch
        {
            (AnalysisJobState.Pending, AnalysisJobState.Claimed) => true,
            (AnalysisJobState.Pending, AnalysisJobState.Cancelled) => true,
            (AnalysisJobState.FailedRetryable, AnalysisJobState.Claimed) => true,
            (AnalysisJobState.FailedRetryable, AnalysisJobState.Cancelled) => true,
            (AnalysisJobState.Claimed, AnalysisJobState.Extracting) => true,
            (AnalysisJobState.Extracting, AnalysisJobState.Observing) => true,
            (AnalysisJobState.Observing, AnalysisJobState.Summarizing) => true,
            (AnalysisJobState.Summarizing, AnalysisJobState.Committing) => true,
            (AnalysisJobState.Committing, AnalysisJobState.Completed) => true,
            (_, AnalysisJobState.FailedRetryable) when IsActive(current) => true,
            (_, AnalysisJobState.FailedTerminal) when IsActive(current) => true,
            _ => false,
        };
    }
}
