namespace WinDayFlow.Application.Analysis;

public enum AnalysisPipelineActivityState
{
    Idle = 0,
    Running = 1,
    Faulted = 2,
}

public enum AnalysisPipelineFaultCode
{
    PipelineRunFailed = 1,
    SchedulerFailed = 2,
}

public sealed record AnalysisPipelineStatus(
    long Sequence,
    long DataRevision,
    AnalysisPipelineActivityState State,
    DateTimeOffset ChangedAtUtc,
    AnalysisPipelineRunSummary? LastRunSummary,
    AnalysisPipelineFaultCode? FaultCode);

public sealed class AnalysisPipelineStatusChangedEventArgs : EventArgs
{
    public AnalysisPipelineStatusChangedEventArgs(
        AnalysisPipelineStatus previous,
        AnalysisPipelineStatus current)
    {
        Previous = previous ?? throw new ArgumentNullException(nameof(previous));
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public AnalysisPipelineStatus Previous { get; }

    public AnalysisPipelineStatus Current { get; }
}

public interface IAnalysisPipelineStatusSource
{
    AnalysisPipelineStatus Current { get; }

    event EventHandler<AnalysisPipelineStatusChangedEventArgs>? StatusChanged;
}

public sealed class AnalysisPipelineStatusSource : IAnalysisPipelineStatusSource
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private AnalysisPipelineStatus _current;

    public AnalysisPipelineStatusSource(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = new AnalysisPipelineStatus(
            Sequence: 0,
            DataRevision: 0,
            AnalysisPipelineActivityState.Idle,
            _timeProvider.GetUtcNow().ToUniversalTime(),
            LastRunSummary: null,
            FaultCode: null);
    }

    public AnalysisPipelineStatus Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event EventHandler<AnalysisPipelineStatusChangedEventArgs>? StatusChanged;

    internal void PublishRunning()
    {
        Publish(static (current, changedAtUtc) => current with
        {
            Sequence = checked(current.Sequence + 1),
            State = AnalysisPipelineActivityState.Running,
            ChangedAtUtc = changedAtUtc,
            FaultCode = null,
        });
    }

    internal void PublishIdle(AnalysisPipelineRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Publish((current, changedAtUtc) => current with
        {
            Sequence = checked(current.Sequence + 1),
            DataRevision = HasPersistedChanges(summary)
                ? checked(current.DataRevision + 1)
                : current.DataRevision,
            State = AnalysisPipelineActivityState.Idle,
            ChangedAtUtc = changedAtUtc,
            LastRunSummary = summary,
            FaultCode = null,
        });
    }

    internal void PublishFaulted(AnalysisPipelineFaultCode faultCode)
    {
        if (!Enum.IsDefined(faultCode))
        {
            throw new ArgumentOutOfRangeException(nameof(faultCode));
        }

        Publish((current, changedAtUtc) => current with
        {
            Sequence = checked(current.Sequence + 1),
            DataRevision = checked(current.DataRevision + 1),
            State = AnalysisPipelineActivityState.Faulted,
            ChangedAtUtc = changedAtUtc,
            FaultCode = faultCode,
        });
    }

    internal void PublishStopped()
    {
        Publish(static (current, changedAtUtc) => current with
        {
            Sequence = checked(current.Sequence + 1),
            State = AnalysisPipelineActivityState.Idle,
            ChangedAtUtc = changedAtUtc,
            FaultCode = null,
        });
    }

    private void Publish(
        Func<AnalysisPipelineStatus, DateTimeOffset, AnalysisPipelineStatus> update)
    {
        AnalysisPipelineStatus previous;
        AnalysisPipelineStatus current;
        EventHandler<AnalysisPipelineStatusChangedEventArgs>? handler;
        lock (_sync)
        {
            previous = _current;
            current = update(
                previous,
                _timeProvider.GetUtcNow().ToUniversalTime());
            _current = current;
            handler = StatusChanged;
        }

        if (handler is null)
        {
            return;
        }

        var eventArgs = new AnalysisPipelineStatusChangedEventArgs(previous, current);
        foreach (EventHandler<AnalysisPipelineStatusChangedEventArgs> subscriber
                 in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, eventArgs);
            }
            catch
            {
                // Observers must not interrupt the background analysis state machine.
            }
        }
    }

    private static bool HasPersistedChanges(AnalysisPipelineRunSummary summary)
    {
        return summary.RecoveredLeaseCount > 0
            || summary.Ingestion.CreatedChunkCount > 0
            || summary.Ingestion.CreatedJobCount > 0
            || summary.ProcessedJobCount > 0;
    }
}
