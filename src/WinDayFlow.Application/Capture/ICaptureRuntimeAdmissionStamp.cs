namespace WinDayFlow.Application.Capture;

/// <summary>
/// Represents an opaque, single-use authorization to admit one capture command.
/// </summary>
public interface ICaptureRuntimeAdmissionStamp
{
    long InvalidationGeneration { get; }
}
