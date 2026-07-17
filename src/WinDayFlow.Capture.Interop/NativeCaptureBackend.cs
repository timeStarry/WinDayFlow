using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using WinDayFlow.Application.Capture;

namespace WinDayFlow.Capture.Interop;

internal sealed class NativeCaptureBackend
    : INativeCaptureRuntimeBackend, IDisposable
{
    private const string FoundationUnavailableDetail =
        "原生录制基础已加载；实时屏幕捕获能力尚未启用。";
    private const int MaximumEventDetailBytes = 1024 * 1024;
    private const uint PollTimeoutMilliseconds = 250;
    internal const uint StopTimeoutMilliseconds = 5_000;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly object _statusSync = new();
    private readonly object _pumpStopSync = new();
    private readonly object _shutdownSync = new();
    private readonly object _persistenceBoundarySync = new();
    private readonly object _disposeOperationSync = new();
    private readonly object _destroyOperationSync = new();
    private readonly INativeCaptureApi _nativeApi;
    private readonly SafeCaptureHandle _handle;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Channel<ManagedNotification> _notifications =
        Channel.CreateUnbounded<ManagedNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private Task _eventPump = Task.CompletedTask;
    private Task _notificationPump = Task.CompletedTask;
    private CaptureStatus _status;
    private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;
    private EventHandler<NativeCaptureChunkCommittedEventArgs>? _chunkCommitted;
    private Task? _pumpStopTask;
    private Task? _requestStopTask;
    private NativePersistenceBoundary _persistenceBoundary = new(0, 0, null);
    private int _shutdownStarted;
    private TaskCompletionSource? _disposeCompletion;
    private TaskCompletionSource<NativeCaptureResult>? _destroyCompletion;
    private int _disposeOwnerThreadId;
    private int _destroyOwnerThreadId;
    private bool _disposed;

    public NativeCaptureBackend(
        NativeCaptureConfiguration configuration,
        NativeCapturePrivacyContext initialPrivacyContext)
        : this(
            configuration,
            initialPrivacyContext,
            PInvokeNativeCaptureApi.Instance)
    {
    }

    internal NativeCaptureBackend(
        NativeCaptureConfiguration configuration,
        NativeCapturePrivacyContext initialPrivacyContext,
        INativeCaptureApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(initialPrivacyContext);
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        EnsureSupportedPlatform();

        var probe = Probe(_nativeApi);
        if (!probe.LibraryLoaded)
        {
            throw new DllNotFoundException(
                probe.Failure ?? "The native capture library could not be loaded.");
        }

        if (!probe.AbiCompatible)
        {
            throw new BadImageFormatException(
                probe.Failure
                ?? $"Native capture ABI {probe.AbiVersion} is incompatible with managed ABI {NativeCaptureAbiContract.AbiVersion}.");
        }

        var requiredFoundation = NativeCaptureAbiContract.FoundationCapabilities;
        if ((probe.Capabilities & requiredFoundation) != requiredFoundation)
        {
            throw new BadImageFormatException(
                "The native capture library does not provide the required privacy guard and event queue capabilities.");
        }

        Capabilities = probe.Capabilities;
        _status = CreateUnavailableStatus(
            sequence: 0,
            DateTimeOffset.UnixEpoch,
            FoundationUnavailableDetail);
        _handle = CreateHandle(configuration, _nativeApi);
        try
        {
            if (SupportsRuntimeAuthorization)
            {
                _ = UpdateRuntimeAuthorizationCore(
                    new NativeCaptureRuntimeAuthorization(
                        initialPrivacyContext,
                        NativeCaptureTargetIdentity.Unknown));
            }
            else
            {
                UpdatePrivacyContextCore(initialPrivacyContext);
            }

            _notificationPump = Task.Run(DispatchNotificationsAsync);
            _eventPump = Task.Run(PollEventsAsync);
        }
        catch
        {
            _notifications.Writer.TryComplete();
            _handle.Dispose();
            throw;
        }
    }

    public NativeCaptureCapabilities Capabilities { get; }

    public bool SupportsScreenCapture =>
        (Capabilities & NativeCaptureAbiContract.SafeScreenCaptureCapabilities)
        == NativeCaptureAbiContract.SafeScreenCaptureCapabilities;

    public bool SupportsRuntimeAuthorization =>
        (Capabilities & (
            NativeCaptureCapabilities.TargetScopedAuthorization
            | NativeCaptureCapabilities.PersistenceGenerationBarrier
            | NativeCaptureCapabilities.DisplayScopedAuthorization))
        == (NativeCaptureCapabilities.TargetScopedAuthorization
            | NativeCaptureCapabilities.PersistenceGenerationBarrier
            | NativeCaptureCapabilities.DisplayScopedAuthorization);

    public bool SupportsCommandAdmission =>
        (Capabilities & NativeCaptureAbiContract.RuntimeOwnerCapabilities)
        == NativeCaptureAbiContract.RuntimeOwnerCapabilities;

    internal bool IsShutdownStarted =>
        Volatile.Read(ref _shutdownStarted) != 0;

    public CaptureStatus CurrentStatus
    {
        get
        {
            lock (_statusSync)
            {
                return _status;
            }
        }
    }

    public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
    {
        add
        {
            lock (_statusSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _statusChanged += value;
            }
        }
        remove
        {
            lock (_statusSync)
            {
                _statusChanged -= value;
            }
        }
    }

    public event EventHandler<NativeCaptureChunkCommittedEventArgs>? ChunkCommitted
    {
        add
        {
            lock (_statusSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _chunkCommitted += value;
            }
        }
        remove
        {
            lock (_statusSync)
            {
                _chunkCommitted -= value;
            }
        }
    }

    public static NativeCaptureProbe Probe()
    {
        if (!IsSupportedPlatform())
        {
            return new NativeCaptureProbe(
                LibraryLoaded: false,
                AbiCompatible: false,
                AbiVersion: 0,
                NativeCaptureCapabilities.None,
                "PlatformNotSupported: WinDayFlow native capture currently requires a Windows x64 process.");
        }

        return Probe(PInvokeNativeCaptureApi.Instance);
    }

    internal static NativeCaptureProbe Probe(INativeCaptureApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        try
        {
            var abiVersion = nativeApi.GetAbiVersion();
            if (abiVersion != NativeCaptureAbiContract.AbiVersion)
            {
                return new NativeCaptureProbe(
                    LibraryLoaded: true,
                    AbiCompatible: false,
                    abiVersion,
                    NativeCaptureCapabilities.None,
                    $"Native capture ABI {abiVersion} is incompatible with managed ABI {NativeCaptureAbiContract.AbiVersion}.");
            }

            var capabilitiesResult = nativeApi.GetCapabilities(out var capabilities);
            if (capabilitiesResult != NativeCaptureResult.Ok)
            {
                return new NativeCaptureProbe(
                    LibraryLoaded: true,
                    AbiCompatible: true,
                    abiVersion,
                    NativeCaptureCapabilities.None,
                    $"Capability probe failed with result {(int)capabilitiesResult}.");
            }

            if (GetCapabilityContractFailure(capabilities) is { } capabilityFailure)
            {
                return new NativeCaptureProbe(
                    LibraryLoaded: true,
                    AbiCompatible: false,
                    abiVersion,
                    capabilities,
                    capabilityFailure);
            }

            return new NativeCaptureProbe(
                LibraryLoaded: true,
                AbiCompatible: true,
                abiVersion,
                capabilities,
                Failure: null);
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                          or EntryPointNotFoundException
                                          or BadImageFormatException)
        {
            return new NativeCaptureProbe(
                LibraryLoaded: false,
                AbiCompatible: false,
                AbiVersion: 0,
                NativeCaptureCapabilities.None,
                exception.GetType().Name);
        }
    }

    public async Task<NativeCaptureCommandAdmissionV1?> TryIssueCommandAdmissionAsync(
        CaptureAdmissionOperation operation,
        ulong expectedRuntimePolicyRevision,
        ulong expectedPersistenceGeneration,
        ulong expectedTargetEpoch,
        CancellationToken cancellationToken = default)
    {
        EnsureCommandAdmissionCapability();
        ArgumentOutOfRangeException.ThrowIfZero(expectedRuntimePolicyRevision);
        ArgumentOutOfRangeException.ThrowIfZero(expectedPersistenceGeneration);
        ArgumentOutOfRangeException.ThrowIfZero(expectedTargetEpoch);
        var command = operation switch
        {
            CaptureAdmissionOperation.Start => NativeCaptureCommand.Start,
            CaptureAdmissionOperation.Resume => NativeCaptureCommand.Resume,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfShuttingDown();
            var admission = NativeCaptureCommandAdmissionV1.Create();
            var result = _nativeApi.IssueCommandAdmission(
                _handle,
                command,
                expectedPersistenceGeneration,
                expectedTargetEpoch,
                ref admission);
            if (result is NativeCaptureResult.AdmissionRejected
                or NativeCaptureResult.PolicyBlocked
                or NativeCaptureResult.InvalidState)
            {
                return null;
            }

            ThrowForResult(result, "issue_command_admission");
            ValidateCommandAdmission(
                admission,
                expectedRuntimePolicyRevision,
                expectedPersistenceGeneration,
                expectedTargetEpoch);
            return admission;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task StartAuthorizedAsync(
        NativeCaptureCommandAdmissionV1 admission,
        CancellationToken cancellationToken = default) =>
        InvokeAuthorizedLifecycleAsync(
            "start_authorized",
            admission,
            start: true,
            cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        InvokeLifecycleAsync(
            "pause",
            _nativeApi.Pause,
            cancellationToken);

    public Task ResumeAuthorizedAsync(
        NativeCaptureCommandAdmissionV1 admission,
        CancellationToken cancellationToken = default) =>
        InvokeAuthorizedLifecycleAsync(
            "resume_authorized",
            admission,
            start: false,
            cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        EnsureScreenCaptureCapability();
        await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowForResult(
                _nativeApi.RequestStop(_handle),
                "request_stop");
            ThrowForResult(
                _nativeApi.WaitStopped(
                    _handle,
                    StopTimeoutMilliseconds),
                "wait_stopped");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task UpdatePrivacyContextAsync(
        NativeCapturePrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfShuttingDown();
            UpdatePrivacyContextCore(context);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<ulong> UpdateRuntimeAuthorizationAsync(
        NativeCaptureRuntimeAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        EnsureRuntimeAuthorizationCapability();
        await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfShuttingDown();
            return UpdateRuntimeAuthorizationCore(authorization);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        TaskCompletionSource? ownedCompletion = null;
        Task completionTask;
        lock (_disposeOperationSync)
        {
            if (_disposeCompletion is not null)
            {
                if (_disposeOwnerThreadId == Environment.CurrentManagedThreadId)
                {
                    return;
                }

                completionTask = _disposeCompletion.Task;
            }
            else
            {
                ownedCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeCompletion = ownedCompletion;
                _disposeOwnerThreadId = Environment.CurrentManagedThreadId;
                completionTask = ownedCompletion.Task;
            }
        }

        if (ownedCompletion is null)
        {
            completionTask.GetAwaiter().GetResult();
            return;
        }

        try
        {
            DisposeCore();
            ownedCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            ownedCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _disposeOwnerThreadId, 0);
        }
    }

    private void DisposeCore()
    {
        if (IsDisposed())
        {
            return;
        }

        Interlocked.Exchange(ref _shutdownStarted, 1);

        if (SupportsRuntimeAuthorization)
        {
            try
            {
                RevokeRuntimeAuthorizationAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Native capture authorization revoke failed during dispose: {exception}");
            }
        }

        try
        {
            RequestStopForShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Native capture stop request failed during dispose: {exception}");
        }

        try
        {
            WaitStoppedForShutdownAsync(StopTimeoutMilliseconds).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Native capture wait failed during dispose: {exception}");
        }

        try
        {
            StopEventPumpAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Native capture event pump failed during dispose: {exception}");
        }

        try
        {
            var destroyResult = DestroyForShutdownCore();
            if (destroyResult != NativeCaptureResult.Ok)
            {
                Debug.WriteLine(
                    $"Native capture destroy failed during dispose with result {(int)destroyResult}.");
            }
        }
        finally
        {
            CompleteOwnedShutdown();
        }
    }

    Task INativeCaptureRuntimeBackend.RequestStopForShutdownAsync() =>
        RequestStopForShutdownAsync();

    private Task RequestStopForShutdownAsync()
    {
        lock (_shutdownSync)
        {
            _requestStopTask ??= RequestStopForShutdownCoreAsync();
            return _requestStopTask;
        }
    }

    Task INativeCaptureRuntimeBackend.WaitStoppedForShutdownAsync(
        uint timeoutMilliseconds) =>
        WaitStoppedForShutdownAsync(timeoutMilliseconds);

    private async Task WaitStoppedForShutdownAsync(uint timeoutMilliseconds)
    {
        if (timeoutMilliseconds > 60_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds),
                timeoutMilliseconds,
                "The native stop wait must be bounded to at most 60000 milliseconds.");
        }

        await EnterLifecycleForShutdownAsync().ConfigureAwait(false);
        try
        {
            ThrowForResult(
                _nativeApi.WaitStopped(_handle, timeoutMilliseconds),
                "wait_stopped");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<ulong> RevokeRuntimeAuthorizationAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        EnsureRuntimeAuthorizationCapability();
        await EnterLifecycleForShutdownAsync().ConfigureAwait(false);
        try
        {
            lock (_persistenceBoundarySync)
            {
                ThrowForResult(
                    _nativeApi.RevokeRuntimeAuthorization(
                        _handle,
                        out var persistenceGeneration),
                    "revoke_runtime_authorization");
                if (persistenceGeneration == 0)
                {
                    throw new InvalidDataException(
                        "The native runtime authorization revoke returned no persistence generation.");
                }

                var currentBoundary = Volatile.Read(ref _persistenceBoundary);
                if (persistenceGeneration < currentBoundary.PersistenceGeneration
                    || (persistenceGeneration == currentBoundary.PersistenceGeneration
                        && currentBoundary.TargetEpoch != 0))
                {
                    throw new InvalidDataException(
                        "The native runtime authorization revoke returned a regressed persistence generation.");
                }

                Volatile.Write(
                    ref _persistenceBoundary,
                    new NativePersistenceBoundary(persistenceGeneration, 0, null));
                return persistenceGeneration;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    Task INativeCaptureRuntimeBackend.StopEventPumpAsync() =>
        StopEventPumpAsync();

    private Task StopEventPumpAsync()
    {
        lock (_pumpStopSync)
        {
            _pumpStopTask ??= StopEventPumpCoreAsync();
            return _pumpStopTask;
        }
    }

    NativeCaptureResult INativeCaptureRuntimeBackend.DestroyForShutdown() =>
        DestroyForShutdownCore();

    private NativeCaptureResult DestroyForShutdownCore()
    {
        TaskCompletionSource<NativeCaptureResult>? ownedCompletion = null;
        Task<NativeCaptureResult> completionTask;
        lock (_destroyOperationSync)
        {
            if (_destroyCompletion is not null)
            {
                if (_destroyOwnerThreadId == Environment.CurrentManagedThreadId)
                {
                    return NativeCaptureResult.InternalError;
                }

                completionTask = _destroyCompletion.Task;
            }
            else
            {
                ownedCompletion = new TaskCompletionSource<NativeCaptureResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _destroyCompletion = ownedCompletion;
                _destroyOwnerThreadId = Environment.CurrentManagedThreadId;
                completionTask = ownedCompletion.Task;
            }
        }

        if (ownedCompletion is null)
        {
            return completionTask.GetAwaiter().GetResult();
        }

        try
        {
            NativeCaptureResult result;
            _lifecycleGate.Wait();
            try
            {
                result = _handle.DestroyExplicit();
            }
            finally
            {
                _handle.Dispose();
                _lifecycleGate.Release();
            }

            ownedCompletion.TrySetResult(result);
            return result;
        }
        catch (Exception exception)
        {
            ownedCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _destroyOwnerThreadId, 0);
        }
    }

    void INativeCaptureRuntimeBackend.CompleteOwnedShutdown() =>
        CompleteOwnedShutdown();

    private void CompleteOwnedShutdown()
    {
        lock (_statusSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _statusChanged = null;
            _chunkCommitted = null;
        }

        _lifetimeCancellation.Dispose();
        _lifecycleGate.Dispose();
    }

    void INativeCaptureRuntimeBackend.DisposeSafelyAfterConstructionFailure() =>
        Dispose();

    private static unsafe SafeCaptureHandle CreateHandle(
        NativeCaptureConfiguration configuration,
        INativeCaptureApi nativeApi)
    {
        if (configuration.OutputDirectory.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The native capture output directory cannot contain NUL characters.",
                nameof(configuration));
        }

        var outputDirectoryUtf8 = StrictUtf8.GetBytes(configuration.OutputDirectory);
        if (outputDirectoryUtf8.Length is 0 or > 32_767)
        {
            throw new ArgumentException(
                "The UTF-8 native capture output directory must contain at most 32767 bytes.",
                nameof(configuration));
        }

        fixed (byte* outputDirectoryPointer = outputDirectoryUtf8)
        {
            var nativeConfiguration = new NativeCaptureConfigV1
            {
                StructSize = checked((uint)sizeof(NativeCaptureConfigV1)),
                AbiVersion = NativeCaptureAbiContract.AbiVersion,
                CaptureIntervalMilliseconds = configuration.CaptureIntervalMilliseconds,
                ContextIntervalMilliseconds = configuration.ContextIntervalMilliseconds,
                ChunkDurationMilliseconds = configuration.ChunkDurationMilliseconds,
                MaximumWidth = configuration.MaximumWidth,
                MaximumHeight = configuration.MaximumHeight,
                EventQueueCapacity = configuration.EventQueueCapacity,
                OutputDirectoryUtf8 = (nint)outputDirectoryPointer,
                OutputDirectoryUtf8Length = checked((uint)outputDirectoryUtf8.Length),
            };
            ThrowForResult(
                nativeApi.Create(
                    ref nativeConfiguration,
                    out var rawHandle),
                "create");
            if (rawHandle == 0)
            {
                throw new NativeCaptureException(
                    NativeCaptureResult.InternalError,
                    "create_zero_handle");
            }

            return new SafeCaptureHandle(rawHandle, nativeApi.Destroy);
        }
    }

    private async Task InvokeLifecycleAsync(
        string operation,
        Func<SafeCaptureHandle, NativeCaptureResult> nativeOperation,
        CancellationToken cancellationToken)
    {
        EnsureScreenCaptureCapability();
        await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfShuttingDown();
            ThrowForResult(nativeOperation(_handle), operation);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task InvokeAuthorizedLifecycleAsync(
        string operation,
        NativeCaptureCommandAdmissionV1 admission,
        bool start,
        CancellationToken cancellationToken)
    {
        EnsureCommandAdmissionCapability();
        ValidateCommandAdmission(admission);
        await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfShuttingDown();
            var result = start
                ? _nativeApi.StartAuthorized(_handle, ref admission)
                : _nativeApi.ResumeAuthorized(_handle, ref admission);
            if (result is NativeCaptureResult.AdmissionRejected
                or NativeCaptureResult.PolicyBlocked
                or NativeCaptureResult.InvalidState)
            {
                throw new CaptureRuntimeAdmissionRejectedException();
            }

            ThrowForResult(result, operation);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task EnterLifecycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfShuttingDown();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _lifecycleGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
    }

    private async Task EnterLifecycleForShutdownAsync()
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void UpdatePrivacyContextCore(NativeCapturePrivacyContext context)
    {
        var nativeContext = new NativeCapturePrivacyContextV1
        {
            StructSize = NativeCaptureAbiContract.X64StructureSize,
            AbiVersion = NativeCaptureAbiContract.AbiVersion,
            ConsentGranted = (int)context.ConsentGranted,
            SessionUnlocked = (int)context.SessionUnlocked,
            SecureDesktopClear = (int)context.SecureDesktopClear,
            RemoteSessionAllowed = (int)context.RemoteSessionAllowed,
            PresentationAllowed = (int)context.PresentationAllowed,
            ApplicationAllowed = (int)context.ApplicationAllowed,
            WindowAllowed = (int)context.WindowAllowed,
            StorageAvailable = (int)context.StorageAvailable,
            PolicyRevision = context.RuntimePolicyRevision,
        };
        lock (_persistenceBoundarySync)
        {
            ThrowForResult(
                _nativeApi.UpdatePrivacyContext(
                    _handle,
                    ref nativeContext),
                "update_privacy_context");
            if (SupportsRuntimeAuthorization)
            {
                Volatile.Write(
                    ref _persistenceBoundary,
                    new NativePersistenceBoundary(0, 0, null));
            }
        }
    }

    private unsafe ulong UpdateRuntimeAuthorizationCore(
        NativeCaptureRuntimeAuthorization authorization)
    {
        var context = authorization.PrivacyContext;
        var target = authorization.Target;
        var targetPresent = target.State == NativeCaptureTargetIdentityState.Present;
        var nativeAuthorization = new NativeCaptureRuntimeAuthorizationV1
        {
            StructSize = NativeCaptureAbiContract.X64RuntimeAuthorizationStructureSize,
            AbiVersion = NativeCaptureAbiContract.AbiVersion,
            RuntimePolicyRevision = context.RuntimePolicyRevision,
            TargetEpoch = targetPresent ? target.TargetEpoch : 0,
            TargetWindowHandle = targetPresent ? target.WindowHandle : 0,
            TargetProcessCreationTime100ns = targetPresent
                ? target.ProcessCreationTime100ns
                : 0,
            TargetProcessId = targetPresent ? target.ProcessId : 0,
            TargetFlags = targetPresent
                ? NativeCaptureRuntimeAuthorizationV1.TargetPresent
                    | NativeCaptureRuntimeAuthorizationV1.TargetDisplayPresent
                : 0,
            ConsentGranted = (int)context.ConsentGranted,
            SessionUnlocked = (int)context.SessionUnlocked,
            SecureDesktopClear = (int)context.SecureDesktopClear,
            RemoteSessionAllowed = (int)context.RemoteSessionAllowed,
            PresentationAllowed = (int)context.PresentationAllowed,
            ApplicationAllowed = (int)context.ApplicationAllowed,
            WindowAllowed = (int)context.WindowAllowed,
            StorageAvailable = (int)context.StorageAvailable,
            TargetDisplayMonitorHandle = targetPresent
                ? target.DisplayMonitorHandle
                : 0,
        };

        try
        {
            if (targetPresent)
            {
                byte* displayDeviceKeyBuffer =
                    nativeAuthorization.TargetDisplayDeviceKeyUtf8;
                var destination = new Span<byte>(
                    displayDeviceKeyBuffer,
                    NativeCaptureAbiContract.DisplayDeviceKeyUtf8Capacity);
                destination.Clear();
                var encodedLength = StrictUtf8.GetBytes(
                    target.DisplayDeviceKey.AsSpan(),
                    destination);
                if (encodedLength is <= 0
                    or > NativeCaptureAbiContract
                        .DisplayDeviceKeyUtf8MaximumLength)
                {
                    throw new InvalidDataException(
                        "The capture display device key does not satisfy the native UTF-8 ABI bound.");
                }

                nativeAuthorization.TargetDisplayDeviceKeyUtf8Length =
                    checked((uint)encodedLength);
            }

            lock (_persistenceBoundarySync)
            {
                ThrowForResult(
                    _nativeApi.UpdateRuntimeAuthorization(
                        _handle,
                        ref nativeAuthorization,
                        out var persistenceGeneration),
                    "update_runtime_authorization");
                if (persistenceGeneration == 0)
                {
                    throw new InvalidDataException(
                        "The native runtime authorization update returned no persistence generation.");
                }

                var currentBoundary = Volatile.Read(ref _persistenceBoundary);
                if (persistenceGeneration < currentBoundary.PersistenceGeneration
                    || (persistenceGeneration == currentBoundary.PersistenceGeneration
                        && !Equals(currentBoundary.Authorization, authorization)))
                {
                    throw new InvalidDataException(
                        "The native runtime authorization update returned a regressed or conflicting persistence generation.");
                }

                Volatile.Write(
                    ref _persistenceBoundary,
                    new NativePersistenceBoundary(
                        persistenceGeneration,
                        targetPresent ? target.TargetEpoch : 0,
                        authorization));
                return persistenceGeneration;
            }
        }
        finally
        {
            byte* displayDeviceKeyBuffer =
                nativeAuthorization.TargetDisplayDeviceKeyUtf8;
            new Span<byte>(
                displayDeviceKeyBuffer,
                NativeCaptureAbiContract.DisplayDeviceKeyUtf8Capacity)
                .Clear();

            nativeAuthorization.TargetDisplayDeviceKeyUtf8Length = 0;
            nativeAuthorization.TargetDisplayMonitorHandle = 0;
            nativeAuthorization.TargetFlags = 0;
        }
    }

    private async Task RequestStopForShutdownCoreAsync()
    {
        Interlocked.Exchange(ref _shutdownStarted, 1);
        await EnterLifecycleForShutdownAsync().ConfigureAwait(false);
        try
        {
            ThrowForResult(_nativeApi.RequestStop(_handle), "request_stop");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopEventPumpCoreAsync()
    {
        _lifetimeCancellation.Cancel();
        try
        {
            await _eventPump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _notifications.Writer.TryComplete();
        }

    }

    private async Task PollEventsAsync()
    {
        var detailBuffer = new byte[4_096];
        while (!_lifetimeCancellation.IsCancellationRequested)
        {
            try
            {
                var captureEvent = NativeCaptureEventV1.Create();
                var result = _nativeApi.PollEvent(
                    _handle,
                    PollTimeoutMilliseconds,
                    ref captureEvent,
                    detailBuffer,
                    checked((uint)detailBuffer.Length),
                    out var detailRequired);
                if (result == NativeCaptureResult.NoEvent)
                {
                    continue;
                }

                if (result == NativeCaptureResult.BufferTooSmall)
                {
                    if (detailRequired <= detailBuffer.Length
                        || detailRequired > MaximumEventDetailBytes)
                    {
                        PublishManagedFault(
                            "Native event detail reported an invalid required buffer size.");
                        return;
                    }

                    detailBuffer = new byte[Math.Max(1, checked((int)detailRequired))];
                    continue;
                }

                if (result == NativeCaptureResult.InvalidState
                    && _lifetimeCancellation.IsCancellationRequested)
                {
                    return;
                }

                if (result != NativeCaptureResult.Ok)
                {
                    throw new NativeCaptureException(result, "poll_event");
                }

                if (!ProcessEvent(captureEvent, detailBuffer))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!_lifetimeCancellation.IsCancellationRequested)
                {
                    PublishManagedFault(exception.GetType().Name);
                }

                return;
            }

            await Task.Yield();
        }
    }

    private bool ProcessEvent(NativeCaptureEventV1 captureEvent, byte[] detailBuffer)
    {
        if (captureEvent.StructSize < NativeCaptureAbiContract.X64StructureSize
            || captureEvent.AbiVersion != NativeCaptureAbiContract.AbiVersion
            || captureEvent.Sequence == 0
            || captureEvent.DetailUtf8Length > detailBuffer.Length
            || !Enum.IsDefined((NativeCaptureEventKind)captureEvent.Kind))
        {
            throw new InvalidDataException("The native capture event contract is invalid.");
        }

        var eventKind = (NativeCaptureEventKind)captureEvent.Kind;
        var state = (CaptureState)captureEvent.State;
        var reason = (CaptureReasonCode)captureEvent.Reason;
        var error = (CaptureErrorCode)captureEvent.Error;
        if (!Enum.IsDefined(state) || !Enum.IsDefined(reason) || !Enum.IsDefined(error))
        {
            throw new InvalidDataException("The native capture event contains an unknown code.");
        }

        if (eventKind == NativeCaptureEventKind.ChunkCommitted)
        {
            if (!SupportsScreenCapture)
            {
                throw new InvalidDataException(
                    "A native binary without the complete live-capture safety capability set published a committed chunk.");
            }

            lock (_persistenceBoundarySync)
            {
                var persistenceBoundary = Volatile.Read(ref _persistenceBoundary);
                if (captureEvent.PersistenceGeneration == 0
                    || captureEvent.TargetEpoch == 0
                    || captureEvent.PersistenceGeneration
                        != persistenceBoundary.PersistenceGeneration
                    || captureEvent.TargetEpoch
                        != persistenceBoundary.TargetEpoch)
                {
                    throw new InvalidDataException(
                        "The native committed chunk was not bound to the current persistence generation and capture target.");
                }
            }
        }

        var detail = captureEvent.DetailUtf8Length == 0
            ? null
            : StrictUtf8.GetString(
                detailBuffer,
                0,
                checked((int)captureEvent.DetailUtf8Length));
        var changedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            captureEvent.TimestampUnixMilliseconds);

        if (captureEvent.DroppedBefore > 0)
        {
            UpdateStatus(new CaptureStatus(
                CaptureState.Faulted,
                changedAt,
                $"Native capture event delivery dropped {captureEvent.DroppedBefore} event(s); the capture state can no longer be trusted.",
                captureEvent.Sequence,
                CaptureReasonCode.BackendFault,
                CaptureErrorCode.NativeFailure));

            if (eventKind == NativeCaptureEventKind.ChunkCommitted)
            {
                QueueChunkCommitted(CreateChunk(captureEvent, changedAt, detail, state));
            }

            RequestStopAfterManagedFault();
            return false;
        }

        if (eventKind == NativeCaptureEventKind.ChunkCommitted)
        {
            QueueChunkCommitted(CreateChunk(captureEvent, changedAt, detail, state));
            return true;
        }

        if (eventKind == NativeCaptureEventKind.Diagnostic)
        {
            return true;
        }

        if (eventKind == NativeCaptureEventKind.Error)
        {
            state = CaptureState.Faulted;
            if (error == CaptureErrorCode.None)
            {
                error = CaptureErrorCode.Unknown;
            }

            if (reason == CaptureReasonCode.None)
            {
                reason = CaptureReasonCode.BackendFault;
            }
        }

        CaptureStatus status;
        if (!SupportsScreenCapture && eventKind != NativeCaptureEventKind.Error)
        {
            status = CreateUnavailableStatus(
                captureEvent.Sequence,
                changedAt,
                detail);
        }
        else
        {
            status = new CaptureStatus(
                state,
                changedAt,
                detail,
                captureEvent.Sequence,
                reason,
                error);
        }

        UpdateStatus(status);
        return true;
    }

    private static NativeCaptureChunkCommitted CreateChunk(
        NativeCaptureEventV1 captureEvent,
        DateTimeOffset committedAt,
        string? artifactIdentifier,
        CaptureState state)
    {
        if (string.IsNullOrWhiteSpace(artifactIdentifier))
        {
            throw new InvalidDataException(
                "A native chunk-committed event did not contain an artifact identifier.");
        }

        return new NativeCaptureChunkCommitted(
            captureEvent.Sequence,
            committedAt,
            artifactIdentifier,
            state,
            captureEvent.DroppedBefore,
            captureEvent.PersistenceGeneration,
            captureEvent.TargetEpoch);
    }

    private void QueueChunkCommitted(NativeCaptureChunkCommitted chunk)
    {
        lock (_statusSync)
        {
            if (_disposed)
            {
                return;
            }
        }

        _notifications.Writer.TryWrite(new ChunkCommittedNotification(chunk));
    }

    private void PublishManagedFault(string detail)
    {
        ulong sequence;
        lock (_statusSync)
        {
            if (_disposed || _status.Sequence == ulong.MaxValue)
            {
                return;
            }

            sequence = _status.Sequence + 1;
        }

        UpdateStatus(new CaptureStatus(
            CaptureState.Faulted,
            DateTimeOffset.UtcNow,
            detail,
            sequence,
            CaptureReasonCode.BackendFault,
            CaptureErrorCode.NativeFailure));
        RequestStopAfterManagedFault();
    }

    private void RequestStopAfterManagedFault()
    {
        _ = RequestStopAfterManagedFaultAsync();
    }

    private async Task RequestStopAfterManagedFaultAsync()
    {
        try
        {
            await RequestStopForShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Native capture fail-closed stop request failed: {exception}");
        }
    }

    private void UpdateStatus(CaptureStatus current)
    {
        CaptureStatus previous;
        lock (_statusSync)
        {
            if (_disposed
                || (_status.Sequence > 0 && current.Sequence <= _status.Sequence)
                || current == _status)
            {
                return;
            }

            previous = _status;
            _status = current;
        }

        _notifications.Writer.TryWrite(new StatusChangedNotification(previous, current));
    }

    private async Task DispatchNotificationsAsync()
    {
        try
        {
            await foreach (var notification in _notifications.Reader.ReadAllAsync()
                               .ConfigureAwait(false))
            {
                switch (notification)
                {
                    case StatusChangedNotification statusChanged:
                        DispatchStatusChanged(statusChanged);
                        break;
                    case ChunkCommittedNotification chunkCommitted:
                        DispatchChunkCommitted(chunkCommitted);
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Native capture notification dispatch stopped: {exception}");
        }
    }

    private void DispatchStatusChanged(StatusChangedNotification notification)
    {
        EventHandler<CaptureStatusChangedEventArgs>? handler;
        lock (_statusSync)
        {
            if (_disposed)
            {
                return;
            }

            handler = _statusChanged;
        }

        var args = new CaptureStatusChangedEventArgs(
            notification.Previous,
            notification.Current);
        InvokeSubscribers(handler, args, "status");
    }

    private void DispatchChunkCommitted(ChunkCommittedNotification notification)
    {
        EventHandler<NativeCaptureChunkCommittedEventArgs>? handler;
        lock (_statusSync)
        {
            if (_disposed)
            {
                return;
            }

            handler = _chunkCommitted;
        }

        InvokeSubscribers(
            handler,
            new NativeCaptureChunkCommittedEventArgs(notification.Chunk),
            "chunk");
    }

    private void InvokeSubscribers<TEventArgs>(
        EventHandler<TEventArgs>? handler,
        TEventArgs args,
        string notificationKind)
        where TEventArgs : EventArgs
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            lock (_statusSync)
            {
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                ((EventHandler<TEventArgs>)subscriber)(this, args);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Native capture {notificationKind} subscriber failed: {exception}");
            }
        }
    }

    private static CaptureStatus CreateUnavailableStatus(
        ulong sequence,
        DateTimeOffset changedAt,
        string? detail)
    {
        return new CaptureStatus(
            CaptureState.Unavailable,
            changedAt,
            string.IsNullOrWhiteSpace(detail) ? FoundationUnavailableDetail : detail,
            sequence,
            CaptureReasonCode.BackendUnavailable);
    }

    private static bool IsSupportedPlatform()
    {
        return OperatingSystem.IsWindows()
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    }

    private static string? GetCapabilityContractFailure(
        NativeCaptureCapabilities capabilities)
    {
        const NativeCaptureCapabilities safetyTrio =
            NativeCaptureCapabilities.TargetScopedAuthorization
            | NativeCaptureCapabilities.PersistenceGenerationBarrier
            | NativeCaptureCapabilities.DeterministicStop;
        var advertisedSafety = capabilities & safetyTrio;
        if (advertisedSafety != NativeCaptureCapabilities.None
            && advertisedSafety != safetyTrio)
        {
            return "The native capture binary advertises only part of the required runtime safety capability set.";
        }

        if (advertisedSafety == safetyTrio
            && (capabilities & NativeCaptureAbiContract.FoundationCapabilities)
                != NativeCaptureAbiContract.FoundationCapabilities)
        {
            return "The native capture runtime safety capabilities require the privacy guard and event queue foundation.";
        }

        if (capabilities.HasFlag(
                NativeCaptureCapabilities.DisplayScopedAuthorization)
            && advertisedSafety != safetyTrio)
        {
            return "Native display-scoped authorization requires the complete runtime safety set.";
        }

        if (capabilities.HasFlag(NativeCaptureCapabilities.CommandAdmission)
            && advertisedSafety != safetyTrio)
        {
            return "The legacy native command-admission capability requires the complete legacy runtime safety set.";
        }

        if (capabilities.HasFlag(NativeCaptureCapabilities.CommandAdmission)
            && capabilities.HasFlag(
                NativeCaptureCapabilities.DisplayBoundCommandAdmission))
        {
            return "The native capture binary advertises both legacy and display-bound command-admission profiles.";
        }

        if (capabilities.HasFlag(
                NativeCaptureCapabilities.DisplayBoundCommandAdmission)
            && (capabilities & NativeCaptureAbiContract.RuntimeOwnerCapabilities)
                != NativeCaptureAbiContract.RuntimeOwnerCapabilities)
        {
            return "Native display-bound command admission requires the complete display-scoped runtime-owner safety set.";
        }

        var screenCapture = capabilities.HasFlag(
            NativeCaptureCapabilities.ScreenCapture);
        var h264Chunks = capabilities.HasFlag(
            NativeCaptureCapabilities.H264Chunks);
        if (h264Chunks && !screenCapture)
        {
            return "The native H.264 chunk capability requires screen capture support.";
        }

        if (screenCapture
            && (capabilities & NativeCaptureAbiContract.SafeScreenCaptureCapabilities)
                != NativeCaptureAbiContract.SafeScreenCaptureCapabilities)
        {
            return "The native screen capture capability requires the complete display-scoped runtime safety set and H.264 chunk support.";
        }

        return null;
    }

    private static void EnsureSupportedPlatform()
    {
        if (!IsSupportedPlatform())
        {
            throw new PlatformNotSupportedException(
                "The native capture foundation currently requires a Windows x64 process.");
        }
    }

    private void EnsureScreenCaptureCapability()
    {
        ThrowIfDisposed();
        if (!SupportsScreenCapture)
        {
            throw new NotSupportedException(FoundationUnavailableDetail);
        }
    }

    private void EnsureRuntimeAuthorizationCapability()
    {
        ThrowIfDisposed();
        if (!SupportsRuntimeAuthorization)
        {
            throw new NotSupportedException(
                "The native capture binary does not provide display-scoped runtime authorization and a persistence generation barrier.");
        }
    }

    private void EnsureCommandAdmissionCapability()
    {
        ThrowIfDisposed();
        if (!SupportsCommandAdmission)
        {
            throw new NotSupportedException(
                "The native capture binary does not provide owner-bound command admission.");
        }
    }

    private static void ValidateCommandAdmission(
        NativeCaptureCommandAdmissionV1 admission,
        ulong? expectedRuntimePolicyRevision = null,
        ulong? expectedPersistenceGeneration = null,
        ulong? expectedTargetEpoch = null)
    {
        if (admission.StructSize != NativeCaptureAbiContract.CommandAdmissionStructureSize
            || admission.AbiVersion != NativeCaptureAbiContract.AbiVersion
            || admission.InstanceEpoch == 0
            || admission.RuntimePolicyRevision == 0
            || admission.PersistenceGeneration == 0
            || admission.TargetEpoch == 0
            || admission.AuthorizationEpoch == 0
            || (admission.NonceLow == 0 && admission.NonceHigh == 0)
            || (expectedRuntimePolicyRevision is { } runtimePolicyRevision
                && admission.RuntimePolicyRevision != runtimePolicyRevision)
            || (expectedPersistenceGeneration is { } persistenceGeneration
                && admission.PersistenceGeneration != persistenceGeneration)
            || (expectedTargetEpoch is { } targetEpoch
                && admission.TargetEpoch != targetEpoch))
        {
            throw new InvalidDataException(
                "The native capture command admission stamp is malformed or does not match the requested authorization snapshot.");
        }
    }

    private static void ThrowForResult(NativeCaptureResult result, string operation)
    {
        if (result == NativeCaptureResult.Ok)
        {
            return;
        }

        if (result == NativeCaptureResult.NotImplemented)
        {
            throw new NotSupportedException(
                "The native capture operation is not implemented by this binary.");
        }

        if (result == NativeCaptureResult.Timeout)
        {
            throw new TimeoutException(
                $"The native capture operation '{operation}' timed out.");
        }

        throw new NativeCaptureException(result, operation);
    }

    private void ThrowIfDisposed()
    {
        lock (_statusSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private void ThrowIfShuttingDown()
    {
        if (Volatile.Read(ref _shutdownStarted) != 0)
        {
            throw new InvalidOperationException(
                "The native capture backend is shutting down.");
        }
    }

    private bool IsDisposed()
    {
        lock (_statusSync)
        {
            return _disposed;
        }
    }

    private abstract record ManagedNotification;

    private sealed record StatusChangedNotification(
        CaptureStatus Previous,
        CaptureStatus Current) : ManagedNotification;

    private sealed record ChunkCommittedNotification(
        NativeCaptureChunkCommitted Chunk) : ManagedNotification;

    private sealed record NativePersistenceBoundary(
        ulong PersistenceGeneration,
        ulong TargetEpoch,
        NativeCaptureRuntimeAuthorization? Authorization);
}
