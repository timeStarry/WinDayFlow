using System.ComponentModel;
using System.Threading.Channels;

namespace WinDayFlow.Capture.Interop;

public sealed record WindowsCapturePrivacyObservation
{
    public WindowsCapturePrivacyObservation(
        NativeCapturePrivacySignals signals,
        WindowsCaptureDisplayTarget displayTarget)
    {
        Signals = signals ?? throw new ArgumentNullException(nameof(signals));
        DisplayTarget = displayTarget
            ?? throw new ArgumentNullException(nameof(displayTarget));

        var statesMatch = signals.Target.State switch
        {
            NativeCaptureTargetIdentityState.Unknown =>
                displayTarget.State == WindowsCaptureDisplayTargetState.Unknown,
            NativeCaptureTargetIdentityState.Absent =>
                displayTarget.State == WindowsCaptureDisplayTargetState.Absent,
            NativeCaptureTargetIdentityState.Present =>
                displayTarget.State == WindowsCaptureDisplayTargetState.Present
                && signals.Target.DisplayMonitorHandle
                    == displayTarget.MonitorHandle
                && string.Equals(
                    signals.Target.DisplayDeviceKey,
                    displayTarget.DeviceKey,
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!statesMatch)
        {
            throw new ArgumentException(
                "The capture target and display target must describe the same observation.",
                nameof(displayTarget));
        }
    }

    public static WindowsCapturePrivacyObservation FailClosed { get; } = new(
        NativeCapturePrivacySignals.FailClosed,
        WindowsCaptureDisplayTarget.Unknown);

    public NativeCapturePrivacySignals Signals { get; }

    public WindowsCaptureDisplayTarget DisplayTarget { get; }

    public override string ToString()
    {
        return $"{nameof(WindowsCapturePrivacyObservation)} {{ "
            + $"SessionUnlocked = {Signals.SessionUnlocked}, "
            + $"SecureDesktopClear = {Signals.SecureDesktopClear}, "
            + $"RemoteSession = {Signals.RemoteSession}, "
            + $"PresentationMode = {Signals.PresentationMode}, "
            + $"ApplicationAllowed = {Signals.ApplicationAllowed}, "
            + $"WindowAllowed = {Signals.WindowAllowed}, "
            + $"StorageAvailable = {Signals.StorageAvailable}, "
            + $"TargetState = {Signals.Target.State}, "
            + $"DisplayTargetState = {DisplayTarget.State}, "
            + $"ExecutableNameState = "
            + $"{Signals.CaptureIdentity.ExecutableNameObservation.State}, "
            + $"PackageFamilyNameState = "
            + $"{Signals.CaptureIdentity.PackageFamilyNameObservation.State}, "
            + $"PublisherCertificateSha256State = "
            + $"{Signals.CaptureIdentity.PublisherCertificateSha256Observation.State}, "
            + $"WindowTitleState = "
            + $"{Signals.CaptureIdentity.WindowTitleObservation.State}, "
            + "Values = [REDACTED] }";
    }
}

internal interface IWindowsCapturePrivacySampler
{
    void InvalidateTargetObservation();

    ValueTask<WindowsCapturePrivacyObservation> SampleAsync(
        CancellationToken cancellationToken);
}

public sealed class WindowsCapturePrivacySampler : IWindowsCapturePrivacySampler
{
    private readonly Func<NativeCapturePrivacySignals> _sampleBaseSignals;
    private readonly Func<WindowsCaptureTargetVerificationResult> _verifyTarget;
    private readonly Action _invalidateTargetObservation;

    public WindowsCapturePrivacySampler(
        string storageDirectory,
        ulong minimumStorageHeadroomBytes)
    {
        var privacyProbe = new WindowsCapturePrivacyProbe(
            storageDirectory,
            minimumStorageHeadroomBytes);
        var targetVerifier = new WindowsCaptureTargetVerifier();
        _sampleBaseSignals = privacyProbe.Sample;
        _verifyTarget = targetVerifier.Verify;
        _invalidateTargetObservation = targetVerifier.InvalidateObservation;
    }

