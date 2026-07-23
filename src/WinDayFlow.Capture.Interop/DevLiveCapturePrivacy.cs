#if WDF_DEV_LIVE_CAPTURE
using System.Collections.Frozen;

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
            new DevAllowlistedWindowsCapturePrivacySampler(baseSampler),
            new WindowsCaptureWinEventSource());
    }
}

internal sealed class DevAllowlistedWindowsCapturePrivacySampler
    : IWindowsCapturePrivacySampler
{
    private static readonly FrozenSet<string> AllowedExecutableNames = new[]
    {
        "WinDayFlow.App.exe",
        "cmd.exe",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IWindowsCapturePrivacySampler _inner;

    internal DevAllowlistedWindowsCapturePrivacySampler(
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

    internal static WindowsCapturePrivacyObservation ApplyPolicy(
        WindowsCapturePrivacyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var signals = observation.Signals;
        var identity = signals.CaptureIdentity;
        var executable = identity.ExecutableNameObservation;
        var packageFamily = identity.PackageFamilyNameObservation;
        var windowTitle = identity.WindowTitleObservation;

        if (signals.Target.State != NativeCaptureTargetIdentityState.Present
            || observation.DisplayTarget.State
                != WindowsCaptureDisplayTargetState.Present
            || executable.State != NativeCaptureObservationState.Present
            || executable.Value is null
            || !AllowedExecutableNames.Contains(executable.Value)
            || packageFamily.State != NativeCaptureObservationState.Absent
            || windowTitle.State != NativeCaptureObservationState.Present
            || string.IsNullOrWhiteSpace(windowTitle.Value)
            || signals.ApplicationAllowed == NativeCapturePolicyDecision.Block
            || signals.WindowAllowed == NativeCapturePolicyDecision.Block)
        {
            return WindowsCapturePrivacyObservation.FailClosed;
        }

        // Dev-only executable-name admission is not signer proof. Publisher identity
        // remains non-authoritative here and this policy must not be used in production.
        var authorizedSignals = new NativeCapturePrivacySignals(
            signals.SessionUnlocked,
            signals.SecureDesktopClear,
            signals.RemoteSession,
            signals.PresentationMode,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            signals.StorageAvailable,
            identity,
            signals.Target);
        return new WindowsCapturePrivacyObservation(
            authorizedSignals,
            observation.DisplayTarget);
    }
}
#endif
