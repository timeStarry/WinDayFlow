namespace WinDayFlow.Capture.Interop;

public enum NativeCaptureTargetIdentityState
{
    Unknown = 0,
    Absent = 1,
    Present = 2,
}

public sealed class NativeCaptureTargetIdentity
    : IEquatable<NativeCaptureTargetIdentity>
{
    private NativeCaptureTargetIdentity(
        NativeCaptureTargetIdentityState state,
        ulong windowHandle,
        uint processId,
        ulong processCreationTime100ns,
        ulong targetEpoch)
    {
        State = state;
        WindowHandle = windowHandle;
        ProcessId = processId;
        ProcessCreationTime100ns = processCreationTime100ns;
        TargetEpoch = targetEpoch;
    }

    public static NativeCaptureTargetIdentity Unknown { get; } = new(
        NativeCaptureTargetIdentityState.Unknown,
        0,
        0,
        0,
        0);

    public static NativeCaptureTargetIdentity Absent { get; } = new(
        NativeCaptureTargetIdentityState.Absent,
        0,
        0,
        0,
        0);

    public NativeCaptureTargetIdentityState State { get; }

    public ulong WindowHandle { get; }

    public uint ProcessId { get; }

    public ulong ProcessCreationTime100ns { get; }

    public ulong TargetEpoch { get; }

    public static NativeCaptureTargetIdentity Present(
        ulong windowHandle,
        uint processId,
        ulong processCreationTime100ns,
        ulong targetEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        ArgumentOutOfRangeException.ThrowIfZero(processId);
        ArgumentOutOfRangeException.ThrowIfZero(processCreationTime100ns);
        ArgumentOutOfRangeException.ThrowIfZero(targetEpoch);
        return new NativeCaptureTargetIdentity(
            NativeCaptureTargetIdentityState.Present,
            windowHandle,
            processId,
            processCreationTime100ns,
            targetEpoch);
    }

    public bool Equals(NativeCaptureTargetIdentity? other)
    {
        return ReferenceEquals(this, other)
            || (other is not null
                && State == other.State
                && WindowHandle == other.WindowHandle
                && ProcessId == other.ProcessId
                && ProcessCreationTime100ns == other.ProcessCreationTime100ns
                && TargetEpoch == other.TargetEpoch);
    }

    public override bool Equals(object? obj) =>
        obj is NativeCaptureTargetIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        State,
        WindowHandle,
        ProcessId,
        ProcessCreationTime100ns,
        TargetEpoch);

    public override string ToString()
    {
        return $"{nameof(NativeCaptureTargetIdentity)} {{ State = {State}, Values = [REDACTED] }}";
    }
}

public sealed class NativeCaptureRuntimeAuthorization
    : IEquatable<NativeCaptureRuntimeAuthorization>
{
    public NativeCaptureRuntimeAuthorization(
        NativeCapturePrivacyContext privacyContext,
        NativeCaptureTargetIdentity target)
    {
        PrivacyContext = privacyContext
            ?? throw new ArgumentNullException(nameof(privacyContext));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        var fullyAllowed = IsFullyAllowed(privacyContext);
        if (fullyAllowed && target.State != NativeCaptureTargetIdentityState.Present)
        {
            throw new ArgumentException(
                "A fully allowed runtime authorization requires a present capture target.",
                nameof(target));
        }


        if (!fullyAllowed && target.State == NativeCaptureTargetIdentityState.Present)
        {
            throw new ArgumentException(
                "A blocked or unknown runtime authorization must clear its capture target.",
                nameof(target));
        }
    }

    public NativeCapturePrivacyContext PrivacyContext { get; }

    public NativeCaptureTargetIdentity Target { get; }

    public ulong RuntimePolicyRevision => PrivacyContext.RuntimePolicyRevision;

    public bool Equals(NativeCaptureRuntimeAuthorization? other)
    {
        return ReferenceEquals(this, other)
            || (other is not null
                && PrivacyContext == other.PrivacyContext
                && Target.Equals(other.Target));
    }

    public override bool Equals(object? obj) =>
        obj is NativeCaptureRuntimeAuthorization other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(PrivacyContext, Target);

    public override string ToString()
    {
        return $"{nameof(NativeCaptureRuntimeAuthorization)} {{ "
            + $"RuntimePolicyRevision = {RuntimePolicyRevision}, "
            + $"TargetState = {Target.State}, TargetValues = [REDACTED] }}";
    }

    internal NativeCaptureRuntimeAuthorization WithRuntimePolicyRevision(
        ulong runtimePolicyRevision)
    {
        return new NativeCaptureRuntimeAuthorization(
            new NativeCapturePrivacyContext(
                PrivacyContext.ConsentGranted,
                PrivacyContext.SessionUnlocked,
                PrivacyContext.SecureDesktopClear,
                PrivacyContext.RemoteSessionAllowed,
                PrivacyContext.PresentationAllowed,
                PrivacyContext.ApplicationAllowed,
                PrivacyContext.WindowAllowed,
                PrivacyContext.StorageAvailable,
                runtimePolicyRevision),
            Target);
    }

    internal static bool IsFullyAllowed(NativeCapturePrivacyContext context)
    {
        return context.ConsentGranted == NativeCapturePolicyDecision.Allow
            && context.SessionUnlocked == NativeCapturePolicyDecision.Allow
            && context.SecureDesktopClear == NativeCapturePolicyDecision.Allow
            && context.RemoteSessionAllowed == NativeCapturePolicyDecision.Allow
            && context.PresentationAllowed == NativeCapturePolicyDecision.Allow
            && context.ApplicationAllowed == NativeCapturePolicyDecision.Allow
            && context.WindowAllowed == NativeCapturePolicyDecision.Allow
            && context.StorageAvailable == NativeCapturePolicyDecision.Allow;
    }
}
