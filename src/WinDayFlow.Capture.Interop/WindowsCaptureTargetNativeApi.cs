using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinDayFlow.Capture.Interop;

internal sealed class PInvokeWindowsCaptureTargetNativeApi
    : IWindowsCaptureTargetNativeApi
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int MaximumWindowTextCharacters = 32_768;
    private const uint MonitorDefaultToNull = 0;

    internal const uint TargetProcessDesiredAccess =
        ProcessQueryLimitedInformation | Synchronize;

    private PInvokeWindowsCaptureTargetNativeApi()
    {
    }

    internal static PInvokeWindowsCaptureTargetNativeApi Instance { get; } = new();

    public bool IsSupportedPlatform =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public bool TryGetForegroundWindow(out ulong windowHandle)
    {
        var handle = WindowsCaptureTargetMethods.GetForegroundWindow();
        windowHandle = unchecked((ulong)handle.ToInt64());
        return true;
    }

    public bool TryGetWindowOwner(
        ulong windowHandle,
        out WindowsCaptureWindowOwner owner)
    {
        owner = default;
        if (windowHandle == 0)
        {
            return false;
        }

        var threadId = WindowsCaptureTargetMethods.GetWindowThreadProcessId(
            ToNativeHandle(windowHandle),
            out var processId);
        if (threadId == 0 || processId == 0)
        {
            return false;
        }

        owner = new WindowsCaptureWindowOwner(threadId, processId);
        return true;
    }

    public bool TryOpenProcess(
        uint processId,
        out IWindowsCaptureTargetProcess? process)
    {
        process = null;
        if (processId == 0)
        {
            return false;
        }

        var handle = WindowsCaptureTargetMethods.OpenProcess(
            TargetProcessDesiredAccess,
            inheritHandle: false,
            processId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        process = new PInvokeWindowsCaptureTargetProcess(handle);
        return true;
    }

    public bool TryGetDisplayTarget(
        ulong windowHandle,
        out WindowsCaptureDisplayAnchor displayTarget)
    {
        displayTarget = default;
        if (windowHandle == 0)
        {
            return false;
        }

        var monitor = WindowsCaptureTargetMethods.MonitorFromWindow(
            ToNativeHandle(windowHandle),
            MonitorDefaultToNull);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new WindowsMonitorInfoEx
        {
            Size = checked((uint)Marshal.SizeOf<WindowsMonitorInfoEx>()),
            DeviceName = string.Empty,
        };
        if (!WindowsCaptureTargetMethods.GetMonitorInfo(monitor, ref monitorInfo)
            || string.IsNullOrWhiteSpace(monitorInfo.DeviceName)
            || monitorInfo.DeviceName.Any(char.IsControl))
        {
            return false;
        }

        displayTarget = new WindowsCaptureDisplayAnchor(
            unchecked((ulong)monitor.ToInt64()),
            monitorInfo.DeviceName);
        return displayTarget.IsValid;
    }

    public WindowsCaptureObservationReadState ReadWindowTitle(
        ulong windowHandle,
        out string value)
    {
        value = string.Empty;
        if (windowHandle == 0)
        {
            return WindowsCaptureObservationReadState.Unknown;
        }

        var buffer = ArrayPool<char>.Shared.Rent(MaximumWindowTextCharacters);
        try
        {
            Marshal.SetLastPInvokeError(0);
            var copied = WindowsCaptureTargetMethods.GetWindowText(
                ToNativeHandle(windowHandle),
                buffer,
                MaximumWindowTextCharacters);
            if (copied == 0)
            {
                return Marshal.GetLastPInvokeError() == 0
                    ? WindowsCaptureObservationReadState.Absent
                    : WindowsCaptureObservationReadState.Unknown;
            }

            if (copied < 0 || copied >= MaximumWindowTextCharacters - 1)
            {
                return WindowsCaptureObservationReadState.Unknown;
            }

            value = new string(buffer, 0, copied);
            return WindowsCaptureObservationReadState.Present;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static IntPtr ToNativeHandle(ulong windowHandle)
    {
        return new IntPtr(unchecked((long)windowHandle));
    }
}

internal sealed class PInvokeWindowsCaptureTargetProcess
    : IWindowsCaptureTargetProcess
{
    private const uint AppModelErrorNoPackage = 15_700;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorSuccess = 0;
    private const int MaximumPackageFamilyNameCharacters = 256;
    private const int MaximumProcessImagePathCharacters = 32_768;
    private const uint WaitFailed = uint.MaxValue;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;

    private readonly SafeProcessHandle _handle;

    internal PInvokeWindowsCaptureTargetProcess(SafeProcessHandle handle)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        if (handle.IsInvalid)
        {
            throw new ArgumentException(
                "A target process handle must be valid.",
                nameof(handle));
        }
    }

    public bool TryGetProcessId(out uint processId)
    {
        processId = WindowsCaptureTargetMethods.GetProcessId(_handle);
        return processId != 0;
    }

    public bool TryGetCreationTime100ns(out ulong creationTime100ns)
    {
        creationTime100ns = 0;
        if (!WindowsCaptureTargetMethods.GetProcessTimes(
                _handle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            return false;
        }

        creationTime100ns = ((ulong)creationTime.HighDateTime << 32)
            | creationTime.LowDateTime;
        return creationTime100ns != 0;
    }

    public bool TryGetActive(out bool active)
    {
        active = false;
        var result = WindowsCaptureTargetMethods.WaitForSingleObject(
            _handle,
            milliseconds: 0);
        switch (result)
        {
            case WaitTimeout:
                active = true;
                return true;
            case WaitObject0:
                return true;
            case WaitFailed:
            default:
                return false;
        }
    }

    public WindowsCaptureObservationReadState ReadExecutableName(out string value)
    {
        value = string.Empty;
        var buffer = ArrayPool<char>.Shared.Rent(MaximumProcessImagePathCharacters);
        try
        {
            var characterCount = (uint)MaximumProcessImagePathCharacters;
            if (!WindowsCaptureTargetMethods.QueryFullProcessImageName(
                    _handle,
                    flags: 0,
                    buffer,
                    ref characterCount)
                || characterCount == 0
                || characterCount >= MaximumProcessImagePathCharacters)
            {
                return WindowsCaptureObservationReadState.Unknown;
            }

            var path = buffer.AsSpan(0, checked((int)characterCount));
            var separator = path.LastIndexOfAny('\\', '/');
            var executableName = path[(separator + 1)..];
            if (executableName.IsEmpty)
            {
                return WindowsCaptureObservationReadState.Unknown;
            }

            value = new string(executableName);
            return WindowsCaptureObservationReadState.Present;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    public WindowsCaptureObservationReadState ReadPackageFamilyName(
        out string value)
    {
        value = string.Empty;
        uint requiredCharacters = 0;
        var firstResult = unchecked((uint)WindowsCaptureTargetMethods.GetPackageFamilyName(
            _handle,
            ref requiredCharacters,
            packageFamilyName: null));
        if (firstResult == AppModelErrorNoPackage)
        {
            return WindowsCaptureObservationReadState.Absent;
        }

        if (firstResult != ErrorInsufficientBuffer
            || requiredCharacters is < 2 or > MaximumPackageFamilyNameCharacters)
        {
            return WindowsCaptureObservationReadState.Unknown;
        }

        var buffer = ArrayPool<char>.Shared.Rent(checked((int)requiredCharacters));
        try
        {
            var suppliedCharacters = requiredCharacters;
            var secondResult = unchecked((uint)WindowsCaptureTargetMethods
                .GetPackageFamilyName(
                    _handle,
                    ref suppliedCharacters,
                    buffer));
            if (secondResult != ErrorSuccess
                || suppliedCharacters == 0
                || suppliedCharacters > requiredCharacters)
            {
                return WindowsCaptureObservationReadState.Unknown;
            }

            var valueLength = checked((int)suppliedCharacters);
            while (valueLength > 0 && buffer[valueLength - 1] == '\0')
            {
                valueLength--;
            }

            if (valueLength == 0)
            {
                return WindowsCaptureObservationReadState.Unknown;
            }

            value = new string(buffer, 0, valueLength);
            return WindowsCaptureObservationReadState.Present;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    public WindowsCaptureObservationReadState ReadPublisherCertificateSha256(
        out string value)
    {
        value = string.Empty;

        // A path-only certificate lookup is not proof that the verified signer is
        // bound to this running image. Keep this observation unknown until the
        // file-handle and WinVerifyTrust boundary is implemented.
        return WindowsCaptureObservationReadState.Unknown;
    }

    public void Dispose()
    {
        _handle.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct WindowsFileTime
{
    internal readonly uint LowDateTime;
    internal readonly uint HighDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct WindowsRectangle
{
    internal readonly int Left;
    internal readonly int Top;
    internal readonly int Right;
    internal readonly int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsMonitorInfoEx
{
    internal uint Size;
    internal WindowsRectangle Monitor;
    internal WindowsRectangle Work;
    internal uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string DeviceName;
}

internal static class WindowsCaptureTargetMethods
{
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(
        IntPtr window,
        [Out] char[] text,
        int maximumCharacterCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr MonitorFromWindow(
        IntPtr window,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref WindowsMonitorInfoEx monitorInfo);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out WindowsFileTime creationTime,
        out WindowsFileTime exitTime,
        out WindowsFileTime kernelTime,
        out WindowsFileTime userTime);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern uint WaitForSingleObject(
        SafeProcessHandle handle,
        uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint characterCount);

    [DllImport("kernel32.dll", EntryPoint = "GetPackageFamilyName",
        CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetPackageFamilyName(
        SafeProcessHandle process,
        ref uint packageFamilyNameLength,
        [Out] char[]? packageFamilyName);
}
