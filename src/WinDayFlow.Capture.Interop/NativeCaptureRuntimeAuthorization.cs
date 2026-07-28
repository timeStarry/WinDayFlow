using System.Text;

namespace WinDayFlow.Capture.Interop;

public enum NativeCaptureTargetIdentityState
{
    Unknown = 0,
    Absent = 1,
    Present = 2,
}

public enum NativeCaptureAuthorizationScope
{
    ForegroundTarget = 0,
    DisplayWide = 1,
}

public sealed class NativeCaptureTargetIdentity
    : IEquatable<NativeCaptureTargetIdentity>
{
    internal const int MaximumDisplayDeviceKeyCharacters = 31;
    internal const int MaximumDisplayDeviceKeyUtf8Bytes = 93;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private NativeCaptureTargetIdentity(
        NativeCaptureTargetIdentityState state,
        ulong windowHandle,
        uint processId,
        ulong processCreationTime100ns,
        ulong targetEpoch,
        ulong displayMonitorHandle,
        string? displayDeviceKey,
        NativeCaptureAuthorizationScope scope)
    {
        State = state;
        WindowHandle = windowHandle;
        ProcessId = processId;
        ProcessCreationTime100ns = processCreationTime100ns;
        TargetEpoch = targetEpoch;
        DisplayMonitorHandle = displayMonitorHandle;
        DisplayDeviceKey = displayDeviceKey;
        Scope = scope;
    }

    public static NativeCaptureTargetIdentity Unknown { get; } = new(
        NativeCaptureTargetIdentityState.Unknown,
        0,
        0,
        0,
        0,
        0,
        displayDeviceKey: null,
        NativeCaptureAuthorizationScope.ForegroundTarget);

    public static NativeCaptureTargetIdentity Absent { get; } = new(
        NativeCaptureTargetIdentityState.Absent,
        0,
        0,
        0,
        0,
        0,
        displayDeviceKey: null,
        NativeCaptureAuthorizationScope.ForegroundTarget);

    public NativeCaptureTargetIdentityState State { get; }

    public ulong WindowHandle { get; }

    public uint ProcessId { get; }

    public ulong ProcessCreationTime100ns { get; }

    public ulong TargetEpoch { get; }

    public ulong DisplayMonitorHandle { get; }

    public string? DisplayDeviceKey { get; }

    public NativeCaptureAuthorizationScope Scope { get; }

    public static NativeCaptureTargetIdentity Present(
        ulong windowHandle,
        uint processId,
        ulong processCreationTime100ns,
        ulong targetEpoch,
        ulong displayMonitorHandle,
        string displayDeviceKey)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        ArgumentOutOfRangeException.ThrowIfZero(processId);
        ArgumentOutOfRangeException.ThrowIfZero(processCreationTime100ns);
        ArgumentOutOfRangeException.ThrowIfZero(targetEpoch);
        ArgumentOutOfRangeException.ThrowIfZero(displayMonitorHandle);
        if (!IsValidDisplayDeviceKey(displayDeviceKey))
        {
            throw new ArgumentException(
                "A display device key must have valid bounded UTF-8 content and cannot contain control characters.",
                nameof(displayDeviceKey));
        }

        return new NativeCaptureTargetIdentity(
            NativeCaptureTargetIdentityState.Present,
            windowHandle,
            processId,
            processCreationTime100ns,
            targetEpoch,
            displayMonitorHandle,
            displayDeviceKey,
            NativeCaptureAuthorizationScope.ForegroundTarget);
    }

    public static NativeCaptureTargetIdentity DisplayWide(
        ulong targetEpoch,
        ulong displayMonitorHandle,
        string displayDeviceKey)
    {
        ArgumentOutOfRangeException.ThrowIfZero(targetEpoch);
        ArgumentOutOfRangeException.ThrowIfZero(displayMonitorHandle);
        if (!IsValidDisplayDeviceKey(displayDeviceKey))
        {
            throw new ArgumentException(
                "A display device key must have valid bounded UTF-8 content and cannot contain control characters.",
                nameof(displayDeviceKey));
        }

        return new NativeCaptureTargetIdentity(
            NativeCaptureTargetIdentityState.Present,
            0,
            0,
            0,
            targetEpoch,
            displayMonitorHandle,
            displayDeviceKey,
            NativeCaptureAuthorizationScope.DisplayWide);
    }

    public bool Equals(NativeCaptureTargetIdentity? other)
    {
        return ReferenceEquals(this, other)
            || (other is not null
                && State == other.State
                && WindowHandle == other.WindowHandle
                && ProcessId == other.ProcessId
                && ProcessCreationTime100ns == other.ProcessCreationTime100ns
                && TargetEpoch == other.TargetEpoch
                && DisplayMonitorHandle == other.DisplayMonitorHandle
                && Scope == other.Scope
                && string.Equals(
                    DisplayDeviceKey,
                    other.DisplayDeviceKey,
                    StringComparison.OrdinalIgnoreCase));
    }

    public override bool Equals(object? obj) =>
        obj is NativeCaptureTargetIdentity other && Equals(other);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            State,
            WindowHandle,
            ProcessId,
            ProcessCreationTime100ns,
            TargetEpoch,
            DisplayMonitorHandle,
            Scope,
            DisplayDeviceKey is null
                ? 0
                : StringComparer.OrdinalIgnoreCase.GetHashCode(DisplayDeviceKey));
    }

    public override string ToString()
    {
        return $"{nameof(NativeCaptureTargetIdentity)} {{ State = {State}, Scope = {Scope}, Values = [REDACTED] }}";
    }

    internal static bool IsValidDisplayDeviceKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumDisplayDeviceKeyCharacters
            || value.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            var byteCount = StrictUtf8.GetByteCount(value);
            return byteCount is > 0 and <= MaximumDisplayDeviceKeyUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
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
