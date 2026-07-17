namespace WinDayFlow.Capture.Interop;

public enum WindowsCaptureDisplayTargetState
{
    Unknown = 0,
    Absent = 1,
    Present = 2,
}

public sealed class WindowsCaptureDisplayTarget
    : IEquatable<WindowsCaptureDisplayTarget>
{
    internal const int MaximumDeviceKeyCharacters = 31;

    private WindowsCaptureDisplayTarget(
        WindowsCaptureDisplayTargetState state,
        ulong monitorHandle,
        string? deviceKey)
    {
        State = state;
        MonitorHandle = monitorHandle;
        DeviceKey = deviceKey;
    }

    public static WindowsCaptureDisplayTarget Unknown { get; } = new(
        WindowsCaptureDisplayTargetState.Unknown,
        0,
        deviceKey: null);

    public static WindowsCaptureDisplayTarget Absent { get; } = new(
        WindowsCaptureDisplayTargetState.Absent,
        0,
        deviceKey: null);

    public WindowsCaptureDisplayTargetState State { get; }

    public ulong MonitorHandle { get; }

    public string? DeviceKey { get; }

    internal static WindowsCaptureDisplayTarget Present(
        ulong monitorHandle,
        string deviceKey)
    {
        ArgumentOutOfRangeException.ThrowIfZero(monitorHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        if (deviceKey.Length > MaximumDeviceKeyCharacters
            || deviceKey.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A display device key must be bounded and cannot contain control characters.",
                nameof(deviceKey));
        }

        return new WindowsCaptureDisplayTarget(
            WindowsCaptureDisplayTargetState.Present,
            monitorHandle,
            deviceKey);
    }

    public bool Equals(WindowsCaptureDisplayTarget? other)
    {
        return ReferenceEquals(this, other)
            || (other is not null
                && State == other.State
                && MonitorHandle == other.MonitorHandle
                && string.Equals(
                    DeviceKey,
                    other.DeviceKey,
                    StringComparison.OrdinalIgnoreCase));
    }

    public override bool Equals(object? obj)
    {
        return obj is WindowsCaptureDisplayTarget other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            State,
            MonitorHandle,
            DeviceKey is null
                ? 0
                : StringComparer.OrdinalIgnoreCase.GetHashCode(DeviceKey));
    }

    public override string ToString()
    {
        return $"{nameof(WindowsCaptureDisplayTarget)} {{ "
            + $"State = {State}, Values = [REDACTED] }}";
    }
}

public sealed class WindowsCaptureTargetVerificationResult
    : IEquatable<WindowsCaptureTargetVerificationResult>
{
    internal WindowsCaptureTargetVerificationResult(
        NativeCaptureTargetIdentity target,
        WindowsCaptureDisplayTarget displayTarget,
        NativeCaptureIdentitySnapshot captureIdentity)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        DisplayTarget = displayTarget
            ?? throw new ArgumentNullException(nameof(displayTarget));
        CaptureIdentity = captureIdentity
            ?? throw new ArgumentNullException(nameof(captureIdentity));
    }

    public static WindowsCaptureTargetVerificationResult Unknown { get; } = new(
        NativeCaptureTargetIdentity.Unknown,
        WindowsCaptureDisplayTarget.Unknown,
        NativeCaptureIdentitySnapshot.Unknown);

    public static WindowsCaptureTargetVerificationResult Absent { get; } = new(
        NativeCaptureTargetIdentity.Absent,
        WindowsCaptureDisplayTarget.Absent,
        NativeCaptureIdentitySnapshot.Absent);

    public NativeCaptureTargetIdentity Target { get; }

    public WindowsCaptureDisplayTarget DisplayTarget { get; }

    public NativeCaptureIdentitySnapshot CaptureIdentity { get; }

    public bool Equals(WindowsCaptureTargetVerificationResult? other)
    {
        return ReferenceEquals(this, other)
            || (other is not null
                && Target.Equals(other.Target)
                && DisplayTarget.Equals(other.DisplayTarget)
                && CaptureIdentity.Equals(other.CaptureIdentity));
    }

    public override bool Equals(object? obj)
    {
        return obj is WindowsCaptureTargetVerificationResult other
            && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Target, DisplayTarget, CaptureIdentity);
    }

    public override string ToString()
    {
        return $"{nameof(WindowsCaptureTargetVerificationResult)} {{ "
            + $"TargetState = {Target.State}, "
            + $"DisplayTargetState = {DisplayTarget.State}, "
            + $"ExecutableNameState = {CaptureIdentity.ExecutableNameObservation.State}, "
            + $"PackageFamilyNameState = {CaptureIdentity.PackageFamilyNameObservation.State}, "
            + $"PublisherCertificateSha256State = "
            + $"{CaptureIdentity.PublisherCertificateSha256Observation.State}, "
            + $"WindowTitleState = {CaptureIdentity.WindowTitleObservation.State}, "
            + "Values = [REDACTED] }";
    }
}

