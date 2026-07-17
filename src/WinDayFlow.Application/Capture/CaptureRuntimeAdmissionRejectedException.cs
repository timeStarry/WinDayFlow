namespace WinDayFlow.Application.Capture;

public sealed class CaptureRuntimeAdmissionRejectedException : InvalidOperationException
{
    public CaptureRuntimeAdmissionRejectedException()
        : base("The capture command admission was rejected.")
    {
    }
}
