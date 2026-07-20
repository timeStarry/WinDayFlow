using System.Runtime.InteropServices;

namespace WinDayFlow.Capture.Interop;

internal sealed class PInvokeWindowsPrivacyNativeApi : IWindowsPrivacyNativeApi
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UserObjectName = 2;
    private const uint MaximumDesktopNameBytes = 64 * 1024;
    private const int SmRemoteSession = 0x1000;
    private const int SmRemoteControl = 0x2001;
    private const int WtsInfoExLevelOne = 1;
    private const int WtsSessionStateLock = 0;
    private const int WtsSessionStateUnlock = 1;

    private static readonly int WtsSessionStateOffset = checked(
        (int)Marshal.OffsetOf<WtsInfoExPrefix>(nameof(WtsInfoExPrefix.Data))
        + (int)Marshal.OffsetOf<WtsInfoExLevel1Prefix>(
            nameof(WtsInfoExLevel1Prefix.SessionState)));
    private static readonly int WtsSessionFlagsOffset = checked(
        (int)Marshal.OffsetOf<WtsInfoExPrefix>(nameof(WtsInfoExPrefix.Data))
        + (int)Marshal.OffsetOf<WtsInfoExLevel1Prefix>(
            nameof(WtsInfoExLevel1Prefix.SessionFlags)));
    private static readonly int WtsMinimumInfoExBytes = WtsSessionFlagsOffset
        + sizeof(int);

    private PInvokeWindowsPrivacyNativeApi()
    {
    }

    internal static PInvokeWindowsPrivacyNativeApi Instance { get; } = new();

    internal static WindowsWtsInfoExLayout WtsInfoExLayout { get; } = new(
        checked((int)Marshal.OffsetOf<WtsInfoExPrefix>(nameof(WtsInfoExPrefix.Data))),
        WtsSessionStateOffset,
        WtsSessionFlagsOffset,
        WtsMinimumInfoExBytes);

    public bool IsSupportedPlatform =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public bool TryGetSessionUnlocked(out bool unlocked)
    {
        unlocked = false;
        if (!TryQuerySessionInformation(
                WtsInfoClass.SessionInfoEx,
                out var memory,
                out var bytesReturned))
        {
            return false;
        }

        using (memory)
        {
            if (memory.IsInvalid || bytesReturned < WtsMinimumInfoExBytes)
            {
                return false;
            }

            var buffer = memory.DangerousGetHandle();
            var level = Marshal.ReadInt32(buffer);
            if (level != WtsInfoExLevelOne)
            {
                return false;
            }

            var rawState = Marshal.ReadInt32(buffer, WtsSessionStateOffset);
            var rawFlags = Marshal.ReadInt32(buffer, WtsSessionFlagsOffset);
            if (!Enum.IsDefined((WtsConnectState)rawState)
                || rawFlags is not WtsSessionStateLock and not WtsSessionStateUnlock)
            {
                return false;
            }

            unlocked = rawFlags == WtsSessionStateUnlock
                && rawState == (int)WtsConnectState.Active;
            return true;
        }
    }

    public bool TryGetSecureDesktopClear(out bool clear)
    {
        clear = false;
        var rawDesktop = WindowsPrivacyMethods.OpenInputDesktop(
            0,
            inherit: false,
            DesktopReadObjects);
        if (rawDesktop == IntPtr.Zero)
        {
            return false;
        }

        using var desktop = new SafeDesktopHandle(
            rawDesktop,
            WindowsPrivacyMethods.CloseDesktop);
        if (!TryGetDesktopName(desktop, out var desktopName))
        {
            return false;
        }

        clear = string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public bool TryGetRemoteProtocol(out WindowsRemoteProtocol protocol)
    {
        protocol = default;
        if (!TryQuerySessionInformation(
                WtsInfoClass.ClientProtocolType,
                out var memory,
                out var bytesReturned))
        {
            return false;
        }

        using (memory)
        {
            if (memory.IsInvalid || bytesReturned < sizeof(ushort))
            {
                return false;
            }

            var rawProtocol = unchecked((ushort)Marshal.ReadInt16(
                memory.DangerousGetHandle()));
            switch (rawProtocol)
            {
                case 0:
                    protocol = WindowsRemoteProtocol.Console;
                    return true;
                case 1:
                case 2:
                    protocol = WindowsRemoteProtocol.Remote;
                    return true;
                default:
                    return false;
            }
        }
    }

    public bool TryGetRemoteSessionMetrics(
        out bool remoteSession,
        out bool remoteControl)
    {
        remoteSession = WindowsPrivacyMethods.GetSystemMetrics(SmRemoteSession) != 0;
        remoteControl = WindowsPrivacyMethods.GetSystemMetrics(SmRemoteControl) != 0;
        return true;
    }

    public bool TryGetPresentationMode(out bool active)
    {
        active = false;
        var result = WindowsPrivacyMethods.SHQueryUserNotificationState(out var state);
        if (result < 0 || !Enum.IsDefined(state) || state == UserNotificationState.Unknown)
        {
            return false;
        }

        active = state == UserNotificationState.PresentationMode;
        return true;
    }

    public bool TryGetAvailableStorageBytes(
        string directory,
        out ulong availableBytes)
    {
        return WindowsPrivacyMethods.GetDiskFreeSpaceEx(
            directory,
            out availableBytes,
            out _,
            out _);
    }

    private static bool TryQuerySessionInformation(
        WtsInfoClass infoClass,
        out SafeWtsMemoryHandle memory,
        out int bytesReturned)
    {
        bytesReturned = 0;
        var succeeded = WindowsPrivacyMethods.WTSQuerySessionInformation(
            IntPtr.Zero,
            uint.MaxValue,
            infoClass,
            out var buffer,
            out var rawBytesReturned);
        memory = new SafeWtsMemoryHandle(
            buffer,
            WindowsPrivacyMethods.WTSFreeMemory);
        if (!succeeded)
        {
            memory.Dispose();
            memory = new SafeWtsMemoryHandle(
                IntPtr.Zero,
                WindowsPrivacyMethods.WTSFreeMemory);
            return false;
        }

        if (rawBytesReturned > int.MaxValue)
        {
            memory.Dispose();
            memory = new SafeWtsMemoryHandle(
                IntPtr.Zero,
                WindowsPrivacyMethods.WTSFreeMemory);
            return false;
        }

        bytesReturned = checked((int)rawBytesReturned);
        return buffer != IntPtr.Zero;
    }

    private static bool TryGetDesktopName(
        SafeDesktopHandle desktop,
        out string desktopName)
    {
        desktopName = string.Empty;
        _ = WindowsPrivacyMethods.GetUserObjectInformation(
            desktop,
            UserObjectName,
            IntPtr.Zero,
            0,
            out var requiredBytes);
        if (requiredBytes < sizeof(char)
            || requiredBytes > MaximumDesktopNameBytes
            || requiredBytes % sizeof(char) != 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!WindowsPrivacyMethods.GetUserObjectInformation(
                    desktop,
                    UserObjectName,
                    buffer,
                    requiredBytes,
                    out var writtenBytes)
                || writtenBytes < sizeof(char)
                || writtenBytes > requiredBytes)
            {
                return false;
            }

            desktopName = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return desktopName.Length > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct WtsInfoExPrefix
    {
        public uint Level;
        public WtsInfoExLevelUnionPrefix Data;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 16)]
    private struct WtsInfoExLevelUnionPrefix
    {
        [FieldOffset(0)]
        public WtsInfoExLevel1Prefix LevelOne;

        [FieldOffset(0)]
        public long Alignment;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct WtsInfoExLevel1Prefix
    {
        public uint SessionId;
        public WtsConnectState SessionState;
        public int SessionFlags;
    }
}

internal readonly record struct WindowsWtsInfoExLayout(
    int DataOffset,
    int SessionStateOffset,
    int SessionFlagsOffset,
    int MinimumBytes);

internal enum WtsInfoClass
{
    ClientProtocolType = 16,
    SessionInfoEx = 25,
}

internal enum WtsConnectState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9,
}

