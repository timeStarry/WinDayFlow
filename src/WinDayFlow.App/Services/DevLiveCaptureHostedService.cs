#if WDF_DEV_LIVE_CAPTURE
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using WinDayFlow.Capture.Interop;

namespace WinDayFlow.App.Services;

internal sealed class DevLiveCaptureHostedService
    : IHostedService,
      IAsyncDisposable
{
    private readonly object _shutdownSync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Func<CancellationToken, Task> _startMonitorAsync;
    private readonly Func<ValueTask> _disposeMonitorAsync;
    private readonly Func<ValueTask> _disposeOwnerAsync;
    private Task? _shutdownTask;
    private int _startAttempted;

    public DevLiveCaptureHostedService(
        WindowsCapturePrivacyMonitor monitor,
        NativeCaptureRuntimeOwner owner)
        : this(
            GetStartMonitorAsync(monitor),
            GetDisposeMonitorAsync(monitor),
            GetDisposeOwnerAsync(owner))
    {
    }

    internal DevLiveCaptureHostedService(
        Func<CancellationToken, Task> startMonitorAsync,
        Func<ValueTask> disposeMonitorAsync,
        Func<ValueTask> disposeOwnerAsync)
    {
        _startMonitorAsync = startMonitorAsync
            ?? throw new ArgumentNullException(nameof(startMonitorAsync));
        _disposeMonitorAsync = disposeMonitorAsync
            ?? throw new ArgumentNullException(nameof(disposeMonitorAsync));
        _disposeOwnerAsync = disposeOwnerAsync
            ?? throw new ArgumentNullException(nameof(disposeOwnerAsync));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _startAttempted, 1) != 0)
        {
            throw new InvalidOperationException(
                "The development live-capture lifetime can only be started once.");
        }

        Exception? startFailure = null;
        var entered = false;
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(IsShutdownStarted(), this);
            await _startMonitorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            startFailure = exception;
        }
        finally
        {
            if (entered)
            {
                _lifecycleGate.Release();
            }
        }

        if (startFailure is null)
        {
            return;
        }

        try
        {
            await EnsureShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception shutdownFailure)
        {
            throw new AggregateException(startFailure, shutdownFailure);
        }

        ExceptionDispatchInfo.Capture(startFailure).Throw();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var shutdown = EnsureShutdownAsync();
        return cancellationToken.CanBeCanceled
            ? shutdown.WaitAsync(cancellationToken)
            : shutdown;
    }

    public ValueTask DisposeAsync() => new(EnsureShutdownAsync());

    private Task EnsureShutdownAsync()
    {
        lock (_shutdownSync)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private bool IsShutdownStarted()
    {
        lock (_shutdownSync)
        {
            return _shutdownTask is not null;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        await Task.Yield();
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Exception? monitorFailure = null;
            try
            {
                await _disposeMonitorAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                monitorFailure = exception;
            }

            try
            {
                await _disposeOwnerAsync().ConfigureAwait(false);
            }
            catch (Exception ownerFailure)
            {
                if (monitorFailure is not null)
                {
                    throw new AggregateException(monitorFailure, ownerFailure);
                }

                throw;
            }

            if (monitorFailure is not null)
            {
                ExceptionDispatchInfo.Capture(monitorFailure).Throw();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static Func<CancellationToken, Task> GetStartMonitorAsync(
        WindowsCapturePrivacyMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return monitor.StartAsync;
    }

    private static Func<ValueTask> GetDisposeMonitorAsync(
        WindowsCapturePrivacyMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return monitor.DisposeAsync;
    }

    private static Func<ValueTask> GetDisposeOwnerAsync(
        NativeCaptureRuntimeOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return owner.DisposeAsync;
    }
}
#endif