public sealed class WindowsCaptureTargetVerifier
{
    private readonly object _gate = new();
    private readonly IWindowsCaptureTargetNativeApi _nativeApi;
    private readonly IWindowsCaptureTargetEpochSource _epochSource;

    public WindowsCaptureTargetVerifier()
        : this(
            PInvokeWindowsCaptureTargetNativeApi.Instance,
            ProcessWideWindowsCaptureTargetEpochSource.Instance)
    {
    }

    internal WindowsCaptureTargetVerifier(
        IWindowsCaptureTargetNativeApi nativeApi)
        : this(
            nativeApi,
            ProcessWideWindowsCaptureTargetEpochSource.Instance)
    {
    }

    internal WindowsCaptureTargetVerifier(
        IWindowsCaptureTargetNativeApi nativeApi,
        IWindowsCaptureTargetEpochSource epochSource)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        _epochSource = epochSource
            ?? throw new ArgumentNullException(nameof(epochSource));
        _epochSource.Invalidate();
    }

    public WindowsCaptureTargetVerificationResult Verify()
    {
        lock (_gate)
        {
            try
            {
                return VerifyCore();
            }
            catch (Exception exception) when (IsRecoverableNativeReadException(exception))
            {
                return InvalidateTarget();
            }
        }
    }

    internal void InvalidateObservation()
    {
        _epochSource.Invalidate();
    }

    private WindowsCaptureTargetVerificationResult VerifyCore()
    {
        if (!_nativeApi.IsSupportedPlatform
            || !_nativeApi.TryGetForegroundWindow(out var windowHandle))
        {
            return InvalidateTarget();
        }

        if (windowHandle == 0)
        {
            _epochSource.Invalidate();
            return WindowsCaptureTargetVerificationResult.Absent;
        }

        IWindowsCaptureTargetProcess? process = null;
        if (!_nativeApi.TryGetWindowOwner(windowHandle, out var firstOwner)
            || !firstOwner.IsValid
            || !_nativeApi.TryGetDisplayTarget(windowHandle, out var firstDisplay)
            || !firstDisplay.IsValid
            || !_nativeApi.TryOpenProcess(firstOwner.ProcessId, out process)
            || process is null)
        {
            process?.Dispose();
            return InvalidateTarget();
        }

        StableTargetObservation? observation;
        try
        {
            observation = ObserveStableTarget(
                windowHandle,
                firstOwner,
                firstDisplay,
                process);
        }
        finally
        {
            process.Dispose();
        }

        if (observation is not { } stable)
        {
            return InvalidateTarget();
        }

        var fingerprint = new WindowsCaptureTargetFingerprint(
            stable.WindowHandle,
            stable.Owner.ProcessId,
            stable.ProcessCreationTime100ns,
            stable.Owner.ThreadId,
            stable.DisplayTarget.MonitorHandle,
            stable.DisplayTarget.DeviceKey.ToUpperInvariant());
        if (!_epochSource.TryResolve(fingerprint, out var targetEpoch)
            || targetEpoch == 0)
        {
            return InvalidateTarget();
        }

        return new WindowsCaptureTargetVerificationResult(
            NativeCaptureTargetIdentity.Present(
                stable.WindowHandle,
                stable.Owner.ProcessId,
                stable.ProcessCreationTime100ns,
                targetEpoch),
            WindowsCaptureDisplayTarget.Present(
                stable.DisplayTarget.MonitorHandle,
                stable.DisplayTarget.DeviceKey),
            stable.CaptureIdentity);
    }

    private StableTargetObservation? ObserveStableTarget(
        ulong windowHandle,
        WindowsCaptureWindowOwner firstOwner,
        WindowsCaptureDisplayAnchor firstDisplay,
        IWindowsCaptureTargetProcess process)
    {
        if (!process.TryGetProcessId(out var firstProcessId)
            || firstProcessId != firstOwner.ProcessId
            || !process.TryGetCreationTime100ns(out var firstCreationTime)
            || firstCreationTime == 0
            || !process.TryGetActive(out var firstActive)
            || !firstActive)
        {
            return null;
        }

        var firstTitle = ReadObservation((out string value) =>
            _nativeApi.ReadWindowTitle(windowHandle, out value));
        var executableName = ReadObservation(process.ReadExecutableName);
        var packageFamilyName = ReadObservation(process.ReadPackageFamilyName);
        var publisherCertificate = ReadObservation(
            process.ReadPublisherCertificateSha256);
        var secondTitle = ReadObservation((out string value) =>
            _nativeApi.ReadWindowTitle(windowHandle, out value));
        if (!firstTitle.Equals(secondTitle)
            || IsUnresolvedApplicationFrameHost(executableName))
        {
            return null;
        }

        var captureIdentity = NativeCaptureIdentitySnapshot.FromObservations(
            executableName,
            packageFamilyName,
            publisherCertificate,
            firstTitle);

        if (!process.TryGetProcessId(out var secondProcessId)
            || secondProcessId != firstOwner.ProcessId
            || !process.TryGetCreationTime100ns(out var secondCreationTime)
            || secondCreationTime != firstCreationTime
            || !process.TryGetActive(out var secondActive)
            || !secondActive
            || !_nativeApi.TryGetForegroundWindow(out var secondWindowHandle)
            || secondWindowHandle != windowHandle
            || !_nativeApi.TryGetWindowOwner(windowHandle, out var secondOwner)
            || secondOwner != firstOwner
            || !_nativeApi.TryGetDisplayTarget(windowHandle, out var secondDisplay)
            || !firstDisplay.Equals(secondDisplay))
        {
            return null;
        }

        return new StableTargetObservation(
            windowHandle,
            firstOwner,
            firstDisplay,
            firstCreationTime,
            captureIdentity);
    }

    private WindowsCaptureTargetVerificationResult InvalidateTarget()
    {
        _epochSource.Invalidate();
        return WindowsCaptureTargetVerificationResult.Unknown;
    }

    private static NativeCaptureObservation ReadObservation(
        ReadWindowsCaptureObservation read)
    {
        try
        {
            var state = read(out var value);
            return state switch
            {
                WindowsCaptureObservationReadState.Absent
                    when value is null or { Length: 0 } =>
                    NativeCaptureObservation.Absent,
                WindowsCaptureObservationReadState.Present
                    when !string.IsNullOrEmpty(value) =>
                    NativeCaptureObservation.Present(value),
                _ => NativeCaptureObservation.Unknown,
            };
        }
        catch (Exception exception) when (IsRecoverableNativeReadException(exception))
        {
            return NativeCaptureObservation.Unknown;
        }
    }

    private static bool IsRecoverableNativeReadException(Exception exception)
    {
        return exception is not AccessViolationException
            and not OutOfMemoryException
            and not System.Runtime.InteropServices.SEHException
            and not StackOverflowException;
    }

    private static bool IsUnresolvedApplicationFrameHost(
        NativeCaptureObservation executableName)
    {
        return executableName.State == NativeCaptureObservationState.Present
            && string.Equals(
                executableName.Value,
                "ApplicationFrameHost.exe",
                StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct StableTargetObservation(
        ulong WindowHandle,
        WindowsCaptureWindowOwner Owner,
        WindowsCaptureDisplayAnchor DisplayTarget,
        ulong ProcessCreationTime100ns,
        NativeCaptureIdentitySnapshot CaptureIdentity);

}

internal interface IWindowsCaptureTargetEpochSource
{
    bool TryResolve(
        WindowsCaptureTargetFingerprint fingerprint,
        out ulong targetEpoch);

    void Invalidate();
}

internal sealed class ProcessWideWindowsCaptureTargetEpochSource
    : IWindowsCaptureTargetEpochSource
{
    private readonly object _gate = new();
    private WindowsCaptureTargetFingerprint? _currentFingerprint;
    private ulong _lastIssuedEpoch;

    internal ProcessWideWindowsCaptureTargetEpochSource(
        ulong lastIssuedEpoch = 0)
    {
        _lastIssuedEpoch = lastIssuedEpoch;
    }

    internal static ProcessWideWindowsCaptureTargetEpochSource Instance { get; } =
        new();

    public bool TryResolve(
        WindowsCaptureTargetFingerprint fingerprint,
        out ulong targetEpoch)
    {
        lock (_gate)
        {
            if (_currentFingerprint is { } current
                && current == fingerprint)
            {
                targetEpoch = _lastIssuedEpoch;
                return targetEpoch != 0;
            }

            if (_lastIssuedEpoch == ulong.MaxValue)
            {
                _currentFingerprint = null;
                targetEpoch = 0;
                return false;
            }

            targetEpoch = ++_lastIssuedEpoch;
            _currentFingerprint = fingerprint;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _currentFingerprint = null;
        }
    }
}

internal readonly record struct WindowsCaptureTargetFingerprint(
    ulong WindowHandle,
    uint ProcessId,
    ulong ProcessCreationTime100ns,
    uint ThreadId,
    ulong DisplayMonitorHandle,
    string DisplayDeviceKey)
{
    public override string ToString()
    {
        return $"{nameof(WindowsCaptureTargetFingerprint)} {{ Values = [REDACTED] }}";
    }
}

internal enum WindowsCaptureObservationReadState
{
    Unknown = 0,
    Absent = 1,
    Present = 2,
}

internal readonly record struct WindowsCaptureWindowOwner(
    uint ThreadId,
    uint ProcessId)
{
    internal bool IsValid => ThreadId != 0 && ProcessId != 0;

    public override string ToString()
    {
        return $"{nameof(WindowsCaptureWindowOwner)} {{ Values = [REDACTED] }}";
    }
}

internal readonly record struct WindowsCaptureDisplayAnchor(
    ulong MonitorHandle,
    string DeviceKey)
{
    internal bool IsValid =>
        MonitorHandle != 0
        && !string.IsNullOrWhiteSpace(DeviceKey)
        && DeviceKey.Length <= WindowsCaptureDisplayTarget.MaximumDeviceKeyCharacters
        && !DeviceKey.Any(char.IsControl);

    public bool Equals(WindowsCaptureDisplayAnchor other)
    {
        return MonitorHandle == other.MonitorHandle
            && string.Equals(
                DeviceKey,
                other.DeviceKey,
                StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            MonitorHandle,
            DeviceKey is null
                ? 0
                : StringComparer.OrdinalIgnoreCase.GetHashCode(DeviceKey));
    }

    public override string ToString()
    {
        return $"{nameof(WindowsCaptureDisplayAnchor)} {{ Values = [REDACTED] }}";
    }
}

internal delegate WindowsCaptureObservationReadState ReadWindowsCaptureObservation(
    out string value);

internal interface IWindowsCaptureTargetNativeApi
{
    bool IsSupportedPlatform { get; }

    bool TryGetForegroundWindow(out ulong windowHandle);

    bool TryGetWindowOwner(
        ulong windowHandle,
        out WindowsCaptureWindowOwner owner);

    bool TryGetDisplayTarget(
        ulong windowHandle,
        out WindowsCaptureDisplayAnchor displayTarget);

    bool TryOpenProcess(
        uint processId,
        out IWindowsCaptureTargetProcess? process);

    WindowsCaptureObservationReadState ReadWindowTitle(
        ulong windowHandle,
        out string value);
}

internal interface IWindowsCaptureTargetProcess : IDisposable
{
    bool TryGetProcessId(out uint processId);

    bool TryGetCreationTime100ns(out ulong creationTime100ns);

    bool TryGetActive(out bool active);

    WindowsCaptureObservationReadState ReadExecutableName(out string value);

    WindowsCaptureObservationReadState ReadPackageFamilyName(out string value);

    WindowsCaptureObservationReadState ReadPublisherCertificateSha256(
        out string value);
}
