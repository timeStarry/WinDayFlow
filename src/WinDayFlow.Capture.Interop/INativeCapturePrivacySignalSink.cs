namespace WinDayFlow.Capture.Interop;

public interface INativeCapturePrivacySignalSink
{
    /// <summary>
    /// Gets the generation of the currently valid privacy observation.
    /// </summary>
    long PrivacyObservationGeneration { get; }

    /// <summary>
    /// Synchronously invalidates the current observation and closes managed admission.
    /// The returned generation must be used for the corresponding native barrier and
    /// any subsequently resolved signals.
    /// </summary>
    long InvalidatePrivacyObservation();

    /// <summary>
    /// Forces the fail-closed observation for <paramref name="privacyObservationGeneration"/>
    /// across the native runtime and persistence boundary. Reapplying the barrier for the
    /// same current generation is idempotent and must not make that generation publishable
    /// again after its resolved signals have already been consumed.
    /// </summary>
    Task ApplyPrivacyInvalidationAsync(
        long privacyObservationGeneration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebinds an already authorized local capture session to a newly verified
    /// focused-application display without publishing a fail-closed privacy state.
    /// Hard-gate changes must continue to use the invalidation protocol above.
    /// </summary>
    Task<bool> TryRebindTargetAsync(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies resolved signals only while their observation generation remains current,
    /// its native invalidation barrier has completed, and no resolved snapshot has already
    /// been published for that generation.
    /// </summary>
    Task<bool> TryUpdateSignalsAsync(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one atomically observed privacy and target snapshot. Producers must clear
    /// the target when foreground identity changes or cannot be revalidated.
    /// This compatibility path is available only before the first explicit observation
    /// invalidation activates the generation-bound protocol.
    /// </summary>
    Task UpdateSignalsAsync(
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default);
}