internal enum UserNotificationState
{
    Unknown = 0,
    NotPresent = 1,
    Busy = 2,
    RunningDirect3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

internal delegate void WtsMemoryRelease(IntPtr memory);

internal sealed class SafeWtsMemoryHandle : SafeHandle
{
    private readonly WtsMemoryRelease _release;

    internal SafeWtsMemoryHandle(IntPtr value, WtsMemoryRelease release)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        SetHandle(value);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        try
        {
            _release(handle);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            handle = IntPtr.Zero;
        }
    }
}

internal delegate bool DesktopRelease(IntPtr desktop);

internal sealed class SafeDesktopHandle : SafeHandle
{
    private readonly DesktopRelease _release;

    internal SafeDesktopHandle(IntPtr value, DesktopRelease release)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        SetHandle(value);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        try
        {
            return _release(handle);
        }
        catch
        {
            return false;
        }
        finally
        {
            handle = IntPtr.Zero;
        }
    }
}

internal static class WindowsPrivacyMethods
{
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformation(
        IntPtr server,
        uint sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out uint bytesReturned);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    internal static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr OpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(
        SafeDesktopHandle handle,
        int index,
        IntPtr information,
        uint informationLength,
        out uint lengthNeeded);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHQueryUserNotificationState(
        out UserNotificationState state);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
