using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace WinDayFlow.Capture.Interop;

internal enum WindowsCaptureWindowTitleWorkerState
{
    Idle = 0,
    Queued = 1,
    InFlight = 2,
    Completing = 3,
    Poisoned = 4,
    Stopping = 5,
}

internal readonly record struct WindowsCaptureWindowTextBufferReadResult(
    WindowsCaptureObservationReadState State,
    int CharactersCopied)
{
    internal static WindowsCaptureWindowTextBufferReadResult Unknown { get; } =
        new(WindowsCaptureObservationReadState.Unknown, 0);

    internal static WindowsCaptureWindowTextBufferReadResult Absent { get; } =
        new(WindowsCaptureObservationReadState.Absent, 0);

    internal static WindowsCaptureWindowTextBufferReadResult Present(
        int charactersCopied)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(charactersCopied, 1);
        return new WindowsCaptureWindowTextBufferReadResult(
            WindowsCaptureObservationReadState.Present,
            charactersCopied);
    }

    public override string ToString()
    {
        return $"{nameof(WindowsCaptureWindowTextBufferReadResult)} {{ "
            + $"State = {State}, Values = [REDACTED] }}";
    }
}

internal interface IWindowsCaptureWindowTextBufferApi
{
    WindowsCaptureWindowTextBufferReadResult ReadWindowText(
        ulong windowHandle,
        char[] buffer);
}

internal interface IWindowsCaptureWindowTitleReader
{
    WindowsCaptureObservationReadState ReadWindowTitle(
        ulong windowHandle,
        out string value);
}

internal interface IWindowsCaptureWindowTitleDeadline
{
    long CreateDeadline(TimeSpan timeout);

    bool IsExpired(long deadline);

    void Wait(Task completion, long deadline);
}

internal sealed class StopwatchWindowsCaptureWindowTitleDeadline
    : IWindowsCaptureWindowTitleDeadline
{
    private StopwatchWindowsCaptureWindowTitleDeadline()
    {
    }

    internal static StopwatchWindowsCaptureWindowTitleDeadline Instance { get; } =
        new();

    public long CreateDeadline(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var timeoutTicks = checked((long)Math.Ceiling(
            timeout.TotalSeconds * Stopwatch.Frequency));
        var timestamp = Stopwatch.GetTimestamp();
        return timestamp > long.MaxValue - timeoutTicks
            ? long.MaxValue
            : timestamp + timeoutTicks;
    }

    public bool IsExpired(long deadline)
    {
        return Stopwatch.GetTimestamp() >= deadline;
    }

    public void Wait(Task completion, long deadline)
    {
        ArgumentNullException.ThrowIfNull(completion);

        var remainingTicks = deadline - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return;
        }

        var remaining = TimeSpan.FromSeconds(
            (double)remainingTicks / Stopwatch.Frequency);
        _ = completion.Wait(remaining);
    }
}

