#if WDF_DEV_LIVE_CAPTURE
namespace WinDayFlow.Capture.Interop;

public static class DevLiveCapturePrivacy
{
    public static WindowsCapturePrivacyMonitor CreateMonitor(
        INativeCapturePrivacySignalSink sink,
        string storageDirectory,
        ulong minimumStorageHeadroomBytes)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var baseSampler = new WindowsCapturePrivacySampler(
            storageDirectory,
            minimumStorageHeadroomBytes);
        return new WindowsCapturePrivacyMonitor(
            sink,
            new DevLiveQaWindowsCapturePrivacySampler(baseSampler),
            new WindowsCaptureWinEventSource());
    }
}

internal sealed class DevLiveQaWindowsCapturePrivacySampler
    : IWindowsCapturePrivacySampler,
      IWindowsCaptureStorageSampler
{
    private const string WinDayFlowExecutableName = "WinDayFlow.App.exe";
    private const string WindowsLockScreenExecutableName = "LockApp.exe";

    private readonly IWindowsCapturePrivacySampler _inner;

    internal DevLiveQaWindowsCapturePrivacySampler(
        IWindowsCapturePrivacySampler inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void InvalidateTargetObservation()
    {
        _inner.InvalidateTargetObservation();
    }

    public async ValueTask<WindowsCapturePrivacyObservation> SampleAsync(
        CancellationToken cancellationToken)
    {
        var observation = await _inner
            .SampleAsync(cancellationToken)
            .ConfigureAwait(false);
        return ApplyPolicy(observation);
    }

    ValueTask<NativeCapturePolicyDecision>
        IWindowsCaptureStorageSampler.SampleStorageAsync(
            CancellationToken cancellationToken)
    {
        return _inner is IWindowsCaptureStorageSampler storageSampler
            ? storageSampler.SampleStorageAsync(cancellationToken)
            : ValueTask.FromResult(NativeCapturePolicyDecision.Unknown);
    }

    internal static WindowsCapturePrivacyObservation ApplyPolicy(
        WindowsCapturePrivacyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var signals = observation.Signals;
        var identity = signals.CaptureIdentity;
        var executable = identity.ExecutableNameObservation;

        if (HasTargetPolicyBlock(signals))
        {
            return observation;
        }

        if (IsAlwaysBlockedProcess(executable))
        {
            return WithApplicationAndWindowPolicy(
                observation,
                NativeCapturePolicyDecision.Block,
                NativeCapturePolicyDecision.Block);
        }

        if (signals.Target.State == NativeCaptureTargetIdentityState.Absent
            && observation.DisplayTarget.State
                == WindowsCaptureDisplayTargetState.Absent)
        {
            return observation;
        }

        var targetUnresolved =
            signals.Target.State != NativeCaptureTargetIdentityState.Present
            || observation.DisplayTarget.State
                != WindowsCaptureDisplayTargetState.Present
            || !IsPresentValue(executable);
        if (targetUnresolved)
        {
            return HasIndependentBlockingDecision(signals)
                ? observation
                : WindowsCapturePrivacyObservation.FailClosed;
        }

        // This admission exists only in a dev-live build selected by the App's explicit
        // activation argument. A stable external process and display are enough for QA;
        // optional identity fields remain untouched so configured exclusion rules can
        // still fail closed when a field required by a rule could not be observed.
        // This is not application trust or signer proof and must never ship in production.
        // Every non-target privacy signal is retained for the normal policy composer.
        return WithApplicationAndWindowPolicy(
            observation,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow);
    }

    private static WindowsCapturePrivacyObservation WithApplicationAndWindowPolicy(
        WindowsCapturePrivacyObservation observation,
        NativeCapturePolicyDecision applicationAllowed,
        NativeCapturePolicyDecision windowAllowed)
    {
        var signals = observation.Signals;
        var updatedSignals = new NativeCapturePrivacySignals(
            signals.SessionUnlocked,
            signals.SecureDesktopClear,
            signals.RemoteSession,
            signals.PresentationMode,
            applicationAllowed,
            windowAllowed,
            signals.StorageAvailable,
            signals.CaptureIdentity,
            signals.Target);
        return new WindowsCapturePrivacyObservation(
            updatedSignals,
            observation.DisplayTarget);
    }

    private static bool IsPresentValue(NativeCaptureObservation observation) =>
        observation.State == NativeCaptureObservationState.Present
        && !string.IsNullOrWhiteSpace(observation.Value);

    private static bool IsAlwaysBlockedProcess(
        NativeCaptureObservation executable) =>
        executable.State == NativeCaptureObservationState.Present
        && (string.Equals(
                executable.Value,
                WinDayFlowExecutableName,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                executable.Value,
                WindowsLockScreenExecutableName,
                StringComparison.OrdinalIgnoreCase));

    private static bool HasTargetPolicyBlock(
        NativeCapturePrivacySignals signals) =>
        signals.ApplicationAllowed == NativeCapturePolicyDecision.Block
        || signals.WindowAllowed == NativeCapturePolicyDecision.Block;

    private static bool HasIndependentBlockingDecision(
        NativeCapturePrivacySignals signals) =>
        signals.SessionUnlocked == NativeCapturePolicyDecision.Block
        || signals.SecureDesktopClear == NativeCapturePolicyDecision.Block
        || signals.StorageAvailable == NativeCapturePolicyDecision.Block;
}
#endif
