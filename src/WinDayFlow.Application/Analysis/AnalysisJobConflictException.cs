namespace WinDayFlow.Application.Analysis;

public sealed class AnalysisJobConflictException : InvalidOperationException
{
    public AnalysisJobConflictException(Guid jobId)
        : base($"Analysis job '{jobId:D}' conflicts with an existing durable job.")
    {
        JobId = jobId;
    }

    public Guid JobId { get; }
}
