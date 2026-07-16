using System.Reflection;
using System.Runtime.InteropServices;

namespace WinDayFlow.Capture.Interop;

internal enum NativeCaptureResult
{
    Ok = 0,
    NoEvent = 1,
    BufferTooSmall = 2,
    InvalidArgument = -1,
    AbiMismatch = -2,
    InvalidState = -3,
    NotImplemented = -4,
    Timeout = -5,
    PolicyBlocked = -6,
    StalePolicy = -7,
    PolicyRevisionConflict = -8,
    TargetMismatch = -9,
    PolicyRevisionGap = -10,
    GenerationExhausted = -11,
    InternalError = -255,
}

internal enum NativeCaptureEventKind
{
    StateChanged = 1,
    ChunkCommitted = 2,
    Error = 3,
    Diagnostic = 4,
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeCaptureConfigV1
{
    public uint StructSize;
    public uint AbiVersion;
    public uint CaptureIntervalMilliseconds;
    public uint ContextIntervalMilliseconds;
    public uint ChunkDurationMilliseconds;
    public uint MaximumWidth;
    public uint MaximumHeight;
    public uint EventQueueCapacity;
    public nint OutputDirectoryUtf8;
    public uint OutputDirectoryUtf8Length;
    public fixed uint Reserved[8];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeCapturePrivacyContextV1
{
    public uint StructSize;
    public uint AbiVersion;
    public int ConsentGranted;
    public int SessionUnlocked;
    public int SecureDesktopClear;
    public int RemoteSessionAllowed;
    public int PresentationAllowed;
    public int ApplicationAllowed;
    public int WindowAllowed;
    public int StorageAvailable;
    public ulong PolicyRevision;
    public fixed uint Reserved[8];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeCaptureRuntimeAuthorizationV1
{
    internal const uint TargetPresent = 1U << 0;

    public uint StructSize;
    public uint AbiVersion;
    public ulong RuntimePolicyRevision;
    public ulong TargetEpoch;
    public ulong TargetWindowHandle;
    public ulong TargetProcessCreationTime100ns;
    public uint TargetProcessId;
    public uint TargetFlags;
    public int ConsentGranted;
    public int SessionUnlocked;
    public int SecureDesktopClear;
    public int RemoteSessionAllowed;
    public int PresentationAllowed;
    public int ApplicationAllowed;
    public int WindowAllowed;
    public int StorageAvailable;
    public fixed uint Reserved[8];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeCaptureEventV1
{
    public uint StructSize;
    public uint AbiVersion;
    public ulong Sequence;
    public long TimestampUnixMilliseconds;
    public int Kind;
    public int State;
    public int Reason;
    public int Error;
    public uint DroppedBefore;
    public uint DetailUtf8Length;
    public ulong PersistenceGeneration;
    public ulong TargetEpoch;
    public fixed uint Reserved[4];

    public static NativeCaptureEventV1 Create()
    {
        return new NativeCaptureEventV1
        {
            StructSize = checked((uint)sizeof(NativeCaptureEventV1)),
            AbiVersion = NativeCaptureAbiContract.AbiVersion,
        };
    }
}

internal delegate NativeCaptureResult NativeCaptureDestroy(ref nuint handle);

internal sealed class SafeCaptureHandle : SafeHandle
{
    internal SafeCaptureHandle()
        : this(0, NativeCaptureMethods.Destroy)
    {
    }

    internal SafeCaptureHandle(nuint value, NativeCaptureDestroy destroy)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        _destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
        SetHandle(unchecked((nint)value));
    }

    private readonly NativeCaptureDestroy _destroy;
    private readonly object _destroySync = new();
    private bool _explicitDestroyAttempted;

    public override bool IsInvalid => handle == IntPtr.Zero;

    internal NativeCaptureResult DestroyExplicit()
    {
        lock (_destroySync)
        {
            if (_explicitDestroyAttempted || IsInvalid || IsClosed)
            {
                return NativeCaptureResult.Ok;
            }

            _explicitDestroyAttempted = true;
            var value = unchecked((nuint)handle);
            try
            {
                return _destroy(ref value);
            }
            finally
            {
                handle = IntPtr.Zero;
                SetHandleAsInvalid();
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        lock (_destroySync)
        {
            if (_explicitDestroyAttempted)
            {
                handle = IntPtr.Zero;
                return true;
            }

            _explicitDestroyAttempted = true;
            try
            {
                var value = unchecked((nuint)handle);
                return _destroy(ref value) == NativeCaptureResult.Ok;
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
}

internal interface INativeCaptureApi
{
    uint GetAbiVersion();

    NativeCaptureResult GetCapabilities(out NativeCaptureCapabilities capabilities);

    NativeCaptureResult Create(ref NativeCaptureConfigV1 configuration, out nuint handle);

    NativeCaptureResult UpdatePrivacyContext(
        SafeCaptureHandle handle,
        ref NativeCapturePrivacyContextV1 context);

    NativeCaptureResult UpdateRuntimeAuthorization(
        SafeCaptureHandle handle,
        ref NativeCaptureRuntimeAuthorizationV1 authorization,
        out ulong persistenceGeneration)
    {
        persistenceGeneration = 0;
        return NativeCaptureResult.NotImplemented;
    }

    NativeCaptureResult RevokeRuntimeAuthorization(
        SafeCaptureHandle handle,
        out ulong persistenceGeneration)
    {
        persistenceGeneration = 0;
        return NativeCaptureResult.NotImplemented;
    }

    NativeCaptureResult Start(SafeCaptureHandle handle);

    NativeCaptureResult Pause(SafeCaptureHandle handle);

    NativeCaptureResult Resume(SafeCaptureHandle handle);

    NativeCaptureResult RequestStop(SafeCaptureHandle handle);

    NativeCaptureResult WaitStopped(SafeCaptureHandle handle, uint timeoutMilliseconds);

    NativeCaptureResult PollEvent(
        SafeCaptureHandle handle,
        uint timeoutMilliseconds,
        ref NativeCaptureEventV1 captureEvent,
        byte[] detailUtf8,
        uint detailUtf8Capacity,
        out uint detailUtf8Required);

    NativeCaptureResult Destroy(ref nuint handle);
}

internal sealed class PInvokeNativeCaptureApi : INativeCaptureApi
{
    internal static PInvokeNativeCaptureApi Instance { get; } = new();

    private PInvokeNativeCaptureApi()
    {
    }

    public uint GetAbiVersion() => NativeCaptureMethods.wdf_capture_get_abi_version();

    public NativeCaptureResult GetCapabilities(out NativeCaptureCapabilities capabilities) =>
        NativeCaptureMethods.wdf_capture_get_capabilities(out capabilities);

    public NativeCaptureResult Create(
        ref NativeCaptureConfigV1 configuration,
        out nuint handle) =>
        NativeCaptureMethods.wdf_capture_create(ref configuration, out handle);

    public NativeCaptureResult UpdatePrivacyContext(
        SafeCaptureHandle handle,
        ref NativeCapturePrivacyContextV1 context) =>
        NativeCaptureMethods.wdf_capture_update_privacy_context(handle, ref context);

    public NativeCaptureResult UpdateRuntimeAuthorization(
        SafeCaptureHandle handle,
        ref NativeCaptureRuntimeAuthorizationV1 authorization,
        out ulong persistenceGeneration) =>
        NativeCaptureMethods.wdf_capture_update_runtime_authorization(
            handle,
            ref authorization,
            out persistenceGeneration);

    public NativeCaptureResult RevokeRuntimeAuthorization(
        SafeCaptureHandle handle,
        out ulong persistenceGeneration) =>
        NativeCaptureMethods.wdf_capture_revoke_runtime_authorization(
            handle,
            out persistenceGeneration);

    public NativeCaptureResult Start(SafeCaptureHandle handle) =>
        NativeCaptureMethods.wdf_capture_start(handle);

    public NativeCaptureResult Pause(SafeCaptureHandle handle) =>
        NativeCaptureMethods.wdf_capture_pause(handle);

    public NativeCaptureResult Resume(SafeCaptureHandle handle) =>
        NativeCaptureMethods.wdf_capture_resume(handle);

    public NativeCaptureResult RequestStop(SafeCaptureHandle handle) =>
        NativeCaptureMethods.wdf_capture_request_stop(handle);

    public NativeCaptureResult WaitStopped(
        SafeCaptureHandle handle,
        uint timeoutMilliseconds) =>
        NativeCaptureMethods.wdf_capture_wait_stopped(handle, timeoutMilliseconds);

    public NativeCaptureResult PollEvent(
        SafeCaptureHandle handle,
        uint timeoutMilliseconds,
        ref NativeCaptureEventV1 captureEvent,
        byte[] detailUtf8,
        uint detailUtf8Capacity,
        out uint detailUtf8Required) =>
        NativeCaptureMethods.wdf_capture_poll_event(
            handle,
            timeoutMilliseconds,
            ref captureEvent,
            detailUtf8,
            detailUtf8Capacity,
            out detailUtf8Required);

    public NativeCaptureResult Destroy(ref nuint handle) =>
        NativeCaptureMethods.Destroy(ref handle);
}

internal static class NativeCaptureLibrary
{
    private static readonly string ResolvedLibraryPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, NativeCaptureMethods.LibraryName));

    internal static string AbsolutePath => ResolvedLibraryPath;

    internal static void RegisterResolver()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(NativeCaptureLibrary).Assembly,
            ResolveLibrary);
    }

    private static nint ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        _ = assembly;
        _ = searchPath;
        if (!string.Equals(
                libraryName,
                NativeCaptureMethods.LibraryName,
                StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (!File.Exists(ResolvedLibraryPath))
        {
            throw new DllNotFoundException(
                $"The native capture library was not found at the controlled application path: {ResolvedLibraryPath}");
        }

        return NativeLibrary.Load(ResolvedLibraryPath);
    }
}

internal static class NativeCaptureMethods
{
    internal const string LibraryName = "WinDayFlow.Capture.Native.dll";

    static NativeCaptureMethods()
    {
        NativeCaptureLibrary.RegisterResolver();
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint wdf_capture_get_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_get_capabilities(
        out NativeCaptureCapabilities capabilities);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_create(
        ref NativeCaptureConfigV1 configuration,
        out nuint handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_update_privacy_context(
        SafeCaptureHandle handle,
        ref NativeCapturePrivacyContextV1 context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_update_runtime_authorization(
        SafeCaptureHandle handle,
        ref NativeCaptureRuntimeAuthorizationV1 authorization,
        out ulong persistenceGeneration);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_revoke_runtime_authorization(
        SafeCaptureHandle handle,
        out ulong persistenceGeneration);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_start(SafeCaptureHandle handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_pause(SafeCaptureHandle handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_resume(SafeCaptureHandle handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_request_stop(
        SafeCaptureHandle handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_wait_stopped(
        SafeCaptureHandle handle,
        uint timeoutMilliseconds);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeCaptureResult wdf_capture_poll_event(
        SafeCaptureHandle handle,
        uint timeoutMilliseconds,
        ref NativeCaptureEventV1 captureEvent,
        [Out] byte[] detailUtf8,
        uint detailUtf8Capacity,
        out uint detailUtf8Required);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true,
        EntryPoint = "wdf_capture_destroy")]
    internal static extern NativeCaptureResult Destroy(ref nuint handle);
}