    internal WindowsCapturePrivacySampler(
        Func<NativeCapturePrivacySignals> sampleBaseSignals,
        Func<WindowsCaptureTargetVerificationResult> verifyTarget,
        Action invalidateTargetObservation)
    {
        _sampleBaseSignals = sampleBaseSignals
            ?? throw new ArgumentNullException(nameof(sampleBaseSignals));
        _verifyTarget = verifyTarget
            ?? throw new ArgumentNullException(nameof(verifyTarget));
        _invalidateTargetObservation = invalidateTargetObservation
            ?? throw new ArgumentNullException(nameof(invalidateTargetObservation));
    }

    public void InvalidateTargetObservation()
    {
        _invalidateTargetObservation();
    }

    public WindowsCapturePrivacyObservation Sample()
    {
        var before = _sampleBaseSignals();
        var target = _verifyTarget();
        var after = _sampleBaseSignals();
        if (before != after)
        {
            return WindowsCapturePrivacyObservation.FailClosed;
        }

        var signals = new NativeCapturePrivacySignals(
            after.SessionUnlocked,
            after.SecureDesktopClear,
            after.RemoteSession,
            after.PresentationMode,
            after.ApplicationAllowed,
            after.WindowAllowed,
            after.StorageAvailable,
            target.CaptureIdentity,
            target.Target);
        return new WindowsCapturePrivacyObservation(
            signals,
            target.DisplayTarget);
    }

    ValueTask<WindowsCapturePrivacyObservation>
        IWindowsCapturePrivacySampler.SampleAsync(
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Sample());
    }
}

[Flags]
internal enum WindowsCapturePrivacyInvalidationReason : long
{
    None = 0,
    Startup = 1L << 0,
    Foreground = 1L << 1,
    DesktopSwitch = 1L << 2,
    ObjectCreated = 1L << 3,
    ObjectDestroyed = 1L << 4,
    ObjectNameChanged = 1L << 5,
    EventSourceFault = 1L << 6,
    MonitorFault = 1L << 7,
    Shutdown = 1L << 8,
    ObjectLocationChanged = 1L << 9,
    DisplayTopologyChanged = 1L << 10,
    SessionUnavailable = 1L << 11,
    SessionAvailable = 1L << 12,
    SessionChanged = 1L << 13,
    PowerSuspending = 1L << 14,
    PowerResumed = 1L << 15,
}

[Flags]
internal enum WindowsCapturePrivacyHold
{
    None = 0,
    SessionUnavailable = 1 << 0,
    PowerSuspended = 1 << 1,
}

public enum WindowsCapturePrivacyMonitorFault
{
    EventSourceStart = 1,
    EventSource = 2,
    ObservationInvalidation = 3,
    PrivacyBarrier = 4,
    SignalPublication = 5,
    GenerationDesynchronized = 6,
    Worker = 7,
    EventSourceDisposal = 8,
    PrivacyBarrierDisposal = 9,
}

public sealed class WindowsCapturePrivacyMonitorException : Exception
{
    internal WindowsCapturePrivacyMonitorException(
        WindowsCapturePrivacyMonitorFault fault)
        : base($"The Windows capture privacy monitor failed with {fault}.")
    {
        Fault = fault;
    }

    public WindowsCapturePrivacyMonitorFault Fault { get; }
}

public sealed class WindowsCapturePrivacyMonitor : IAsyncDisposable
{
    private const int TerminalTransitionPending = -1;