internal sealed class FailStopWindowsCaptureWindowTitleReader
    : IWindowsCaptureWindowTitleReader,
      IDisposable
{
    internal const int MaximumWindowTextCharacters = 32_768;
    internal const int DefaultReadDeadlineMilliseconds = 100;
    internal const int DefaultDisposeTimeoutMilliseconds = 250;

    private const string WorkerThreadName = "WinDayFlow.WindowTitle";

    private readonly object _sync = new();
    private readonly IWindowsCaptureWindowTextBufferApi _nativeApi;
    private readonly IWindowsCaptureWindowTitleDeadline _deadline;
    private readonly Func<char[], int, string> _valueBuilder;
    private readonly Action _workerEntry;
    private readonly TimeSpan _readTimeout;
    private readonly int _disposeTimeoutMilliseconds;
    private readonly char[] _buffer = new char[MaximumWindowTextCharacters];
    private WindowsCaptureWindowTitleWorkerState _state =
        WindowsCaptureWindowTitleWorkerState.Idle;
    private WindowTitleReadRequest? _request;
    private Thread? _worker;
    private ExceptionDispatchInfo? _terminalFatalException;
    private long _lastAttempt;
    private bool _disposed;

    internal FailStopWindowsCaptureWindowTitleReader(
        IWindowsCaptureWindowTextBufferApi nativeApi,
        IWindowsCaptureWindowTitleDeadline? deadline = null,
        int readDeadlineMilliseconds = DefaultReadDeadlineMilliseconds,
        int disposeTimeoutMilliseconds = DefaultDisposeTimeoutMilliseconds,
        Func<char[], int, string>? valueBuilder = null,
        Action? workerEntry = null)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        _deadline = deadline
            ?? StopwatchWindowsCaptureWindowTitleDeadline.Instance;
        _valueBuilder = valueBuilder
            ?? (static (buffer, length) => new string(buffer, 0, length));
        _workerEntry = workerEntry ?? (static () => { });
        ArgumentOutOfRangeException.ThrowIfLessThan(readDeadlineMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(disposeTimeoutMilliseconds, 1);
        _readTimeout = TimeSpan.FromMilliseconds(readDeadlineMilliseconds);
        _disposeTimeoutMilliseconds = disposeTimeoutMilliseconds;
    }

    internal static FailStopWindowsCaptureWindowTitleReader ProcessWide { get; } =
        new(PInvokeWindowsCaptureWindowTextBufferApi.Instance);

    internal WindowsCaptureWindowTitleWorkerState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    internal bool IsWorkerAlive
    {
        get
        {
            lock (_sync)
            {
                return _worker?.IsAlive == true;
            }
        }
    }

    internal Task? CurrentAttemptCompletion
    {
        get
        {
            lock (_sync)
            {
                return _request?.Completion.Task;
            }
        }
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

        var deadline = _deadline.CreateDeadline(_readTimeout);
        WindowTitleReadRequest request;
        lock (_sync)
        {
            _terminalFatalException?.Throw();
            if (_disposed
                || _state is not WindowsCaptureWindowTitleWorkerState.Idle)
            {
                return WindowsCaptureObservationReadState.Unknown;
            }

            if (_lastAttempt == long.MaxValue)
            {
                _state = WindowsCaptureWindowTitleWorkerState.Poisoned;
                Monitor.PulseAll(_sync);
                return WindowsCaptureObservationReadState.Unknown;
            }

            if (!TryEnsureWorkerUnderLock()
                || _deadline.IsExpired(deadline))
            {
                return WindowsCaptureObservationReadState.Unknown;
            }

            request = new WindowTitleReadRequest(
                windowHandle,
                ++_lastAttempt,
                deadline);
            _request = request;
            _state = WindowsCaptureWindowTitleWorkerState.Queued;
            Monitor.PulseAll(_sync);
        }

        while (true)
        {
            ExceptionDispatchInfo? fatalException = null;
            WindowsCaptureObservationReadState? completedState = null;
            lock (_sync)
            {
                if (request.Status is (
                        WindowTitleReadRequestStatus.Pending
                        or WindowTitleReadRequestStatus.Completing)
                    && _deadline.IsExpired(request.Deadline))
                {
                    ExpireRequestUnderLock(request);
                }

                switch (request.Status)
                {
                    case WindowTitleReadRequestStatus.Completed:
                        value = request.Value;
                        completedState = request.ResultState;
                        break;
                    case WindowTitleReadRequestStatus.Faulted:
                        fatalException = request.FatalException
                            ?? _terminalFatalException
                            ?? ExceptionDispatchInfo.Capture(
                                new InvalidOperationException(
                                    "A fatal window-title read failed without an exception."));
                        break;
                    case WindowTitleReadRequestStatus.Expired:
                        return WindowsCaptureObservationReadState.Unknown;
                }
            }

            if (completedState is { } resultState)
            {
                return resultState;
            }

            fatalException?.Throw();

            try
            {
                _deadline.Wait(request.Finished.Task, request.Deadline);
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    ExpireRequestUnderLock(request);
                }

                if (WindowsCaptureTargetVerifier
                    .IsRecoverableNativeReadException(exception))
                {
                    return WindowsCaptureObservationReadState.Unknown;
                }

                throw;
            }
        }
    }

    public void Dispose()
    {
        Thread? worker;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_request is
                {
                    Status: WindowTitleReadRequestStatus.Pending
                        or WindowTitleReadRequestStatus.Completing,
                } request)
            {
                request.Status = WindowTitleReadRequestStatus.Expired;
                request.Finished.TrySetResult();
            }

            _request = null;
            _state = WindowsCaptureWindowTitleWorkerState.Stopping;
            worker = _worker;
            Monitor.PulseAll(_sync);
        }

        if (worker is { IsAlive: true }
            && !ReferenceEquals(Thread.CurrentThread, worker))
        {
            _ = worker.Join(_disposeTimeoutMilliseconds);
        }

        GC.SuppressFinalize(this);
    }

    private bool TryEnsureWorkerUnderLock()
    {
        if (_worker is not null)
        {
            if (_worker.IsAlive)
            {
                return true;
            }

            _state = WindowsCaptureWindowTitleWorkerState.Poisoned;
            return false;
        }

        var worker = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = WorkerThreadName,
        };
        _worker = worker;
        try
        {
            worker.Start();
            return true;
        }
        catch (Exception exception) when (WindowsCaptureTargetVerifier
            .IsRecoverableNativeReadException(exception))
        {
            _state = WindowsCaptureWindowTitleWorkerState.Poisoned;
            return false;
        }
    }

    private void RunWorker()
    {
        try
        {
            RunWorkerCore();
        }
        catch (Exception exception)
        {
            CompleteWorkerFailure(exception);
        }
    }

    private void RunWorkerCore()
    {
        _workerEntry();

        while (true)
        {
            WindowTitleReadRequest request;
            lock (_sync)
            {
                while (_state is WindowsCaptureWindowTitleWorkerState.Idle)
                {
                    Monitor.Wait(_sync);
                }

                if (_state is WindowsCaptureWindowTitleWorkerState.Stopping
                    or WindowsCaptureWindowTitleWorkerState.Poisoned)
                {
                    _request = null;
                    return;
                }

                if (_state is not WindowsCaptureWindowTitleWorkerState.Queued
                    || _request is not { } queuedRequest)
                {
                    throw new InvalidOperationException(
                        "A queued window-title read must have a request.");
                }

                request = queuedRequest;
                if (request.Status is not WindowTitleReadRequestStatus.Pending
                    || _deadline.IsExpired(request.Deadline))
                {
                    ExpireRequestUnderLock(request);
                    continue;
                }

                _state = WindowsCaptureWindowTitleWorkerState.InFlight;
                Monitor.PulseAll(_sync);
            }

            RunRequest(request);
        }
    }

    private void RunRequest(WindowTitleReadRequest request)
    {
        Array.Clear(_buffer);
        try
        {
            WindowsCaptureWindowTextBufferReadResult bufferResult;
            try
            {
                bufferResult = _nativeApi.ReadWindowText(
                    request.WindowHandle,
                    _buffer);
            }
            catch (Exception exception) when (WindowsCaptureTargetVerifier
                .IsRecoverableNativeReadException(exception))
            {
                bufferResult = WindowsCaptureWindowTextBufferReadResult.Unknown;
            }
            catch (Exception exception)
            {
                Array.Clear(_buffer);
                FaultRequest(request, exception);
                return;
            }

            if (!TryClaimCompletion(request))
            {
                return;
            }

            string value;
            try
            {
                value = BuildValue(bufferResult);
            }
            catch (Exception exception) when (WindowsCaptureTargetVerifier
                .IsRecoverableNativeReadException(exception))
            {
                bufferResult = WindowsCaptureWindowTextBufferReadResult.Unknown;
                value = string.Empty;
            }
            catch (Exception exception)
            {
                Array.Clear(_buffer);
                FaultRequest(request, exception);
                return;
            }

            var resultState = NormalizeResultState(bufferResult, value);
            var resultValue = resultState
                is WindowsCaptureObservationReadState.Present
                ? value
                : string.Empty;
            Array.Clear(_buffer);

            lock (_sync)
            {
                if (!ReferenceEquals(_request, request)
                    || request.Attempt != _lastAttempt
                    || request.Status
                        is not WindowTitleReadRequestStatus.Completing
                    || _state
                        is not WindowsCaptureWindowTitleWorkerState.Completing
                    || _deadline.IsExpired(request.Deadline))
                {
                    ExpireActiveRequestUnderLock(request);
                    return;
                }

                request.ResultState = resultState;
                request.Value = resultValue;
                request.Status = WindowTitleReadRequestStatus.Completed;
                request.Completion.TrySetResult();
                _request = null;
                _state = WindowsCaptureWindowTitleWorkerState.Idle;
                request.Finished.TrySetResult();
                Monitor.PulseAll(_sync);
            }
        }
        finally
        {
            Array.Clear(_buffer);
        }
    }

    private bool TryClaimCompletion(WindowTitleReadRequest request)
    {
        lock (_sync)
        {
            if (!IsCurrentPendingRequestUnderLock(request)
                || _deadline.IsExpired(request.Deadline))
            {
                ExpireActiveRequestUnderLock(request);
                return false;
            }

            request.Status = WindowTitleReadRequestStatus.Completing;
            _state = WindowsCaptureWindowTitleWorkerState.Completing;
            return true;
        }
    }

    private void FaultRequest(
        WindowTitleReadRequest request,
        Exception exception)
    {
        var fatalException = ExceptionDispatchInfo.Capture(exception);
        lock (_sync)
        {
            _terminalFatalException ??= fatalException;
            if (ReferenceEquals(_request, request)
                && request.Status is (
                    WindowTitleReadRequestStatus.Pending
                    or WindowTitleReadRequestStatus.Completing))
            {
                request.FatalException = fatalException;
                request.Status = WindowTitleReadRequestStatus.Faulted;
                request.Finished.TrySetResult();
            }

            _request = null;
            _state = _disposed
                ? WindowsCaptureWindowTitleWorkerState.Stopping
                : WindowsCaptureWindowTitleWorkerState.Poisoned;
            Monitor.PulseAll(_sync);
        }
    }

    private void CompleteWorkerFailure(Exception exception)
    {
        Array.Clear(_buffer);
        var recoverable = WindowsCaptureTargetVerifier
            .IsRecoverableNativeReadException(exception);
        var fatalException = recoverable
            ? null
            : ExceptionDispatchInfo.Capture(exception);

        lock (_sync)
        {
            if (fatalException is not null)
            {
                _terminalFatalException ??= fatalException;
            }

            if (_request is { } request
                && request.Status is (
                    WindowTitleReadRequestStatus.Pending
                    or WindowTitleReadRequestStatus.Completing))
            {
                if (fatalException is not null)
                {
                    request.FatalException = fatalException;
                    request.Status = WindowTitleReadRequestStatus.Faulted;
                }
                else
                {
                    request.Status = WindowTitleReadRequestStatus.Expired;
                }

                request.Finished.TrySetResult();
            }

            _request = null;
            _state = _disposed
                ? WindowsCaptureWindowTitleWorkerState.Stopping
                : WindowsCaptureWindowTitleWorkerState.Poisoned;
            Monitor.PulseAll(_sync);
        }
    }

    private string BuildValue(
        WindowsCaptureWindowTextBufferReadResult bufferResult)
    {
        if (bufferResult.State is not WindowsCaptureObservationReadState.Present
            || bufferResult.CharactersCopied <= 0
            || bufferResult.CharactersCopied
                >= MaximumWindowTextCharacters - 1)
        {
            return string.Empty;
        }

        return _valueBuilder(_buffer, bufferResult.CharactersCopied);
    }

    private static WindowsCaptureObservationReadState NormalizeResultState(
        WindowsCaptureWindowTextBufferReadResult bufferResult,
        string value)
    {
        return bufferResult.State switch
        {
            WindowsCaptureObservationReadState.Absent
                when bufferResult.CharactersCopied == 0 =>
                WindowsCaptureObservationReadState.Absent,
            WindowsCaptureObservationReadState.Present
                when !string.IsNullOrEmpty(value) =>
                WindowsCaptureObservationReadState.Present,
            _ => WindowsCaptureObservationReadState.Unknown,
        };
    }

    private bool IsCurrentPendingRequestUnderLock(
        WindowTitleReadRequest request)
    {
        return ReferenceEquals(_request, request)
            && request.Attempt == _lastAttempt
            && request.Status is WindowTitleReadRequestStatus.Pending
            && _state is WindowsCaptureWindowTitleWorkerState.InFlight;
    }

    private void ExpireRequestUnderLock(WindowTitleReadRequest request)
    {
        if (request.Status is not (
            WindowTitleReadRequestStatus.Pending
            or WindowTitleReadRequestStatus.Completing))
        {
            return;
        }

        request.Status = WindowTitleReadRequestStatus.Expired;
        if (ReferenceEquals(_request, request))
        {
            if (_state is WindowsCaptureWindowTitleWorkerState.Queued)
            {
                _request = null;
                _state = WindowsCaptureWindowTitleWorkerState.Idle;
            }
            else if (_state is (
                WindowsCaptureWindowTitleWorkerState.InFlight
                or WindowsCaptureWindowTitleWorkerState.Completing))
            {
                _state = WindowsCaptureWindowTitleWorkerState.Poisoned;
            }
        }

        request.Finished.TrySetResult();
        Monitor.PulseAll(_sync);
    }

    private void ExpireActiveRequestUnderLock(
        WindowTitleReadRequest request)
    {
        ExpireRequestUnderLock(request);
    }

    private enum WindowTitleReadRequestStatus
    {
        Pending = 0,
        Completing = 1,
        Completed = 2,
        Expired = 3,
        Faulted = 4,
    }

    private sealed class WindowTitleReadRequest(
        ulong windowHandle,
        long attempt,
        long deadline)
    {
        internal ulong WindowHandle { get; } = windowHandle;

        internal long Attempt { get; } = attempt;

        internal long Deadline { get; } = deadline;

        internal TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Finished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal WindowTitleReadRequestStatus Status { get; set; }

        internal ExceptionDispatchInfo? FatalException { get; set; }

        internal WindowsCaptureObservationReadState ResultState { get; set; }

        internal string Value { get; set; } = string.Empty;
    }
}
