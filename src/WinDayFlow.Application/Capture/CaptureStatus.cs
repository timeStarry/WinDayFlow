namespace WinDayFlow.Application.Capture;

public sealed record CaptureStatus
{
    public CaptureStatus(
        CaptureState State,
        DateTimeOffset ChangedAt,
        string? Detail = null,
        ulong Sequence = 0,
        CaptureReasonCode Reason = CaptureReasonCode.None,
        CaptureErrorCode ErrorCode = CaptureErrorCode.None)
    {
        if (!Enum.IsDefined(State))
        {
            throw new ArgumentOutOfRangeException(
                nameof(State),
                State,
                "Capture state is not defined.");
        }

        if (!Enum.IsDefined(Reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Reason),
                Reason,
                "Capture reason code is not defined.");
        }

        if (!Enum.IsDefined(ErrorCode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ErrorCode),
                ErrorCode,
                "Capture error code is not defined.");
        }

        if (State == CaptureState.Faulted && ErrorCode == CaptureErrorCode.None)
        {
            throw new ArgumentException(
                "A faulted capture status requires an error code.",
                nameof(ErrorCode));
        }

        if (State != CaptureState.Faulted && ErrorCode != CaptureErrorCode.None)
        {
            throw new ArgumentException(
                "An error code is only valid for a faulted capture status.",
                nameof(ErrorCode));
        }

        this.State = State;
        this.ChangedAt = ChangedAt;
        this.Detail = Detail;
        this.Sequence = Sequence;
        this.Reason = Reason;
        this.ErrorCode = ErrorCode;
    }

    public CaptureState State { get; }

    public DateTimeOffset ChangedAt { get; }

    public string? Detail { get; init; }

    public ulong Sequence { get; }

    public CaptureReasonCode Reason { get; }

    public CaptureErrorCode ErrorCode { get; }

    public bool IsOperational => State switch
    {
        CaptureState.Stopped => true,
        CaptureState.Starting => true,
        CaptureState.Recording => true,
        CaptureState.Pausing => true,
        CaptureState.Paused => true,
        CaptureState.Resuming => true,
        CaptureState.Stopping => true,
        CaptureState.Unavailable => false,
        CaptureState.Faulted => false,
        CaptureState.BlockedByConsent => false,
        CaptureState.NeedsAttention => false,
        _ => false,
    };

    public void Deconstruct(
        out CaptureState State,
        out DateTimeOffset ChangedAt,
        out string? Detail)
    {
        State = this.State;
        ChangedAt = this.ChangedAt;
        Detail = this.Detail;
    }
}
