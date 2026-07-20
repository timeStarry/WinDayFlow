namespace WinDayFlow.Application.Capture;

public sealed class CaptureStatusChangedEventArgs : EventArgs
{
    public CaptureStatusChangedEventArgs(CaptureStatus previous, CaptureStatus current)
    {
        Previous = previous ?? throw new ArgumentNullException(nameof(previous));
        Current = current ?? throw new ArgumentNullException(nameof(current));

        if (Previous.Sequence > 0 && Current.Sequence <= Previous.Sequence)
        {
            throw new ArgumentException(
                "A sequenced capture status must advance the event sequence.",
                nameof(current));
        }
    }

    public CaptureStatus Previous { get; }

    public CaptureStatus Current { get; }
}