    private readonly object _lifecycleSync = new();
    private readonly object _invalidationSync = new();
    private readonly INativeCapturePrivacySignalSink _sink;
    private readonly IWindowsCapturePrivacySampler _sampler;
    private readonly IWindowsCaptureEventSource _eventSource;
    private readonly CancellationTokenSource _workerCancellation = new();
    private readonly Channel<byte> _workerWake =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WindowsCapturePrivacyMonitorFault>
        _terminalFaultCommitted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    private WindowsCapturePrivacyObservation _lastObservation =
        WindowsCapturePrivacyObservation.FailClosed;
    private Task? _workerTask;
    private Task? _disposeTask;
    private long _latestGeneration;
    private long _lastProcessedGeneration;
    private long _lastPublishedGeneration;
    private long _observedReasonBits;
    private long _holdGeneration;
    private WindowsCapturePrivacyHold _activeHolds;
    private int _terminalFault;
    private int _sourceCleanupFault;
    private int _startAttempted;
    private int _acceptCallbacks;
    private int _stopping;

    public WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        string storageDirectory,
        ulong minimumStorageHeadroomBytes)
        : this(
            sink,
            new WindowsCapturePrivacySampler(
                storageDirectory,
                minimumStorageHeadroomBytes),
            new WindowsCaptureWinEventSource())
    {
    }

    internal WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        IWindowsCapturePrivacySampler sampler,
        IWindowsCaptureEventSource eventSource)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _eventSource = eventSource
            ?? throw new ArgumentNullException(nameof(eventSource));
    }

    public Task Completion => _completion.Task;

    public WindowsCapturePrivacyObservation LastObservation =>
        Volatile.Read(ref _lastObservation);

    public long LastPublishedGeneration =>
        Volatile.Read(ref _lastPublishedGeneration);

    internal WindowsCapturePrivacyInvalidationReason ObservedInvalidationReasons =>
        (WindowsCapturePrivacyInvalidationReason)Volatile.Read(
            ref _observedReasonBits);

    internal WindowsCapturePrivacyHold ActiveHolds
    {
        get
        {
            lock (_invalidationSync)
            {
                return _activeHolds;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsCapturePrivacyMonitorFault? startupFault = null;
        lock (_lifecycleSync)
        {
            if (Interlocked.Exchange(ref _startAttempted, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The Windows capture privacy monitor can only be started once.");
            }

            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _stopping) != 0,
                this);
            lock (_invalidationSync)
            {
                try
                {
                    InvalidateWithoutWake(
                        WindowsCapturePrivacyInvalidationReason.Startup);
                }
                catch
                {
                    if (TryBeginTerminalTransitionUnderLock())
                    {
                        CompleteTerminalTransitionUnderLock(
                            WindowsCapturePrivacyMonitorFault
                                .ObservationInvalidation);
                    }

                    startupFault = GetTerminalFault()
                        ?? WindowsCapturePrivacyMonitorFault
                            .ObservationInvalidation;
                }
            }
        }

        if (startupFault is null)
        {
            try
            {
                await ApplyLatestPrivacyInvalidationAsync()
                    .ConfigureAwait(false);
            }
            catch
            {
                lock (_invalidationSync)
                {
                    if (TryBeginTerminalTransitionUnderLock())
                    {
                        CompleteTerminalTransitionUnderLock(
                            WindowsCapturePrivacyMonitorFault.PrivacyBarrier);
                    }

                    startupFault = GetTerminalFault()
                        ?? WindowsCapturePrivacyMonitorFault.PrivacyBarrier;
                }
            }
        }

        if (startupFault is null)
        {
            lock (_lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _stopping) != 0,
                    this);
                Volatile.Write(ref _acceptCallbacks, 1);
                try
                {
                    _eventSource.Start(OnSourceChanged, OnSourceFaulted);
                }
                catch
                {
                    Volatile.Write(ref _acceptCallbacks, 0);
                    lock (_invalidationSync)
                    {
                        if (TryBeginTerminalTransitionUnderLock())
                        {
                            var fault = WindowsCapturePrivacyMonitorFault
                                .EventSourceStart;
                            try
                            {
                                InvalidateWithoutWake(
                                    WindowsCapturePrivacyInvalidationReason
                                        .MonitorFault);
                            }
                            catch
                            {
                                fault = WindowsCapturePrivacyMonitorFault
                                    .ObservationInvalidation;
                            }

                            CompleteTerminalTransitionUnderLock(fault);
                        }

                        startupFault = GetTerminalFault()
                            ?? WindowsCapturePrivacyMonitorFault
                                .EventSourceStart;
                    }
                }

                if (startupFault is null
                    && GetTerminalFault() is { } sourceStartupFault)
                {
                    Volatile.Write(ref _acceptCallbacks, 0);
                    startupFault = sourceStartupFault;
                }

                if (startupFault is null)
                {
                    _workerTask = Task.Run(
                        RunWorkerAsync,
                        CancellationToken.None);
                    WakeWorker();
                }
            }
        }

        if (startupFault is null)
        {
            return;
        }

        await CompleteFailedStartAsync(startupFault.Value).ConfigureAwait(false);
        throw CreateFaultException(startupFault.Value);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private void OnSourceChanged(WindowsCaptureWinEventChange change)
    {
        if (Volatile.Read(ref _acceptCallbacks) == 0)
        {
            return;
        }

        try
        {
            lock (_invalidationSync)
            {
                if (Volatile.Read(ref _acceptCallbacks) == 0
                    || IsTerminalTransitionStarted())
                {
                    return;
                }

                try
                {
                    var reason = MapReason(change);
                    ApplyHoldChangeUnderLock(change);
                    InvalidateWithoutWake(reason);
                }
                catch
                {
                    HandleCallbackFailureUnderLock();
                }
            }

            WakeWorker();
        }
        catch
        {
            // Exceptions must never cross a native WinEvent callback boundary.
        }
    }

    private void OnSourceFaulted(WindowsCaptureWinEventSourceFault fault)
    {
        try
        {
            _ = fault;
            lock (_invalidationSync)
            {
                if (Volatile.Read(ref _stopping) != 0)
                {
                    Interlocked.CompareExchange(
                        ref _sourceCleanupFault,
                        value: 1,
                        comparand: 0);
                    return;
                }

                if (!TryBeginTerminalTransitionUnderLock())
                {
                    return;
                }

                var terminalFault = WindowsCapturePrivacyMonitorFault.EventSource;
                try
                {
                    InvalidateWithoutWake(
                        WindowsCapturePrivacyInvalidationReason.EventSourceFault);
                }
                catch
                {
                    terminalFault = WindowsCapturePrivacyMonitorFault
                        .ObservationInvalidation;
                }

                CompleteTerminalTransitionUnderLock(terminalFault);
            }

            WakeWorker();
        }
        catch
        {
            // Exceptions must never cross a native WinEvent callback boundary.
        }
    }

    private void HandleCallbackFailureUnderLock()
    {
        if (!TryBeginTerminalTransitionUnderLock())
        {
            return;
        }

        try
        {
            InvalidateWithoutWake(
                WindowsCapturePrivacyInvalidationReason.MonitorFault);
        }
        catch
        {
        }

        CompleteTerminalTransitionUnderLock(
            WindowsCapturePrivacyMonitorFault.ObservationInvalidation);
    }

    private void InvalidateWithoutWake(
        WindowsCapturePrivacyInvalidationReason reason)
    {
        var generation = 0L;
        Exception? failure = null;
        try
        {
            generation = _sink.InvalidatePrivacyObservation();
            var current = Volatile.Read(ref _latestGeneration);
            if (generation <= current)
            {
                throw new InvalidOperationException(
                    "The privacy observation generation did not advance.");
            }

            AdvanceGeneration(ref _latestGeneration, generation);
            _holdGeneration = generation;
            _ = Interlocked.Or(ref _observedReasonBits, (long)reason);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _sampler.InvalidateTargetObservation();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            while (await _workerWake.Reader
                       .WaitToReadAsync(_workerCancellation.Token)
                       .ConfigureAwait(false))
            {
                while (_workerWake.Reader.TryRead(out _))
                {
                }

                while (true)
                {
                    var generation = Volatile.Read(ref _latestGeneration);
                    if (generation <= 0)
                    {
                        break;
                    }

                    if (generation <= Volatile.Read(ref _lastProcessedGeneration)
                        && GetTerminalFault() is null)
                    {
                        break;
                    }

                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        return;
                    }

                    try
                    {
                        await _sink
                            .ApplyPrivacyInvalidationAsync(
                                generation,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        await CloseWorkerForFaultAsync(
                                WindowsCapturePrivacyMonitorFault.PrivacyBarrier)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!IsCurrentGeneration(generation))
                    {
                        if (generation == Volatile.Read(ref _latestGeneration))
                        {
                            await CloseWorkerForFaultAsync(
                                    WindowsCapturePrivacyMonitorFault
                                        .GenerationDesynchronized)
                                .ConfigureAwait(false);
                            return;
                        }

                        continue;
                    }

                    if (GetTerminalFault() is { } terminalFault)
                    {
                        _completion.TrySetException(
                            CreateFaultException(terminalFault));
                        return;
                    }

                    if (IsTerminalTransitionPending())
                    {
                        break;
                    }

                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        return;
                    }

                    var resolution = CaptureResolutionDirective(generation);
                    if (resolution == ObservationResolutionDirective.Stale)
                    {
                        continue;
                    }

                    WindowsCapturePrivacyObservation observation;
                    if (resolution
                        == ObservationResolutionDirective.PublishFailClosed)
                    {
                        observation = WindowsCapturePrivacyObservation.FailClosed;
                    }
                    else
                    {
                        try
                        {
                            observation = await _sampler
                                .SampleAsync(_workerCancellation.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                            when (_workerCancellation.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception exception)
                            when (IsRecoverableSampleException(exception))
                        {
                            if (!await PublishRecoverableSampleFailureAsync(generation)
                                    .ConfigureAwait(false))
                            {
                                return;
                            }

                            if (generation == Volatile.Read(ref _latestGeneration))
                            {
                                break;
                            }

                            continue;
                        }
                    }

                    if (IsTerminalTransitionPending())
                    {
                        break;
                    }

                    if (GetTerminalFault() is not null)
                    {
                        continue;
                    }

                    if (!IsCurrentGeneration(generation)
                        || Volatile.Read(ref _stopping) != 0)
                    {
                        continue;
                    }

                    bool published;
                    try
                    {
                        published = await _sink
                            .TryUpdateSignalsAsync(
                                generation,
                                observation.Signals,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (published && IsCurrentGeneration(generation))
                        {
                            Volatile.Write(ref _lastObservation, observation);
                            Volatile.Write(
                                ref _lastPublishedGeneration,
                                generation);
                            Volatile.Write(
                                ref _lastProcessedGeneration,
                                generation);
                        }
                    }
                    catch
                    {
                        await CloseWorkerForFaultAsync(
                                WindowsCapturePrivacyMonitorFault.SignalPublication)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!published
                        && generation == Volatile.Read(ref _latestGeneration))
                    {
                        await CloseWorkerForFaultAsync(
                                WindowsCapturePrivacyMonitorFault
                                    .GenerationDesynchronized)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (generation == Volatile.Read(ref _latestGeneration))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (_workerCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            await CloseWorkerForFaultAsync(
                    WindowsCapturePrivacyMonitorFault.Worker)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> PublishRecoverableSampleFailureAsync(long generation)
    {
        if (!IsCurrentGeneration(generation)
            || Volatile.Read(ref _stopping) != 0)
        {
            return true;
        }

        try
        {
            var published = await _sink
                .TryUpdateSignalsAsync(
                    generation,
                    NativeCapturePrivacySignals.FailClosed,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (published && IsCurrentGeneration(generation))
            {
                Volatile.Write(
                    ref _lastObservation,
                    WindowsCapturePrivacyObservation.FailClosed);
                Volatile.Write(ref _lastPublishedGeneration, generation);
                Volatile.Write(ref _lastProcessedGeneration, generation);
            }
            else if (!published
                && generation == Volatile.Read(ref _latestGeneration))
            {
                await CloseWorkerForFaultAsync(
                        WindowsCapturePrivacyMonitorFault.GenerationDesynchronized)
                    .ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch
        {
            await CloseWorkerForFaultAsync(
                    WindowsCapturePrivacyMonitorFault.SignalPublication)
                .ConfigureAwait(false);
            return false;
        }
    }

    private async Task CloseWorkerForFaultAsync(
        WindowsCapturePrivacyMonitorFault fault)
    {
        var ownsTerminalTransition = false;
        try
        {
            lock (_invalidationSync)
            {
                ownsTerminalTransition = TryBeginTerminalTransitionUnderLock();
                if (ownsTerminalTransition)
                {
                    try
                    {
                        InvalidateWithoutWake(
                            WindowsCapturePrivacyInvalidationReason.MonitorFault);
                    }
                    catch
                    {
                        fault = WindowsCapturePrivacyMonitorFault
                            .ObservationInvalidation;
                    }
                }
            }

            if (ownsTerminalTransition)
            {
                try
                {
                    await ApplyLatestPrivacyInvalidationAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                    fault = WindowsCapturePrivacyMonitorFault.PrivacyBarrier;
                }

                lock (_invalidationSync)
                {
                    CompleteTerminalTransitionUnderLock(fault);
                }
            }
            else
            {
                fault = await _terminalFaultCommitted.Task.ConfigureAwait(false);
                try
                {
                    await ApplyLatestPrivacyInvalidationAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The accepted terminal fault remains authoritative.
                }
            }

            _completion.TrySetException(CreateFaultException(fault));
        }
        catch
        {
            lock (_invalidationSync)
            {
                if (ownsTerminalTransition && IsTerminalTransitionPending())
                {
                    CompleteTerminalTransitionUnderLock(fault);
                }
            }

            _completion.TrySetException(CreateFaultException(fault));
        }
    }

    private async Task CompleteFailedStartAsync(
        WindowsCapturePrivacyMonitorFault startupFault)
    {
        var barrierGeneration = 0L;
        try
        {
            barrierGeneration = await ApplyLatestPrivacyInvalidationAsync()
                .ConfigureAwait(false);
        }
        catch
        {
            // The already accepted startup fault remains authoritative.
        }

        try
        {
            _eventSource.Dispose();
        }
        catch
        {
            Interlocked.CompareExchange(
                ref _sourceCleanupFault,
                value: 1,
                comparand: 0);
        }

        if (barrierGeneration != Volatile.Read(ref _latestGeneration)
            || barrierGeneration != _sink.PrivacyObservationGeneration)
        {
            try
            {
                await ApplyLatestPrivacyInvalidationAsync().ConfigureAwait(false);
            }
            catch
            {
                // The already accepted startup fault remains authoritative.
            }
        }

        var fault = GetTerminalFault() ?? startupFault;
        _completion.TrySetException(CreateFaultException(fault));
    }

    private async Task DisposeCoreAsync()
    {
        WindowsCapturePrivacyMonitorFault? disposalFault = null;
        var barrierGeneration = 0L;
        lock (_invalidationSync)
        {
            Volatile.Write(ref _acceptCallbacks, 0);
            Interlocked.Exchange(ref _stopping, 1);
            try
            {
                InvalidateWithoutWake(
                    WindowsCapturePrivacyInvalidationReason.Shutdown);
            }
            catch
            {
                disposalFault = WindowsCapturePrivacyMonitorFault
                    .ObservationInvalidation;
            }
        }

        try
        {
            barrierGeneration = await ApplyLatestPrivacyInvalidationAsync()
                .ConfigureAwait(false);
        }
        catch
        {
            disposalFault ??= WindowsCapturePrivacyMonitorFault
                .PrivacyBarrierDisposal;
        }

        try
        {
            _eventSource.Dispose();
        }
        catch
        {
            disposalFault ??= WindowsCapturePrivacyMonitorFault.EventSourceDisposal;
        }

        if (Volatile.Read(ref _sourceCleanupFault) != 0)
        {
            disposalFault ??= WindowsCapturePrivacyMonitorFault.EventSourceDisposal;
        }

        if (barrierGeneration != Volatile.Read(ref _latestGeneration)
            || barrierGeneration != _sink.PrivacyObservationGeneration)
        {
            try
            {
                await ApplyLatestPrivacyInvalidationAsync().ConfigureAwait(false);
            }
            catch
            {
                disposalFault ??= WindowsCapturePrivacyMonitorFault
                    .PrivacyBarrierDisposal;
            }
        }

        _workerCancellation.Cancel();
        _workerWake.Writer.TryComplete();
        var workerTask = Volatile.Read(ref _workerTask);
        if (workerTask is not null)
        {
            try
            {
                await workerTask.ConfigureAwait(false);
            }
            catch
            {
                disposalFault ??= WindowsCapturePrivacyMonitorFault.Worker;
            }
        }

        _workerCancellation.Dispose();
        var terminalFault = GetTerminalFault();
        if (terminalFault is null && IsTerminalTransitionPending())
        {
            terminalFault = await _terminalFaultCommitted.Task.ConfigureAwait(false);
        }

        if (terminalFault is { } acceptedTerminalFault)
        {
            _completion.TrySetException(
                CreateFaultException(acceptedTerminalFault));
        }

        if (disposalFault is null)
        {
            if (terminalFault is null)
            {
                _completion.TrySetResult();
            }

            return;
        }

        var failure = CreateFaultException(disposalFault.Value);
        _completion.TrySetException(failure);
        throw failure;
    }

    private bool IsCurrentGeneration(long generation)
    {
        return generation == Volatile.Read(ref _latestGeneration)
            && generation == _sink.PrivacyObservationGeneration;
    }

    private bool TryBeginTerminalTransitionUnderLock()
    {
        if (Interlocked.CompareExchange(
                ref _terminalFault,
                TerminalTransitionPending,
                comparand: 0) != 0)
        {
            return false;
        }

        Volatile.Write(ref _acceptCallbacks, 0);
        return true;
    }

    private void CompleteTerminalTransitionUnderLock(
        WindowsCapturePrivacyMonitorFault fault)
    {
        if (!Enum.IsDefined(fault))
        {
            throw new ArgumentOutOfRangeException(nameof(fault));
        }

        var previous = Interlocked.CompareExchange(
            ref _terminalFault,
            (int)fault,
            TerminalTransitionPending);
        if (previous != TerminalTransitionPending)
        {
            throw new InvalidOperationException(
                "No privacy monitor terminal transition is pending.");
        }

        _terminalFaultCommitted.TrySetResult(fault);
    }

    private WindowsCapturePrivacyMonitorFault? GetTerminalFault()
    {
        var fault = Volatile.Read(ref _terminalFault);
        return fault <= 0
            ? null
            : (WindowsCapturePrivacyMonitorFault)fault;
    }

    private bool IsTerminalTransitionPending() =>
        Volatile.Read(ref _terminalFault) == TerminalTransitionPending;

    private bool IsTerminalTransitionStarted() =>
        Volatile.Read(ref _terminalFault) != 0;

    private async Task<long> ApplyLatestPrivacyInvalidationAsync()
    {
        while (true)
        {
            var generation = Volatile.Read(ref _latestGeneration);
            if (generation <= 0)
            {
                return 0;
            }

            await _sink
                .ApplyPrivacyInvalidationAsync(
                    generation,
                    CancellationToken.None)
                .ConfigureAwait(false);

            var latestGeneration = Volatile.Read(ref _latestGeneration);
            var sinkGeneration = _sink.PrivacyObservationGeneration;
            if (generation == latestGeneration
                && generation == sinkGeneration)
            {
                return generation;
            }

            if (generation == latestGeneration)
            {
                throw new InvalidOperationException(
                    "The privacy observation generations are desynchronized.");
            }
        }
    }

    private static WindowsCapturePrivacyMonitorException CreateFaultException(
        WindowsCapturePrivacyMonitorFault fault)
    {
        return new WindowsCapturePrivacyMonitorException(fault);
    }

    private void WakeWorker()
    {
        _workerWake.Writer.TryWrite(0);
    }

    private static void AdvanceGeneration(ref long location, long generation)
    {
        if (generation <= 0)
        {
            throw new InvalidOperationException(
                "The privacy observation generation must be positive.");
        }

        var current = Volatile.Read(ref location);
        while (generation > current)
        {
            var observed = Interlocked.CompareExchange(
                ref location,
                generation,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private void ApplyHoldChangeUnderLock(WindowsCaptureWinEventChange change)
    {
        switch (change)
        {
            case WindowsCaptureWinEventChange.SessionUnavailable:
                _activeHolds |= WindowsCapturePrivacyHold.SessionUnavailable;
                break;
            case WindowsCaptureWinEventChange.SessionAvailable:
                _activeHolds &= ~WindowsCapturePrivacyHold.SessionUnavailable;
                break;
            case WindowsCaptureWinEventChange.PowerSuspending:
                _activeHolds |= WindowsCapturePrivacyHold.PowerSuspended;
                break;
            case WindowsCaptureWinEventChange.PowerResumed:
                _activeHolds &= ~WindowsCapturePrivacyHold.PowerSuspended;
                break;
        }
    }

    private ObservationResolutionDirective CaptureResolutionDirective(
        long generation)
    {
        lock (_invalidationSync)
        {
            if (generation != Volatile.Read(ref _latestGeneration)
                || generation != _holdGeneration)
            {
                return ObservationResolutionDirective.Stale;
            }

            return _activeHolds == WindowsCapturePrivacyHold.None
                ? ObservationResolutionDirective.Sample
                : ObservationResolutionDirective.PublishFailClosed;
        }
    }

    private static WindowsCapturePrivacyInvalidationReason MapReason(
        WindowsCaptureWinEventChange change)
    {
        return change switch
        {
            WindowsCaptureWinEventChange.Foreground =>
                WindowsCapturePrivacyInvalidationReason.Foreground,
            WindowsCaptureWinEventChange.DesktopSwitch =>
                WindowsCapturePrivacyInvalidationReason.DesktopSwitch,
            WindowsCaptureWinEventChange.ObjectCreated =>
                WindowsCapturePrivacyInvalidationReason.ObjectCreated,
            WindowsCaptureWinEventChange.ObjectDestroyed =>
                WindowsCapturePrivacyInvalidationReason.ObjectDestroyed,
            WindowsCaptureWinEventChange.ObjectNameChanged =>
                WindowsCapturePrivacyInvalidationReason.ObjectNameChanged,
            WindowsCaptureWinEventChange.ObjectLocationChanged =>
                WindowsCapturePrivacyInvalidationReason.ObjectLocationChanged,
            WindowsCaptureWinEventChange.DisplayTopologyChanged =>
                WindowsCapturePrivacyInvalidationReason.DisplayTopologyChanged,
            WindowsCaptureWinEventChange.SessionUnavailable =>
                WindowsCapturePrivacyInvalidationReason.SessionUnavailable,
            WindowsCaptureWinEventChange.SessionAvailable =>
                WindowsCapturePrivacyInvalidationReason.SessionAvailable,
            WindowsCaptureWinEventChange.SessionChanged =>
                WindowsCapturePrivacyInvalidationReason.SessionChanged,
            WindowsCaptureWinEventChange.PowerSuspending =>
                WindowsCapturePrivacyInvalidationReason.PowerSuspending,
            WindowsCaptureWinEventChange.PowerResumed =>
                WindowsCapturePrivacyInvalidationReason.PowerResumed,
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
    }

    private enum ObservationResolutionDirective
    {
        Stale = 0,
        Sample = 1,
        PublishFailClosed = 2,
    }

    private static bool IsRecoverableSampleException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or Win32Exception
            or PlatformNotSupportedException;
    }
}
