using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public enum UnprocessedIntervalState
{
    LocalOnly = 0,
    Queued = 1,
    Processing = 2,
    RetryScheduled = 3,
    Failed = 4,
    Cancelled = 5,
}

public sealed record UnprocessedInterval
{
    public UnprocessedInterval(
        string captureChunkId,
        TimeRange range,
        UnprocessedIntervalState state,
        Guid? latestJobId,
        int? attempt,
        AnalysisJobErrorCode? errorCode)
    {
        CaptureChunk.ValidateIdentifier(captureChunkId);
        ArgumentNullException.ThrowIfNull(range);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (state == UnprocessedIntervalState.LocalOnly)
        {
            if (latestJobId.HasValue || attempt.HasValue || errorCode.HasValue)
            {
                throw new ArgumentException(
                    "A local-only interval cannot contain analysis job metadata.",
                    nameof(latestJobId));
            }
        }
        else
        {
            if (latestJobId is null || latestJobId == Guid.Empty || attempt is null or < 0)
            {
                throw new ArgumentException(
                    "A queued or attempted interval requires valid analysis job metadata.",
                    nameof(latestJobId));
            }

            if (state == UnprocessedIntervalState.Queued && attempt != 0
                || state is UnprocessedIntervalState.Processing
                    or UnprocessedIntervalState.RetryScheduled
                    or UnprocessedIntervalState.Failed
                    && attempt == 0)
            {
                throw new ArgumentException(
                    "The interval attempt does not match its analysis state.",
                    nameof(attempt));
            }

            var failed = state is UnprocessedIntervalState.RetryScheduled
                or UnprocessedIntervalState.Failed;
            if (failed != errorCode.HasValue
                || errorCode.HasValue
                    && (!Enum.IsDefined(errorCode.Value)
                        || errorCode == AnalysisJobErrorCode.None))
            {
                throw new ArgumentException(
                    "The interval error code does not match its analysis state.",
                    nameof(errorCode));
            }
        }

        CaptureChunkId = captureChunkId;
        Range = range;
        State = state;
        LatestJobId = latestJobId;
        Attempt = attempt;
        ErrorCode = errorCode;
    }

    public string CaptureChunkId { get; }

    public TimeRange Range { get; }

    public UnprocessedIntervalState State { get; }

    public Guid? LatestJobId { get; }

    public int? Attempt { get; }

    public AnalysisJobErrorCode? ErrorCode { get; }
}
