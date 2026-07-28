namespace WinDayFlow.Capture.Interop;

internal interface INativeCapturePrivacySignalSinkTermination
{
    /// <summary>
    /// Gets whether a single shared termination operation has started.
    /// </summary>
    bool IsTerminationStarted { get; }

    /// <summary>
    /// Gets the shared termination operation. Successful completion proves that
    /// capture was quiesced fail-closed and all native runtime resources were stopped
    /// and destroyed; cancellation or failure does not prove a safe shutdown.
    /// </summary>
    Task Termination { get; }
}
