using System.Runtime.InteropServices;

namespace WinDayFlow.Capture.Interop;

public sealed class WindowsCapturePrivacyProbe
{
    private readonly IWindowsPrivacyNativeApi _nativeApi;
    private readonly string _storageDirectory;
    private readonly ulong _minimumStorageHeadroomBytes;

    public WindowsCapturePrivacyProbe(
        string storageDirectory,
        ulong minimumStorageHeadroomBytes)
        : this(
            PInvokeWindowsPrivacyNativeApi.Instance,
            storageDirectory,
            minimumStorageHeadroomBytes)
    {
    }

    internal WindowsCapturePrivacyProbe(
        IWindowsPrivacyNativeApi nativeApi,
        string storageDirectory,
        ulong minimumStorageHeadroomBytes)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        if (!Path.IsPathFullyQualified(storageDirectory))
        {
            throw new ArgumentException(
                "The privacy probe storage directory must be fully qualified.",
                nameof(storageDirectory));
        }

        if (minimumStorageHeadroomBytes == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumStorageHeadroomBytes),
                minimumStorageHeadroomBytes,
                "The minimum storage headroom must be positive.");
        }

        _storageDirectory = Path.GetFullPath(storageDirectory);
        _minimumStorageHeadroomBytes = minimumStorageHeadroomBytes;
    }

    public NativeCapturePrivacySignals Sample()
    {
        if (!IsSupportedPlatform())
        {
            return NativeCapturePrivacySignals.FailClosed;
        }

        return new NativeCapturePrivacySignals(
            SamplePolicyDecision(_nativeApi.TryGetSessionUnlocked),
            SamplePolicyDecision(_nativeApi.TryGetSecureDesktopClear),
            SampleRemoteSession(),
            SampleCondition(_nativeApi.TryGetPresentationMode),
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Unknown,
            SampleStorageCore());
    }

    private NativeCaptureConditionState SampleRemoteSession()
    {
        var protocolRead = TryReadRemoteProtocol(out var protocol);
        var metricsRead = TryReadRemoteSessionMetrics(
            out var remoteSession,
            out var remoteControl);

        if ((protocolRead && protocol == WindowsRemoteProtocol.Remote)
            || (metricsRead && (remoteSession || remoteControl)))
        {
            return NativeCaptureConditionState.Active;
        }

        return protocolRead
            && protocol == WindowsRemoteProtocol.Console
            && metricsRead
            && !remoteSession
            && !remoteControl
                ? NativeCaptureConditionState.Inactive
                : NativeCaptureConditionState.Unknown;
    }

    private bool IsSupportedPlatform()
    {
        try
        {
            return _nativeApi.IsSupportedPlatform;
        }
        catch (Exception exception) when (IsRecoverableNativeReadException(exception))
        {
            return false;
        }
    }

    private bool TryReadRemoteProtocol(out WindowsRemoteProtocol protocol)
    {
        try
        {
            return _nativeApi.TryGetRemoteProtocol(out protocol);
        }
        catch (Exception exception) when (IsRecoverableNativeReadException(exception))
        {
            protocol = default;
            return false;
        }
    }

    private bool TryReadRemoteSessionMetrics(
        out bool remoteSession,
        out bool remoteControl)
    {
        try
        {
            return _nativeApi.TryGetRemoteSessionMetrics(
                out remoteSession,
                out remoteControl);
        }
        catch (Exception exception) when (IsRecoverableNativeReadException(exception))
        {
            remoteSession = false;
            remoteControl = false;
            return false;
        }
    }

    internal NativeCapturePolicyDecision SampleStorage()
    {
        return IsSupportedPlatform()
            ? SampleStorageCore()
            : NativeCapturePolicyDecision.Unknown;
    }

    private NativeCapturePolicyDecision SampleStorageCore()
    {
        try
        {
            if (!_nativeApi.TryGetAvailableStorageBytes(
                    _storageDirectory,
                    out var availableBytes))
            {
                return NativeCapturePolicyDecision.Unknown;
            }

            return availableBytes >= _minimumStorageHeadroomBytes
                ? NativeCapturePolicyDecision.Allow
                : NativeCapturePolicyDecision.Block;
        }
        catch (Exception exception) when (IsRecoverableNativeReadException(exception))
        {
            return NativeCapturePolicyDecision.Unknown;
        }
    }

    private static NativeCapturePolicyDecision SamplePolicyDecision(
        TryReadBoolean read)
    {
        try
        {
            return read(out var allowed)
                ? allowed
                    ? NativeCapturePolicyDecision.Allow
                    : NativeCapturePolicyDecision.Block
                : NativeCapturePolicyDecision.Unknown;
        }
        catch (Exception exception) when (IsRecoverableNativeReadException(exception))
        {
            return NativeCapturePolicyDecision.Unknown;
        }
    }

    private static NativeCaptureConditionState SampleCondition(
        TryReadBoolean read)
    {
        try
        {
            return read(out var active)
                ? active
                    ? NativeCaptureConditionState.Active
                    : NativeCaptureConditionState.Inactive
                : NativeCaptureConditionState.Unknown;
        }
        catch (Exception exception) when (IsRecoverableNativeReadException(exception))
        {
            return NativeCaptureConditionState.Unknown;
        }
    }

    internal static bool IsRecoverableNativeReadException(Exception exception)
    {
        return exception is not AccessViolationException
            and not OutOfMemoryException
            and not SEHException
            and not StackOverflowException;
    }
}

internal delegate bool TryReadBoolean(out bool value);

internal enum WindowsRemoteProtocol
{
    Console = 0,
    Remote = 1,
}

internal interface IWindowsPrivacyNativeApi
{
    bool IsSupportedPlatform { get; }

    bool TryGetSessionUnlocked(out bool unlocked);

    bool TryGetSecureDesktopClear(out bool clear);

    bool TryGetRemoteProtocol(out WindowsRemoteProtocol protocol);

    bool TryGetRemoteSessionMetrics(
        out bool remoteSession,
        out bool remoteControl);

    bool TryGetPresentationMode(out bool active);

    bool TryGetAvailableStorageBytes(
        string directory,
        out ulong availableBytes);
}
