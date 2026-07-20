namespace WinDayFlow.Capture.Interop;

public sealed class NativeCaptureException : InvalidOperationException
{
    internal NativeCaptureException(NativeCaptureResult result, string operation)
        : base($"Native capture operation '{operation}' failed with result {(int)result} ({result}).")
    {
        ResultCode = (int)result;
        Operation = operation;
    }

    public int ResultCode { get; }

    public string Operation { get; }
}
