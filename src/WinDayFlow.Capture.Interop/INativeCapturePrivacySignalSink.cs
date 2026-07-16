namespace WinDayFlow.Capture.Interop;

public interface INativeCapturePrivacySignalSink
{
    /// <summary>
    /// Applies one atomically observed privacy and target snapshot. Producers must clear
    /// the target when foreground identity changes or cannot be revalidated.
    /// </summary>
    Task UpdateSignalsAsync(
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default);
}
