using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using WinDayFlow.Application.Capture;

namespace WinDayFlow.Capture.Interop;

public sealed class NativeCaptureBackend
    : ICaptureBackend, INativeCapturePrivacyTarget, IDisposable
{
    private const string FoundationUnavailableDetail =
        "原生录制基础已加载；实时屏幕捕获能力尚未启用。";
    private const int MaximumEventDetailBytes = 1024 * 1024;
    private const uint PollTimeoutMilliseconds = 250;
    private const uint StopTimeoutMilliseconds = 5_000;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly object _statusSync = new();
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
                $"Native capture ABI {probe.AbiVersion} is incompatible with managed ABI {NativeCaptureAbiContract.AbiVersion}.");
        }

        var requiredFoundation = NativeCaptureCapabilities.PrivacyGuard
            | NativeCaptureCapabilities.EventQueue;
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
            UpdatePrivacyContextCore(initialPrivacyContext);
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
        (Capabilities & NativeCaptureCapabilities.ScreenCapture) != 0;

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

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        InvokeLifecycleAsync(
            "start",
            _nativeApi.Start,
            cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        InvokeLifecycleAsync(
            "pause",
            _nativeApi.Pause,
            cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        InvokeLifecycleAsync(
            "resume",
            _nativeApi.Resume,
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
            UpdatePrivacyContextCore(context);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
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

        _lifetimeCancellation.Cancel();
        _notifications.Writer.TryComplete();
        _lifecycleGate.Wait();
        try
        {
            if (!_handle.IsInvalid && !_handle.IsClosed)
            {
                _ = _nativeApi.RequestStop(_handle);
                _ = _nativeApi.WaitStopped(
                    _handle,
                    StopTimeoutMilliseconds);
                _handle.Dispose();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        try
        {
            _eventPump.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        if (_notificationPump.IsCompleted)
        {
            _notificationPump.GetAwaiter().GetResult();
        }

        _lifetimeCancellation.Dispose();
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

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
            ThrowForResult(nativeOperation(_handle), operation);
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
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _lifecycleGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
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
        ThrowForResult(
            _nativeApi.UpdatePrivacyContext(
                _handle,
                ref nativeContext),
            "update_privacy_context");
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
            captureEvent.DroppedBefore);
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
        try
        {
            if (!_handle.IsInvalid && !_handle.IsClosed)
            {
                _ = _nativeApi.RequestStop(_handle);
            }
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

    private abstract record ManagedNotification;

    private sealed record StatusChangedNotification(
        CaptureStatus Previous,
        CaptureStatus Current) : ManagedNotification;

    private sealed record ChunkCommittedNotification(
        NativeCaptureChunkCommitted Chunk) : ManagedNotification;
}
