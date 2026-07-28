namespace WinDayFlow.App.Services;

internal sealed class CaptureAwareWindowCloseCoordinator
{
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task> _stopCaptureAsync;
    private readonly Func<Task> _completeShutdownAsync;
    private readonly Action _requestWindowClose;
    private readonly Action<Exception> _reportFailure;
    private readonly TimeSpan _stopTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private int _state;

    public CaptureAwareWindowCloseCoordinator(
        Func<CancellationToken, Task> stopCaptureAsync,
        Func<Task> completeShutdownAsync,
        Action requestWindowClose,
        Action<Exception> reportFailure)
        : this(
            stopCaptureAsync,
            completeShutdownAsync,
            requestWindowClose,
            reportFailure,
            DefaultStopTimeout,
            Task.Delay)
    {
    }

    internal CaptureAwareWindowCloseCoordinator(
        Func<CancellationToken, Task> stopCaptureAsync,
        Func<Task> completeShutdownAsync,
        Action requestWindowClose,
        Action<Exception> reportFailure,
        TimeSpan stopTimeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _stopCaptureAsync = stopCaptureAsync
            ?? throw new ArgumentNullException(nameof(stopCaptureAsync));
        _completeShutdownAsync = completeShutdownAsync
            ?? throw new ArgumentNullException(nameof(completeShutdownAsync));
        _requestWindowClose = requestWindowClose
            ?? throw new ArgumentNullException(nameof(requestWindowClose));
        _reportFailure = reportFailure
            ?? throw new ArgumentNullException(nameof(reportFailure));
        if (stopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopTimeout),
                stopTimeout,
                "The capture stop timeout must be positive.");
        }

        _stopTimeout = stopTimeout;
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public bool ShouldCancelClose()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state == (int)CloseState.ReadyToClose)
            {
                return false;
            }

            if (state == (int)CloseState.StoppingCapture)
            {
                return true;
            }

            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)CloseState.StoppingCapture,
                    (int)CloseState.Open)
                == (int)CloseState.Open)
            {
                _ = StopCaptureAndShutdownThenCloseAsync();
                return true;
            }
        }
    }

    private async Task StopCaptureAndShutdownThenCloseAsync()
    {
        await Task.Yield();

        await StopCaptureAsync();
        try
        {
            await _completeShutdownAsync();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
        finally
        {
            Volatile.Write(ref _state, (int)CloseState.ReadyToClose);
            try
            {
                _requestWindowClose();
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

    private async Task StopCaptureAsync()
    {
        Task? stopTask = null;
        try
        {
            using var stopCancellation = new CancellationTokenSource();
            using var timeoutCancellation = new CancellationTokenSource();
            var timeoutTask = _delayAsync(_stopTimeout, timeoutCancellation.Token);
            stopTask = Task.Run(
                () => _stopCaptureAsync(stopCancellation.Token),
                CancellationToken.None);
            var completedTask = await Task.WhenAny(stopTask, timeoutTask);

            if (ReferenceEquals(completedTask, timeoutTask))
            {
                stopCancellation.Cancel();
                ObserveFailure(stopTask);
                await timeoutTask;
                throw new TimeoutException(
                    $"Capture did not stop within {_stopTimeout.TotalSeconds:g} seconds.");
            }

            timeoutCancellation.Cancel();
            await stopTask;
        }
        catch (Exception exception)
        {
            if (stopTask is not null && !stopTask.IsCompleted)
            {
                ObserveFailure(stopTask);
            }

            ReportFailure(exception);
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            _reportFailure(exception);
        }
        catch (Exception reportingFailure)
        {
            System.Diagnostics.Debug.WriteLine(reportingFailure);
        }
    }

    private static void ObserveFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private enum CloseState
    {
        Open = 0,
        StoppingCapture = 1,
        ReadyToClose = 2,
    }
}
